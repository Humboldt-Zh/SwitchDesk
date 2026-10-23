using System.Security.Cryptography;
using System.Text.Json;

namespace SwitchDesk.Core;

public static class Transfers
{
    public static async Task UploadTreeAsync(DeviceClient client, string local, string remote,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        PathSafety.DemandWritable(remote);
        if (!Directory.Exists(local)) { await UploadAsync(client, local, remote, progress, ct); return; }
        async Task EnsureDirectory(string path)
        {
            var entry = await client.QueryAsync<JsonElement>("fs.stat", new { path }, ct);
            if (entry.GetProperty("exists").GetBoolean()) { if (!entry.GetProperty("isDirectory").GetBoolean()) throw new IOException("目标名称已被文件使用：" + path); }
            else await client.CallAsync("fs.mkdir", new { path }, ct);
        }
        await EnsureDirectory(remote);
        foreach (var entry in Directory.EnumerateFileSystemEntries(local, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            ct.ThrowIfCancellationRequested(); var relative = PathSafety.Relative(Path.GetRelativePath(local, entry).Replace('\\', '/'));
            var target = remote.TrimEnd('/') + '/' + relative;
            if (Directory.Exists(entry)) await EnsureDirectory(target); else await UploadAsync(client, entry, target, progress, ct);
        }
    }
    public static async Task DownloadTreeAsync(DeviceClient client, string remote, string local,
        IProgress<TransferProgress>? progress, CancellationToken ct, int depth = 0)
    {
        if (depth > 64) throw new IOException("目录层级过深");
        PathSafety.Remote(remote); if (File.Exists(local) || Directory.Exists(local)) throw new IOException("目标名称已存在：" + local);
        Directory.CreateDirectory(local);
        var entries = await client.QueryAsync<RemoteEntry[]>("fs.list", new { path = remote }, ct);
        foreach (var item in entries)
        {
            ct.ThrowIfCancellationRequested(); PathSafety.Relative(item.Name);
            if (item.Name.Contains('/')) throw new InvalidDataException("主机返回了无效文件名");
            var expected = remote.TrimEnd('/') + '/' + item.Name;
            if (item.Path != expected) throw new InvalidDataException("主机返回了目录之外的文件");
            var destination = PathSafety.Child(local, item.Name);
            if (item.IsDirectory) await DownloadTreeAsync(client, item.Path, destination, progress, ct, depth + 1);
            else await DownloadAsync(client, item.Path, destination, progress, ct);
        }
    }
    public static async Task ReadToAsync(DeviceClient client, string handle, string path, long size, Stream target,
        IncrementalHash? hash, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        if (size < 0) throw new InvalidDataException("无效的文件大小");
        long offset = 0;
        while (offset < size)
        {
            var count = (int)Math.Min(Wire.ChunkSize, size - offset);
            var reply = await client.CallAsync("read.chunk", new { handle, path, offset, length = count }, ct);
            var bytes = reply.Binary ?? throw new InvalidDataException("文件响应缺少数据");
            if (bytes.Length != count) throw new EndOfStreamException("主机文件长度在读取期间发生变化");
            await target.WriteAsync(bytes, ct); hash?.AppendData(bytes); offset += bytes.Length;
            progress?.Report(new(offset, size, path));
        }
    }
    public static async Task WriteFromAsync(DeviceClient client, string handle, string path, Stream source,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var buffer = new byte[Wire.ChunkSize]; long offset = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer, ct); if (count == 0) break;
            await client.CallAsync("write.chunk", new { handle, path, offset }, ct, upload: buffer.AsSpan(0, count).ToArray());
            offset += count; progress?.Report(new(offset, source.CanSeek ? source.Length : 0, path));
        }
    }
    public static async Task CloseAsync(DeviceClient client, string handle)
    {
        if (!client.IsBroken)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await client.CallAsync("handle.close", new { handle }, timeout.Token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }
    public static async Task DownloadAsync(DeviceClient client, string remotePath, string destination,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        PathSafety.Remote(remotePath);
        if (File.Exists(destination)) throw new IOException("目标文件已存在，请选择其他名称");
        var open = await client.QueryAsync<JsonElement>("fs.read.open", new { path = remotePath }, ct);
        var handle = open.GetProperty("handle").GetString()!; var size = open.GetProperty("size").GetInt64();
        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, Wire.ChunkSize, true))
            { await ReadToAsync(client, handle, "", size, file, null, progress, ct); await file.FlushAsync(ct); }
            File.Move(partial, destination);
        }
        finally { await CloseAsync(client, handle); if (File.Exists(partial)) File.Delete(partial); }
    }
    public static async Task UploadAsync(DeviceClient client, string sourcePath, string remotePath,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        PathSafety.DemandWritable(remotePath);
        await using var file = File.OpenRead(sourcePath);
        var open = await client.QueryAsync<JsonElement>("fs.write.open", new { path = remotePath, size = file.Length }, ct);
        var handle = open.GetProperty("handle").GetString()!;
        try
        {
            await WriteFromAsync(client, handle, "", file, progress, ct);
            await client.CallAsync("fs.write.commit", new { handle }, ct);
        }
        finally { await CloseAsync(client, handle); }
    }
    public static async Task ExportGameAsync(DeviceClient client, DeviceInfo device, GameTitle game,
        TitleComponent component, string directory, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"{PathSafety.SafeName(game.Name)} [{component.TitleId}][v{component.Version}][{component.Kind}].nsp");
        var sidecar = destination + ".switchdesk.json";
        if (File.Exists(destination) || File.Exists(sidecar)) throw new IOException("该组件的导出文件或清单已存在");
        var open = await client.QueryAsync<JsonElement>("titles.export.open", new { titleId = component.TitleId, component.Version, component.Storage }, ct);
        var handle = open.GetProperty("handle").GetString()!;
        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        var metaPartial = sidecar + "." + Guid.NewGuid().ToString("N") + ".partial";
        bool metadataPublished = false, contentPublished = false;
        try
        {
            if (File.Exists(destination)) throw new IOException("该组件的导出文件已存在");
            var size = open.GetProperty("size").GetInt64(); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, Wire.ChunkSize, true))
            { await ReadToAsync(client, handle, "", size, file, hash, progress, ct); await file.FlushAsync(ct); }
            var manifest = new GameManifest(1, device.DeviceId, device.Environment, game.TitleId, game.Name, component,
                Convert.ToHexString(hash.GetHashAndReset()), size, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(metaPartial, JsonSerializer.Serialize(manifest, Wire.Json), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(metaPartial, sidecar); metadataPublished = true;
            File.Move(partial, destination); contentPublished = true;
        }
        finally
        {
            await CloseAsync(client, handle);
            if (File.Exists(partial)) File.Delete(partial);
            if (File.Exists(metaPartial)) File.Delete(metaPartial);
            if (metadataPublished && !contentPublished) File.Delete(sidecar);
        }
    }
    public static async Task<PackageInfo> InspectAsync(DeviceClient client, string path, CancellationToken ct)
    {
        await using var source = File.OpenRead(path);
        return (await client.CallAsync("package.inspect", new { name = Path.GetFileName(path), size = source.Length }, ct, source: source))
            .Result.Deserialize<PackageInfo>(Wire.Json)!;
    }
    public static async Task InstallAsync(DeviceClient client, string path, string storage,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        await using var source = File.OpenRead(path);
        await client.CallAsync("titles.install", new { name = Path.GetFileName(path), size = source.Length, storage }, ct, source: source, progress: progress);
    }
}
