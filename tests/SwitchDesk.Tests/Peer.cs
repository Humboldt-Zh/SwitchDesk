using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal sealed class Peer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private Task? worker;
    private int handleId;
    public readonly Dictionary<string, byte[]> Files = new() { ["main.dat"] = [1, 2, 3, 4, 5], ["nested/config.bin"] = new byte[Wire.ChunkSize + 17] };
    private readonly Dictionary<string, string> handles = [];
    private readonly Dictionary<string, MemoryStream> staged = [];
    private readonly Dictionary<string, string> sdPaths = [];
    public readonly Dictionary<string, byte[]> SdFiles = [];
    public readonly HashSet<string> SdDirectories = ["/", "/incoming"];
    public int WriteOperations, RestoreCommits, ReadOpens;
    public bool FailRead, FailCommit, DirectoryConflict;
    public bool IgnoreClose, MalformedDeviceInfo;
    public DeviceInfo? DeviceOverride;
    public string? RestoreFingerprint, RestoreOperation;
    public PackageInfo? PackageOverride { get; set; }
    public Dictionary<string, PackageInfo> Packages { get; } = [];
    public Dictionary<string, string> OperationStates { get; } = [];
    public bool SkipManagedInstall { get; set; }
    public int ManagedInstallRequests { get; private set; }
    public int CloseOperations, ExportOpens;
    public int ActiveHandleCount => handles.Count;
    public Action? OnRestoreBegin;
    public string? FailInstallation;
    public long FreeBytes = 120L << 30;
    public GameTitle[] Titles = [];
    public List<string> InstalledNames { get; } = [];
    public static readonly DeviceInfo Info = new("TEST-DEVICE", "协议测试设备", "测试固件", "测试 AMS", "0.1.0", "emuMMC", "协议模拟", ["files", "titles", "saves", "inspect", "install", "export"], true);
    public static readonly SaveInfo Save = new("save-1", "0100000000000000", "测试游戏", "USER-A", "测试用户", "Account", 32 * 1024 * 1024, "emuMMC");
    public async Task<DeviceClient> ConnectAsync()
    {
        listener.Start(); worker = ServeAsync(); var socket = new TcpClient(); await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint); return new(socket.GetStream());
    }
    private async Task ServeAsync()
    {
        try
        {
            using var socket = await listener.AcceptTcpClientAsync(stop.Token); await using var connection = new FramedConnection(socket.GetStream());
            while (!stop.IsCancellationRequested)
            {
                var request = await connection.ReadAsync(stop.Token); if (request.Kind == FrameKind.Cancel) break;
                using var doc = JsonDocument.Parse(request.Payload); var root = doc.RootElement;
                var id = root.GetProperty("id").GetInt64(); var method = root.GetProperty("method").GetString()!; var args = root.GetProperty("args");
                string Text(string name) => args.GetProperty(name).GetString()!;
                object? result = new { }; byte[]? binary = null; string? error = null;
                switch (method)
                {
                    case "hello": result = new { protocol = 1 }; break;
                    case "device.info": result = MalformedDeviceInfo ? new { model = "bad fixture" } : DeviceOverride ?? Info; break;
                    case "saves.inspect": result = new SaveDeviceInspection(Fingerprints.Of(JsonSerializer.SerializeToUtf8Bytes(Files, Wire.Json)), null, SaveTimeSource.ObservedOnly, "fixture-commit"); break;
                    case "operations.status": result = new { state = OperationStates.GetValueOrDefault(Text("operationId"), "unknown") }; break;
                    case "storages.list": result = new[] { new StorageInfo("SdCard", "SD", 256L << 30, FreeBytes) }; break;
                    case "titles.list": result = Titles; break;
                    case "package.inspect": result = Packages.GetValueOrDefault(Text("name")) ?? PackageOverride ?? new PackageInfo(Text("name"), 5L << 30, [new("0100000000000000", "Base", 0, "SdCard", 5L << 30)], "0100000000000000", "测试游戏", true); break;
                    case "titles.install.plan":
                        ManagedInstallRequests++;
                        OperationStates[Text("operationId")] = "verify-required";
                        if (!SkipManagedInstall && (Packages.GetValueOrDefault(Text("name")) ?? PackageOverride) is {} package)
                            Titles = Titles.Select(g => g.TitleId == package.TitleId ? g with { Components = g.Components.Where(c => !package.Components.Any(p => p.TitleId == c.TitleId)).Concat(package.Components.Select(c => c with { Storage = Text("storage") })).ToArray() } : g).ToArray();
                        break;
                    case "titles.install": if (Text("name") == FailInstallation) error = "InstallInterrupted"; else InstalledNames.Add(Text("name")); break;
                    case "saves.list": result = new[] { Save }; break;
                    case "fs.stat": result = new { exists = SdFiles.ContainsKey(Text("path")) || SdDirectories.Contains(Text("path")), isDirectory = SdDirectories.Contains(Text("path")), size = SdFiles.GetValueOrDefault(Text("path"))?.Length ?? 0 }; break;
                    case "fs.mkdir": SdDirectories.Add(Text("path")); break;
                    case "fs.list":
                        var prefix = Text("path").TrimEnd('/') + '/';
                        result = SdFiles.Where(f => f.Key.StartsWith(prefix) && !f.Key[prefix.Length..].Contains('/')).Select(f => new RemoteEntry(f.Key[prefix.Length..], f.Key, false, f.Value.Length))
                            .Concat(SdDirectories.Where(p => p.StartsWith(prefix) && p.Length > prefix.Length && !p[prefix.Length..].Contains('/')).Select(p => new RemoteEntry(p[prefix.Length..], p, true, 0))).ToArray(); break;
                    case "fs.write.open":
                        var upload = (++handleId).ToString(); handles[upload] = "upload"; sdPaths[upload] = Text("path"); staged[upload] = new(); result = new { handle = upload }; break;
                    case "fs.write.commit": SdFiles[sdPaths[Text("handle")]] = staged[Text("handle")].ToArray(); break;
                    case "fs.read.open":
                        if (SdFiles.TryGetValue(Text("path"), out var sdData)) { var sd = (++handleId).ToString(); handles[sd] = "sdfile"; sdPaths[sd] = Text("path"); result = new { handle = sd, size = sdData.Length }; break; }
                        goto case "saves.read.open";
                    case "saves.read.open":
                        ReadOpens++; var h = (++handleId).ToString(); handles[h] = method == "fs.read.open" ? "file" : "save"; result = new { handle = h, size = Files["main.dat"].Length }; break;
                    case "handle.tree": result = Files.Select(f => new RemoteEntry(f.Key, f.Key, false, f.Value.Length)).Append(new(DirectoryConflict ? "main.dat" : "empty", DirectoryConflict ? "main.dat" : "empty", true, 0)).ToArray(); break;
                    case "read.chunk":
                        var name = Text("path"); if (name == "") name = "main.dat";
                        if (FailRead) { error = "ReadInterrupted"; break; }
                        var body = handles[Text("handle")] == "sdfile" ? SdFiles[sdPaths[Text("handle")]] : Files[name];
                        binary = body.AsSpan((int)args.GetProperty("offset").GetInt64(), args.GetProperty("length").GetInt32()).ToArray(); result = new { binaryLength = binary.Length }; break;
                    case "titles.export.open":
                        ExportOpens++; var exported = (++handleId).ToString(); handles[exported] = "export"; result = new { handle = exported, size = Files["main.dat"].Length }; break;
                    case "handle.close": CloseOperations++; if (IgnoreClose) await Task.Delay(Timeout.Infinite, stop.Token); handles.Remove(Text("handle")); break;
                    case "saves.restore.begin":
                        RestoreFingerprint = args.TryGetProperty("expectedFingerprint", out var fp) ? fp.GetString() : null;
                        RestoreOperation = args.TryGetProperty("operationId", out var op) ? op.GetString() : null;
                        WriteOperations++; var restore = (++handleId).ToString(); handles[restore] = "restore"; result = new { handle = restore }; staged.Clear(); OnRestoreBegin?.Invoke(); break;
                    case "saves.restore.file": staged[Text("path")] = new(); break;
                    case "write.chunk":
                        var chunk = await connection.ReadAsync(stop.Token); if (chunk.Kind != FrameKind.Binary) throw new InvalidDataException();
                        WriteOperations++; await staged[handles[Text("handle")] == "upload" ? Text("handle") : Text("path")].WriteAsync(chunk.Payload, stop.Token); break;
                    case "saves.restore.commit":
                        if (FailCommit) { error = "RestoreInterrupted"; break; }
                        foreach (var file in staged) Files[file.Key] = file.Value.ToArray(); RestoreCommits++; break;
                    case "large.read":
                        var range = new { offset = (4L << 30) + 17, length = 31 };
                        await connection.WriteAsync(FrameKind.ReadAt, JsonSerializer.SerializeToUtf8Bytes(range, Wire.Json), stop.Token);
                        var data = await connection.ReadAsync(stop.Token);
                        if (!data.Payload.SequenceEqual(Enumerable.Range(0, range.length).Select(i => (byte)((range.offset + i) % 251)))) throw new InvalidDataException("Large source offset was truncated");
                        result = new { offset = range.offset, correct = true }; break;
                    case "source.invalid":
                        await connection.WriteAsync(FrameKind.ReadAt, JsonSerializer.SerializeToUtf8Bytes(new { offset = -1, length = 2 }), stop.Token); await connection.ReadAsync(stop.Token); break;
                    case "wait": await Task.Delay(Timeout.Infinite, stop.Token); break;
                    case "error": error = "ExpectedError"; break;
                    default: error = "UnknownMethod"; break;
                }
                var response = error is null ? JsonSerializer.SerializeToUtf8Bytes(new { id, ok = true, result }, Wire.Json) :
                    JsonSerializer.SerializeToUtf8Bytes(new { id, ok = false, error = new { code = error, message = error } }, Wire.Json);
                await connection.WriteAsync(FrameKind.Response, response, stop.Token);
                if (binary != null && error == null) await connection.WriteAsync(FrameKind.Binary, binary, stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); if (worker != null) await worker; foreach (var s in staged.Values) s.Dispose(); stop.Dispose(); }
}
