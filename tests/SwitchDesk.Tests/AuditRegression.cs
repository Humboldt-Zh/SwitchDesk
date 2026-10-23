using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchDesk.App;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;
internal static partial class Program
{
    private static async Task AuditRegressionTests()
    {
        var audit = Path.Combine(root, "audit"); Directory.CreateDirectory(audit);
        await Test("corrupt settings are preserved and startup falls back with automatic backups disabled", () => Sync(() =>
        {
            var directory = Path.Combine(audit, "settings-broken"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "settings.json"), "{broken");
            var store = new SettingsStore(directory); var settings = store.Load();
            Assert(settings.AutoBackupKeys.Length == 0 && store.RecoveryMessage.Length != 0);
            Assert(Directory.GetFiles(directory, "settings.json.invalid-*").Length == 1);
        }));
        await Test("legacy nullable backup policy is normalized without losing configured directories", () => Sync(() =>
        {
            var directory = Path.Combine(audit, "settings-null"); Directory.CreateDirectory(directory);
            var settings = AppSettings.Default(directory) with { Retention = 17, AutoBackupKeys = null! };
            File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(settings, Wire.Json));
            var loaded = new SettingsStore(directory).Load(); Assert(loaded.Retention == 17 && loaded.AutoBackupKeys.Length == 0);
        }));
        await Test("failed backup directory creation never opens a live save handle", async () =>
        {
            var path = Path.Combine(audit, "not-a-directory"); await File.WriteAllTextAsync(path, "test");
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            await Throws<IOException>(() => new SnapshotStore(path).BackupAsync(client, Peer.Info, Peer.Save, null, default));
            Assert(peer.ReadOpens == 0 && peer.ActiveHandleCount == 0);
        });
        await Test("backup rejects stale system identity before any save read", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            await Throws<DeviceException>(() => new SnapshotStore(Path.Combine(audit, "wrong-env")).BackupAsync(client, Peer.Info, Peer.Save with { Environment = "sysMMC" }, null, default));
            Assert(peer.ReadOpens == 0 && peer.WriteOperations == 0);
        });
        await Test("restore log creation failure still closes staging handle and retains recovery", async () =>
        {
            var directory = Path.Combine(audit, "log-failure"); var store = new SnapshotStore(directory);
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var snapshot = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            peer.OnRestoreBegin = () => Directory.CreateDirectory(Directory.GetFiles(Path.Combine(directory, "Recovery"), "*.zip", SearchOption.AllDirectories).Single() + ".restore.json");
            await Throws<UnauthorizedAccessException>(() => store.RestoreAsync(client, Peer.Info, Peer.Save, snapshot.Path, null, default));
            Assert(peer.ActiveHandleCount == 0 && peer.RestoreCommits == 0);
            Assert(Directory.GetFiles(Path.Combine(directory, "Recovery"), "*.zip", SearchOption.AllDirectories).Length == 1);
        });
        await Test("failed restore records failure and gives caller the verified recovery location", async () =>
        {
            var directory = Path.Combine(audit, "restore-failure"); var store = new SnapshotStore(directory);
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var snapshot = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default); peer.FailCommit = true;
            try { await store.RestoreAsync(client, Peer.Info, Peer.Save, snapshot.Path, null, default); throw new Exception("Expected restore failure"); }
            catch (DeviceException ex)
            {
                var recovery = (string)ex.Data["SwitchDesk.RecoveryPath"]!; await SnapshotStore.VerifyAsync(recovery);
                var log = JsonNode.Parse(await File.ReadAllTextAsync(recovery + ".restore.json")); Assert(log!["state"]!.GetValue<string>() == "failed");
            }
        });
        await Test("retention never deletes manually archived snapshots outside managed Snapshots", async () =>
        {
            var directory = Path.Combine(audit, "prune-scope"); var store = new SnapshotStore(directory);
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var old = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            var archive = Path.Combine(directory, "Archive", "keep.zip"); Directory.CreateDirectory(Path.GetDirectoryName(archive)!); File.Copy(old.Path, archive);
            peer.Files["main.dat"] = [9, 8, 7]; await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            store.Prune(1); Assert(File.Exists(archive) && store.List().Count == 2);
        });
        await Test("snapshot with missing identity is rejected and omitted from history", async () =>
        {
            var directory = Path.Combine(audit, "bad-identity"); var store = new SnapshotStore(directory);
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var snapshot = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            using (var zip = ZipFile.Open(snapshot.Path, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("manifest.json")!; JsonNode node; using (var reader = new StreamReader(entry.Open())) node = JsonNode.Parse(reader.ReadToEnd())!;
                node["titleId"] = null; entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()); writer.Write(node.ToJsonString());
            }
            await Throws<InvalidDataException>(() => SnapshotStore.VerifyAsync(snapshot.Path)); Assert(store.List().Count == 0);
        });
        await Test("cache with null game entry does not poison the latest valid inventory", async () =>
        {
            var directory = Path.Combine(audit, "cache"); var cache = new DeviceCache(directory);
            var good = new CachedConsole(Peer.Info, [], [], [], DateTimeOffset.UtcNow); await cache.SaveAsync(good);
            var broken = Path.Combine(directory, "bad"); Directory.CreateDirectory(broken);
            await File.WriteAllTextAsync(Path.Combine(broken, "inventory.json"), JsonSerializer.Serialize(good with { Games = [null!], SyncedAt = good.SyncedAt.AddDays(1) }, Wire.Json));
            Assert(cache.Latest()!.SyncedAt == good.SyncedAt);
        });
        foreach (var name in new[] { "NUL", "CON.txt", "COM1.log", "name.", "name " })
            await Test("Windows download rejects aliased or device name " + name, () => Throws<InvalidDataException>(() => Sync(() => PathSafety.Child(audit, name))));
        await Test("safe filename handles dot-only, reserved and overlong names", () => Sync(() =>
        {
            Assert(PathSafety.SafeName("...") == "_" && PathSafety.SafeName("NUL") == "_NUL" && PathSafety.SafeName(new string('x', 300)).Length <= 120);
        }));
        await Test("existing export never removes another task's temporary manifest or opens remote content", async () =>
        {
            var directory = Path.Combine(audit, "export-collision"); Directory.CreateDirectory(directory);
            var game = new GameTitle(Peer.Save.TitleId, "Game", "", "1", "已安装", []);
            var component = new TitleComponent(Peer.Save.TitleId, "Base", 0, "SdCard", 5);
            var target = Path.Combine(directory, $"Game [{component.TitleId}][v0][Base].nsp");
            await File.WriteAllTextAsync(target, "keep"); var otherPartial = target + ".switchdesk.json.partial"; await File.WriteAllTextAsync(otherPartial, "other-task");
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            await Throws<IOException>(() => Transfers.ExportGameAsync(client, Peer.Info, game, component, directory, null, default));
            Assert(peer.ExportOpens == 0 && await File.ReadAllTextAsync(otherPartial) == "other-task");
        });
        await Test("game export publishes a matching NSP and manifest without temporary leftovers", async () =>
        {
            var directory = Path.Combine(audit, "export-ok");
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            await Transfers.ExportGameAsync(client, Peer.Info, new(Peer.Save.TitleId, "Game", "", "1", "已安装", []), new(Peer.Save.TitleId, "Base", 0, "SdCard", 5), directory, null, default);
            var file = Directory.GetFiles(directory, "*.nsp").Single(); var manifest = JsonSerializer.Deserialize<GameManifest>(await File.ReadAllTextAsync(file + ".switchdesk.json"), Wire.Json)!;
            Assert(manifest.Sha256 == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))) && !Directory.GetFiles(directory, "*.partial").Any());
        });
        await Test("unresponsive handle close times out and invalidates the unusable connection", async () =>
        {
            await using var peer = new Peer { IgnoreClose = true }; await using var client = await peer.ConnectAsync();
            var watch = Stopwatch.StartNew(); await Transfers.CloseAsync(client, "test"); watch.Stop();
            Assert(client.IsBroken && watch.Elapsed < TimeSpan.FromSeconds(6));
        });
        await Test("malformed device response is rejected with a clear protocol error", async () =>
        {
            await using var peer = new Peer { MalformedDeviceInfo = true }; await using var client = await peer.ConnectAsync();
            await Throws<InvalidDataException>(() => client.HandshakeAsync());
        });
        await Test("library card preserves incomplete-content status", () => Sync(() =>
        {
            var vm = new LibraryViewModel(); vm.Merge([new(Peer.Save.TitleId, "Game", "", "1", "仅更新/DLC · 缺少本体", [new(Peer.Save.TitleId, "DLC", 0, "SdCard", 10)])], []);
            Assert(vm.All.Single().Status.Contains("缺少本体"));
        }));
        await Test("driver diagnostics distinguish missing, disabled and healthy devices", () => Sync(() =>
        {
            Assert(UsbDiscovery.ProblemMessage(28)!.Contains("WinUSB") && UsbDiscovery.ProblemMessage(22)!.Contains("禁用") && UsbDiscovery.ProblemMessage(0) == null);
            _ = UsbDiscovery.DiagnoseMissingInterface();
        }));
        await Test("cancel requested by commit-stage notification never starts live restore", async () =>
        {
            var store = new SnapshotStore(Path.Combine(audit, "commit-cancel"));
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var backup = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            using var cancel = new CancellationTokenSource();
            await Throws<OperationCanceledException>(() => store.RestoreAsync(client, Peer.Info, Peer.Save, backup.Path,
                new CallbackProgress(p => { if (p.Stage.Contains("写回存档")) cancel.Cancel(); }), cancel.Token));
            Assert(peer.RestoreCommits == 0 && peer.ActiveHandleCount == 0);
        });
        await Test("virtual file copy reports a short destination write instead of success", () => Sync(() =>
        {
            var path = Path.Combine(audit, "drag-short.dat"); File.WriteAllBytes(path, [1, 2, 3, 4]);
            var stream = new SnapshotStream(new(() => Task.FromResult(path)));
            try { stream.CopyTo(new ShortDestination(), 4, 0, 0); throw new Exception("Expected short write failure"); }
            catch (COMException ex) { Assert(ex.HResult == unchecked((int)0x80030070)); }
        }));
    }
    private sealed class CallbackProgress(Action<TransferProgress> callback) : IProgress<TransferProgress> { public void Report(TransferProgress value) => callback(value); }
    private sealed class ShortDestination : IStream
    {
        public void Write(byte[] bytes, int count, nint written) { Marshal.WriteInt32(written, count / 2); }
        public void Read(byte[] b, int n, nint p) => throw new NotSupportedException();
        public void Seek(long o, int n, nint p) => throw new NotSupportedException();
        public void SetSize(long size) => throw new NotSupportedException();
        public void CopyTo(IStream s, long n, nint r, nint w) => throw new NotSupportedException();
        public void Commit(int flags) => throw new NotSupportedException();
        public void Revert() => throw new NotSupportedException();
        public void LockRegion(long a, long b, int c) => throw new NotSupportedException();
        public void UnlockRegion(long a, long b, int c) => throw new NotSupportedException();
        public void Stat(out STATSTG stat, int flags) => throw new NotSupportedException();
        public void Clone(out IStream clone) => throw new NotSupportedException();
    }
}
