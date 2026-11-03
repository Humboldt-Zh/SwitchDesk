using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text.Json;
using SwitchDesk.App;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;
internal static partial class Program
{
    private static int passed, failed;
    private static string root = "";
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--native-video-fixture") return RunNativeVideoFixture();
        root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "SwitchDesk-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Run().GetAwaiter().GetResult();
        Console.WriteLine($"RESULT {passed} passed, {failed} failed; fixtures: {root}"); return failed == 0 ? 0 : 1;
    }
    private static void Assert(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task Test(string name, Func<Task> action)
    { try { await action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); } }
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static async Task Run()
    {
        await RunUsbStreamingTestsAsync();
        await NativeVideoHostTests();
        await Phase2Tests();
        await ManagedUpdateTests();
        await UiV2Tests();
        await AuditRegressionTests();
        await Test("remote path canonicalization", () => Sync(() => Assert(PathSafety.Remote("//switch///games/") == "/switch/games")));
        foreach (var path in new[] { "/../secret", "/x/../Nintendo", "C:/test", "/x\\y", "/x\0y" })
            await Test("reject remote path " + path.Replace('\0', '?'), () => Throws<InvalidDataException>(() => Sync(() => PathSafety.Remote(path))));
        foreach (var path in new[] { "/Nintendo/Contents/a", "/EMUmmc/raw1", "/atmosphere/package3", "/switch/SwitchDesk/staging/x", "/" })
            await Test("protect " + path, () => Throws<DeviceException>(() => Sync(() => PathSafety.DemandWritable(path))));
        await Test("ordinary SD path allowed", () => Sync(() => PathSafety.DemandWritable("/switch/tools/config.ini")));
        await Test("backup traversal rejected", () => Throws<InvalidDataException>(() => Sync(() => PathSafety.Child(root, "../escape"))));
        await Test("local drive root is a valid download destination", () => Sync(() => Assert(PathSafety.Child(Path.GetPathRoot(root)!, "download.bin") == Path.Combine(Path.GetPathRoot(root)!, "download.bin"))));
        await Test("Windows USB interface discovery runs without a device", () => Sync(() => Assert(UsbDiscovery.Enumerate().All(c => !string.IsNullOrWhiteSpace(c.Path)))));
        await Test("wire golden header / 64-bit JSON", async () =>
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { size = 5L << 30 }, Wire.Json); using var buffer = new MemoryStream();
            var writer = new FramedConnection(buffer); await writer.WriteAsync(FrameKind.Request, bytes, default); var packet = buffer.ToArray();
            Assert(packet.AsSpan(0, 8).SequenceEqual(new byte[] { 0x53, 0x44, 0x4b, 0x31, 1, 0, 1, 0 })); Assert(BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(8)) == (ulong)bytes.Length);
            await using var reader = new FramedConnection(new FragmentedStream(packet)); var frame = await reader.ReadAsync(default); Assert(frame.Payload.SequenceEqual(bytes));
        });
        await Test("oversized frame rejected before allocation", async () =>
        { var header = Header(); BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), ulong.MaxValue); await using var connection = new FramedConnection(new MemoryStream(header)); await Throws<InvalidDataException>(async () => await connection.ReadAsync(default)); });
        await Test("version mismatch rejected", async () =>
        { var header = Header(); header[4] = 2; await using var connection = new FramedConnection(new MemoryStream(header)); await Throws<DeviceException>(async () => await connection.ReadAsync(default)); });
        await Test("truncated payload rejected", async () =>
        { var header = Header(); BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), 4); await using var connection = new FramedConnection(new MemoryStream(header)); await Throws<EndOfStreamException>(async () => await connection.ReadAsync(default)); });
        await Test("handshake and array responses", async () =>
        { await using var peer = new Peer(); await using var client = await peer.ConnectAsync(); Assert((await client.HandshakeAsync()).DeviceId == Peer.Info.DeviceId); var volumes = await client.QueryAsync<StorageInfo[]>("storages.list"); Assert(volumes[0].TotalBytes == 256L << 30); });
        await Test("device errors leave protocol synchronized", async () =>
        { await using var peer = new Peer(); await using var client = await peer.ConnectAsync(); await Throws<DeviceException>(async () => await client.CallAsync("error")); Assert(!client.IsBroken); Assert((await client.HandshakeAsync()).DeviceId == Peer.Info.DeviceId); });
        await Test("random access above 4 GiB", async () =>
        { await using var peer = new Peer(); await using var client = await peer.ConnectAsync(); using var source = new LargeStream(); var result = await client.CallAsync("large.read", source: source); Assert(result.Result.GetProperty("correct").GetBoolean()); });
        await Test("host source out-of-bounds rejected", async () =>
        { await using var peer = new Peer(); await using var client = await peer.ConnectAsync(); using var source = new LargeStream(); await Throws<InvalidDataException>(async () => await client.CallAsync("source.invalid", source: source)); Assert(client.IsBroken); });
        await Test("cancellation invalidates active connection", async () =>
        { await using var peer = new Peer(); await using var client = await peer.ConnectAsync(); using var ct = new CancellationTokenSource(100); await Throws<OperationCanceledException>(async () => await client.CallAsync("wait", ct: ct.Token)); Assert(client.IsBroken); });
        await Test("snapshot roundtrip, directories and deduplication", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync(); var store = new SnapshotStore(Path.Combine(root, "backups"));
            var first = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default); Assert(first.Manifest.Files.Length == 2); Assert(first.Manifest.Directories!.Contains("empty"));
            Assert((await SnapshotStore.VerifyAsync(first.Path)).Manifest.ContentHash == first.Manifest.ContentHash);
            var second = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default); Assert(second.Path == first.Path); Assert(store.List().Count == 1);
            peer.Files["main.dat"] = [9, 9, 9]; await store.RestoreAsync(client, Peer.Info, Peer.Save, first.Path, null, default);
            Assert(peer.Files["main.dat"].SequenceEqual(new byte[] { 1, 2, 3, 4, 5 })); Assert(peer.RestoreCommits == 1);
            Assert(Directory.EnumerateFiles(Path.Combine(root, "backups", "Recovery"), "*.zip", SearchOption.AllDirectories).Any());
        });
        await Test("restore mismatch performs no live reads or writes", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync(); var store = new SnapshotStore(Path.Combine(root, "mismatch"));
            var backup = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default); var reads = peer.ReadOpens;
            await Throws<DeviceException>(() => store.RestoreAsync(client, Peer.Info, Peer.Save with { UserId = "USER-B" }, backup.Path, null, default)); Assert(peer.WriteOperations == 0 && peer.ReadOpens == reads);
        });
        await Test("conflicting directory manifest never publishes a snapshot", async () =>
        {
            await using var peer = new Peer { DirectoryConflict = true }; await using var client = await peer.ConnectAsync(); var store = new SnapshotStore(Path.Combine(root, "directory-conflict"));
            await Throws<InvalidDataException>(async () => await store.BackupAsync(client, Peer.Info, Peer.Save, null, default)); Assert(store.List().Count == 0 && peer.WriteOperations == 0);
        });
        await Test("corrupt duplicate cannot replace a fresh verified backup", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync(); var store = new SnapshotStore(Path.Combine(root, "bad-duplicate"));
            var first = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            using (var zip = ZipFile.Open(first.Path, ZipArchiveMode.Update)) { zip.GetEntry("data/main.dat")!.Delete(); using var data = zip.CreateEntry("data/main.dat").Open(); data.Write(new byte[] { 0, 0, 0, 0, 0 }); }
            var second = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default); Assert(second.Path != first.Path); await SnapshotStore.VerifyAsync(second.Path);
            store.Prune(1); Assert(File.Exists(second.Path));
        });
        await Test("folder upload/download preserves binary files and empty directories", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync(); var input = Path.Combine(root, "folder-input");
            Directory.CreateDirectory(Path.Combine(input, "empty")); Directory.CreateDirectory(Path.Combine(input, "nested"));
            var bytes = RandomNumberGenerator.GetBytes(Wire.ChunkSize + 19); await File.WriteAllBytesAsync(Path.Combine(input, "nested", "data.bin"), bytes);
            await Transfers.UploadTreeAsync(client, input, "/incoming/folder", null, default); Assert(peer.SdFiles["/incoming/folder/nested/data.bin"].SequenceEqual(bytes));
            var output = Path.Combine(root, "folder-output"); await Transfers.DownloadTreeAsync(client, "/incoming/folder", output, null, default);
            Assert((await File.ReadAllBytesAsync(Path.Combine(output, "nested", "data.bin"))).SequenceEqual(bytes)); Assert(Directory.Exists(Path.Combine(output, "empty")));
        });
        await Test("tampered snapshot blocked before writes", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync(); var store = new SnapshotStore(Path.Combine(root, "tamper"));
            var backup = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            using (var zip = ZipFile.Open(backup.Path, ZipArchiveMode.Update)) { zip.GetEntry("data/main.dat")!.Delete(); using var data = zip.CreateEntry("data/main.dat").Open(); data.Write(new byte[] { 0, 0, 0, 0, 0 }); }
            await Throws<InvalidDataException>(() => store.RestoreAsync(client, Peer.Info, Peer.Save, backup.Path, null, default)); Assert(peer.WriteOperations == 0);
        });
        await Test("interrupted download never publishes partial file", async () =>
        { await using var peer = new Peer { FailRead = true }; await using var client = await peer.ConnectAsync(); var target = Path.Combine(root, "interrupted.dat"); await Throws<DeviceException>(() => Transfers.DownloadAsync(client, "/test", target, null, default)); Assert(!File.Exists(target)); Assert(!Directory.GetFiles(root, "*.partial").Any()); });
        await Test("restore failure retains recovery and failure log", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync(); var dir = Path.Combine(root, "restore-failure"); var store = new SnapshotStore(dir);
            var backup = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default); peer.FailCommit = true;
            await Throws<DeviceException>(() => store.RestoreAsync(client, Peer.Info, Peer.Save, backup.Path, null, default));
            Assert(Directory.GetFiles(Path.Combine(dir, "Recovery"), "*.restore.json", SearchOption.AllDirectories).Length == 1); Assert(store.List().Count == 2);
        });
        await Test("retention is scoped to target and preserves recovery", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync(); var store = new SnapshotStore(Path.Combine(root, "retention"));
            for (var i = 0; i < 4; i++) { peer.Files["main.dat"] = [(byte)i]; await store.BackupAsync(client, Peer.Info, Peer.Save, null, default); }
            await store.BackupAsync(client, Peer.Info, Peer.Save, null, default, recovery: true); store.Prune(2); Assert(store.List().Count == 3);
        });
        await Test("SQLite Unicode, NULL and 64-bit size persistence", () => Sync(() =>
        {
            var path = Path.Combine(root, "library.db"); using (var db = new LibraryDatabase(path)) { db.Upsert(new("D:\\游戏\\测试.nsp", "中文游戏", null, "NSP", "待识别", 5L << 30, null, "待识别")); db.Upsert(new("D:\\游戏\\测试.nsp", "更新后的名称", "0100", "Base", "65536", 5L << 30, null, "已识别")); }
            using var reopened = new LibraryDatabase(path); var item = reopened.List().Single(); Assert(item.Name == "更新后的名称" && item.Size == 5L << 30 && item.Sha256 == null);
        }));
        await Test("settings atomic roundtrip", () => Sync(() =>
        { var store = new SettingsStore(Path.Combine(root, "settings")); var settings = store.Load() with { Retention = 20, AutoBackupKeys = ["x|emuMMC|save1"] }; store.Save(settings); Assert(store.Load().Retention == 20 && store.Load().AutoBackupKeys.SequenceEqual(settings.AutoBackupKeys)); }));
        await Test("same-size modified file invalidates cached library metadata", async () =>
        {
            var directory = Path.Combine(root, "library-scan"); Directory.CreateDirectory(directory); var path = Path.Combine(directory, "test.nsp");
            await File.WriteAllBytesAsync(path, [1, 2, 3]); using var db = new LibraryDatabase(Path.Combine(root, "scan.db")); await db.ScanAsync(directory, default);
            var original = db.List().Single(); db.Upsert(original with { Name = "old title", TitleId = "0100", Status = "已识别" });
            await File.WriteAllBytesAsync(path, [4, 5, 6]); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(3)); await db.ScanAsync(directory, default);
            Assert(db.List().Single().TitleId == null);
        });
        await Test("repeated hash mismatch never rewrites the expected digest", async () =>
        {
            var path = Path.Combine(root, "changed.nsp"); await File.WriteAllBytesAsync(path, [1, 2, 3]); using var db = new LibraryDatabase(Path.Combine(root, "hash.db"));
            var expected = Convert.ToHexString(SHA256.HashData(new byte[] { 9 })); var item = new LibraryItem(path, "test", "0100", "Base", "0", 3, expected, "待校验");
            var first = await db.VerifyAsync(item, default); var second = await db.VerifyAsync(first, default); Assert(second.Status == "校验不匹配" && second.Sha256 == expected);
        });
        await Test("malformed export sidecar does not abort a library scan", async () =>
        {
            var directory = Path.Combine(root, "invalid-sidecar"); Directory.CreateDirectory(directory); var path = Path.Combine(directory, "test.nsp"); await File.WriteAllBytesAsync(path, [1]);
            await File.WriteAllTextAsync(path + ".switchdesk.json", "{\"formatVersion\":1,\"size\":1}"); using var db = new LibraryDatabase(Path.Combine(root, "sidecar.db"));
            await db.ScanAsync(directory, default); Assert(db.List().Single().Status == "清单无效");
        });
        await Test("delayed snapshot stream seeks and reads", () => Sync(() =>
        {
            var path = Path.Combine(root, "stream.dat"); File.WriteAllBytes(path, [10, 20, 30, 40]); var called = false;
            var stream = new SnapshotStream(new(() => { called = true; return Task.FromResult(path); })); Assert(!called); var bytes = new byte[2]; stream.Seek(1, 0, 0); stream.Read(bytes, 2, 0); Assert(called && bytes.SequenceEqual(new byte[] { 20, 30 })); stream.Stat(out var stat, 1); Assert(stat.cbSize == 4);
        }));
    }
    private static byte[] Header() { var bytes = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, Wire.Magic); bytes[4] = 1; bytes[6] = 1; return bytes; }
    private sealed class FragmentedStream(byte[] data) : MemoryStream(data)
    { public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], ct); }
    private sealed class LargeStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => 5L << 30; public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) { count = (int)Math.Min(count, Length - Position); for (var i = 0; i < count; i++) buffer[offset + i] = (byte)((Position + i) % 251); Position += count; return count; }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => Position = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? Position + offset : Length + offset;
        public override void SetLength(long size) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
