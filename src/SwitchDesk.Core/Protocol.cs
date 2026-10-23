using System.Buffers.Binary;
using System.Text.Json;

namespace SwitchDesk.Core;

public enum FrameKind : ushort { Request = 1, Response = 2, Binary = 3, ReadAt = 5, Progress = 6, Cancel = 7 }
public sealed record Frame(FrameKind Kind, byte[] Payload);
public sealed class FramedConnection(Stream stream) : IAsyncDisposable
{
    public async Task WriteAsync(FrameKind kind, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > Wire.MaxFrame) throw new InvalidDataException("消息超过协议上限");
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Wire.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), Wire.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)kind);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), (ulong)payload.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(payload, ct); await stream.FlushAsync(ct);
    }
    public async Task<Frame> ReadAsync(CancellationToken ct)
    {
        var header = new byte[16]; await stream.ReadExactlyAsync(header, ct);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Wire.Magic) throw new InvalidDataException("设备不是 SwitchDesk 配套程序");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) != Wire.Version) throw new DeviceException("ProtocolMismatch", "电脑与配套程序的协议版本不一致");
        var length = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
        if (length > Wire.MaxFrame) throw new InvalidDataException("无效的消息长度");
        var kind = (FrameKind)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        if (!Enum.IsDefined(kind)) throw new InvalidDataException("未知消息类型");
        var payload = new byte[(int)length]; await stream.ReadExactlyAsync(payload, ct); return new(kind, payload);
    }
    public ValueTask DisposeAsync() => stream.DisposeAsync();
}
public sealed record RpcReply(JsonElement Result, byte[]? Binary);
public sealed class DeviceClient(Stream transport) : IAsyncDisposable
{
    private readonly FramedConnection connection = new(transport);
    private readonly SemaphoreSlim gate = new(1);
    private long nextId;
    private bool broken;
    public bool IsBroken => broken;
    public async Task<RpcReply> CallAsync(string method, object? args = null, CancellationToken ct = default,
        byte[]? upload = null, Stream? source = null, IProgress<TransferProgress>? progress = null)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (broken) throw new IOException("连接已结束，请重新连接");
            var id = ++nextId;
            await connection.WriteAsync(FrameKind.Request, JsonSerializer.SerializeToUtf8Bytes(new { id, method, args = args ?? new { } }, Wire.Json), ct);
            if (upload != null) await connection.WriteAsync(FrameKind.Binary, upload, ct);
            while (true)
            {
                var frame = await connection.ReadAsync(ct);
                if (frame.Kind == FrameKind.Progress)
                {
                    progress?.Report(JsonSerializer.Deserialize<TransferProgress>(frame.Payload, Wire.Json)!); continue;
                }
                if (frame.Kind == FrameKind.ReadAt)
                {
                    if (source is null || !source.CanSeek) throw new InvalidDataException("设备意外请求源文件");
                    using var read = JsonDocument.Parse(frame.Payload);
                    var offset = read.RootElement.GetProperty("offset").GetInt64();
                    var count = read.RootElement.GetProperty("length").GetInt32();
                    if (offset < 0 || count < 0 || count > Wire.ChunkSize || offset > source.Length || count > source.Length - offset)
                        throw new InvalidDataException("设备请求超出源文件范围");
                    source.Position = offset; var data = new byte[count]; await source.ReadExactlyAsync(data, ct);
                    await connection.WriteAsync(FrameKind.Binary, data, ct); continue;
                }
                if (frame.Kind != FrameKind.Response) throw new InvalidDataException("响应类型错误");
                using var doc = JsonDocument.Parse(frame.Payload); var root = doc.RootElement;
                if (root.GetProperty("id").GetInt64() != id) throw new InvalidDataException("响应序号错误");
                if (!root.GetProperty("ok").GetBoolean())
                {
                    var error = root.GetProperty("error"); throw new DeviceException(error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!);
                }
                var result = root.GetProperty("result").Clone(); byte[]? body = null;
                if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("binaryLength", out var len))
                {
                    var binary = await connection.ReadAsync(ct);
                    if (binary.Kind != FrameKind.Binary || binary.Payload.LongLength != len.GetInt64()) throw new InvalidDataException("二进制响应长度错误");
                    body = binary.Payload;
                }
                return new(result, body);
            }
        }
        catch (DeviceException ex) when (ex.Code != "ProtocolMismatch") { throw; }
        catch
        {
            broken = true;
            try
            {
                using var abort = new CancellationTokenSource(500);
                await connection.WriteAsync(FrameKind.Cancel, ReadOnlyMemory<byte>.Empty, abort.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            await connection.DisposeAsync(); throw;
        }
        finally { gate.Release(); }
    }
    public async Task<T> QueryAsync<T>(string method, object? args = null, CancellationToken ct = default)
    {
        try
        {
            var result = (await CallAsync(method, args, ct)).Result.Deserialize<T>(Wire.Json) ?? throw new InvalidDataException("响应内容为空");
            if (result is DeviceInfo info && (string.IsNullOrWhiteSpace(info.DeviceId) || string.IsNullOrWhiteSpace(info.Environment) || info.Capabilities == null ||
                info.Model == null || info.Firmware == null || info.Atmosphere == null || info.Companion == null || info.UsbSpeed == null))
                throw new InvalidDataException("主机信息缺少必要字段，请重新连接并核对配套版本");
            if (result is GameTitle[] games && games.Any(g => g is not { TitleId: not null, Name: not null, State: not null, Components: not null } || g.Components.Any(c => c is not { TitleId: not null, Kind: not null, Storage: not null, Size: >= 0 })))
                throw new InvalidDataException("主机返回的游戏列表不完整");
            if (result is StorageInfo[] storages && storages.Any(s => s is not { Id: not null, Name: not null, TotalBytes: >= 0, FreeBytes: >= 0 } || s.FreeBytes > s.TotalBytes))
                throw new InvalidDataException("主机返回的容量信息无效");
            if (result is SaveInfo[] saves && saves.Any(s => s is not { Key: not null, TitleId: not null, UserId: not null, UserName: not null, GameName: not null, Environment: not null }))
                throw new InvalidDataException("主机返回的存档列表不完整");
            return result;
        }
        catch (JsonException ex) { throw new InvalidDataException("主机响应格式错误：" + method, ex); }
    }
    public async Task<DeviceInfo> HandshakeAsync(CancellationToken ct = default)
    {
        var hello = await QueryAsync<JsonElement>("hello", new { protocol = Wire.Version, client = BuildInfo.ClientId }, ct);
        if (hello.GetProperty("protocol").GetInt32() != Wire.Version) throw new DeviceException("ProtocolMismatch", "协议版本不兼容");
        return await QueryAsync<DeviceInfo>("device.info", ct: ct);
    }
    public async ValueTask DisposeAsync() { broken = true; await connection.DisposeAsync(); }
}
