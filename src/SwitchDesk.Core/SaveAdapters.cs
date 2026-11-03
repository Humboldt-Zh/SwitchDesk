using System.Buffers.Binary;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SwitchDesk.Core;

/// <summary>Bounded codec for the published Knight string envelope. Does not invoke BinaryFormatter.</summary>
public sealed class KnightSaveAdapter(bool silksong) : ISaveAdapter
{
    public string Id => silksong ? "silksong" : "hollow-knight";
    public uint SteamAppId => silksong ? 1030300u : 367520u;
    public string Name => silksong ? "空洞骑士：丝之歌" : "空洞骑士";
    public override string ToString() => Name;
    // Public, game-specific format constant, not a console/account key. Provenance: docs/PHASE2.md.
    private static ReadOnlySpan<byte> FormatKey => "UKu52ePUBwetZ9wNX88o54dnfKRu0T1l"u8;
    private static ReadOnlySpan<byte> Header => [0, 1, 0, 0, 0, 255, 255, 255, 255, 1, 0, 0, 0, 0, 0, 0, 0, 6, 1, 0, 0, 0];
    public static byte[] DecodePc(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > StableSaveFile.MaximumBytes || !bytes.StartsWith(Header)) throw new InvalidDataException("不支持的 Knight 存档封装");
        var offset = Header.Length; uint length = 0; var ended = false;
        for (var i = 0; i < 5; i++)
        {
            if (offset >= bytes.Length) throw new InvalidDataException("存档长度前缀被截断");
            var value = bytes[offset++]; if (i == 4 && value > 7) throw new InvalidDataException("存档长度前缀溢出");
            length |= (uint)(value & 127) << (7 * i);
            if ((value & 128) == 0) { if (i > 0 && value == 0) throw new InvalidDataException("非规范长度前缀"); ended = true; break; }
        }
        if (!ended || length == 0 || length != bytes.Length - offset - 1 || bytes[^1] != 11 || length % 4 != 0) throw new InvalidDataException("存档长度或结尾无效");
        var encoded = bytes.Slice(offset, checked((int)length));
        foreach (var ch in encoded) if (!(ch is >= 65 and <= 90 or >= 97 and <= 122 or >= 48 and <= 57 or 43 or 47 or 61)) throw new InvalidDataException("存档 Base64 包含非法字符");
        byte[] cipher;
        try { cipher = System.Convert.FromBase64String(Encoding.ASCII.GetString(encoded)); }
        catch (FormatException e) { throw new InvalidDataException("存档 Base64 损坏", e); }
        if (cipher.Length == 0 || cipher.Length % 16 != 0) throw new InvalidDataException("存档密文长度无效");
        try { using var aes = Aes.Create(); aes.Key = FormatKey.ToArray(); return aes.DecryptEcb(cipher, PaddingMode.PKCS7); }
        catch (CryptographicException e) { throw new InvalidDataException("存档解密或填充校验失败", e); }
    }
    public static byte[] EncodePc(ReadOnlySpan<byte> json)
    {
        LocalSafety.DemandJsonObject(json); using var aes = Aes.Create(); aes.Key = FormatKey.ToArray();
        var encoded = Encoding.ASCII.GetBytes(System.Convert.ToBase64String(aes.EncryptEcb(json, PaddingMode.PKCS7)));
        using var output = new MemoryStream(); output.Write(Header); using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write7BitEncodedInt(encoded.Length); output.Write(encoded); output.WriteByte(11); return output.ToArray();
    }
    private static byte[] Plain(ReadOnlySpan<byte> bytes, SavePlatform platform)
    { var data = platform == SavePlatform.Steam ? DecodePc(bytes) : bytes.ToArray(); if (data.AsSpan().StartsWith(new byte[] { 239, 187, 191 })) data = data[3..]; LocalSafety.DemandJsonObject(data); return data; }
    public SlotInspection Inspect(ReadOnlySpan<byte> bytes, SavePlatform platform)
    {
        using var doc = JsonDocument.Parse(Plain(bytes, platform)); var obj = doc.RootElement;
        if (!obj.TryGetProperty("playerData", out var player) || player.ValueKind != JsonValueKind.Object || !obj.TryGetProperty("sceneData", out _)) throw new InvalidDataException("缺少 Knight 进度与场景数据");
        var version = player.TryGetProperty("version", out var v) ? v.ToString() : obj.TryGetProperty("version", out v) ? v.ToString() : "unknown";
        return new(platform == SavePlatform.Steam ? "knight-string-aes256-v1" : "knight-json-v1", version, "已解析角色与场景进度", MigrationFidelity.Full);
    }
    public ConvertedSlot Convert(ReadOnlySpan<byte> source, SavePlatform sourcePlatform, ReadOnlySpan<byte> targetTemplate)
    {
        var targetPlatform = sourcePlatform == SavePlatform.Steam ? SavePlatform.Switch : SavePlatform.Steam;
        var a = Inspect(source, sourcePlatform); var b = Inspect(targetTemplate, targetPlatform);
        if (a.Version == "unknown" || a.Version != b.Version) throw new InvalidDataException("Knight 游戏版本不同或不可识别");
        using var from = JsonDocument.Parse(Plain(source, sourcePlatform)); using var to = JsonDocument.Parse(Plain(targetTemplate, targetPlatform));
        using var output = new MemoryStream(); using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var property in to.RootElement.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                // Explicit ownership: progress and scene state only. Unknown target settings remain untouched.
                (property.Name is "playerData" or "sceneData" ? from.RootElement.GetProperty(property.Name) : property.Value).WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        var plain = output.ToArray(); var result = targetPlatform == SavePlatform.Steam ? EncodePc(plain) : plain;
        Inspect(result, targetPlatform); return new(result, MigrationFidelity.Full, ["仅替换所选槽位的角色与场景进度；目标其他顶层数据保留"]);
    }
}

/// <summary>Exact-byte checksum validation; never guesses JS/.NET numeric serialization.</summary>
public sealed class VampireSaveAdapter : ISaveAdapter
{
    public string Id => "vampire-survivors";
    public uint SteamAppId => 1794680;
    public string Name => "吸血鬼幸存者";
    public override string ToString() => Name;
    public SlotInspection Inspect(ReadOnlySpan<byte> bytes, SavePlatform platform)
    {
        LocalSafety.DemandJsonObject(bytes);
        var reader = new Utf8JsonReader(bytes); var start = -1; var end = -1; string? checksum = null;
        while (reader.Read()) if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals("checksum"u8))
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.String) throw new InvalidDataException("存档摘要不是字符串");
            start = checked((int)reader.TokenStartIndex); end = checked((int)reader.BytesConsumed); checksum = reader.GetString();
        }
        if (!Fingerprints.Valid(checksum) || start < 0) throw new InvalidDataException("缺少有效存档摘要");
        using var stream = new MemoryStream(); stream.Write(bytes[..start]); stream.Write("\"\""u8); stream.Write(bytes[end..]);
        if (!Fingerprints.Equal(Fingerprints.Of(stream.ToArray()), checksum!)) throw new InvalidDataException("存档摘要不匹配，或不是已识别的紧凑 JSON 序列化；保留原文件");
        using var doc = JsonDocument.Parse(bytes.ToArray()); var root = doc.RootElement;
        if (!root.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number) throw new InvalidDataException("缺少游戏存档版本");
        var coins = root.TryGetProperty("Coins", out var c) ? c.ToString() : "—";
        return new("vampire-checksummed-json-v1", version.GetRawText(), "金币 " + coins + " · 摘要校验通过", MigrationFidelity.Unsupported);
    }
    public ConvertedSlot Convert(ReadOnlySpan<byte> source, SavePlatform sourcePlatform, ReadOnlySpan<byte> targetTemplate)
        => throw new NotSupportedException("需要双端样本确认进度字段与平台设置边界；当前支持校验和独立备份");
}

public sealed class HadesSaveAdapter(bool second) : ISaveAdapter
{
    public string Id => second ? "hades-2" : "hades";
    public uint SteamAppId => second ? 1145350u : 1145360u;
    public string Name => second ? "哈迪斯 2" : "哈迪斯";
    public override string ToString() => Name;
    public SlotInspection Inspect(ReadOnlySpan<byte> bytes, SavePlatform platform)
    {
        if (bytes.Length is < 16 or > StableSaveFile.MaximumBytes) throw new InvalidDataException("Profile 存档长度无效");
        return new("hades-profile-unclassified", "unknown", "已读取 Profile；不执行其中脚本，等待格式与版本准入", MigrationFidelity.Unsupported);
    }
    public ConvertedSlot Convert(ReadOnlySpan<byte> source, SavePlatform sourcePlatform, ReadOnlySpan<byte> targetTemplate)
        => throw new NotSupportedException("此代 Hades 的 Profile 版本组合尚未准入");
}
public sealed class DeadCellsSaveAdapter : ISaveAdapter
{
    public string Id => "dead-cells";
    public uint SteamAppId => 588650;
    public string Name => "死亡细胞";
    public override string ToString() => Name;
    public SlotInspection Inspect(ReadOnlySpan<byte> bytes, SavePlatform platform)
    {
        var data = Parse(bytes); var version = data.Chunks.TryGetValue(7, out var raw) && raw.Length == 4 ? BinaryPrimitives.ReadSingleLittleEndian(raw).ToString("R", CultureInfo.InvariantCulture) : "unknown";
        DateTimeOffset? saved = null;
        if (data.Chunks.TryGetValue(3, out var time) && time.Length == 8)
        { var seconds = BinaryPrimitives.ReadDoubleLittleEndian(time); if (double.IsFinite(seconds) && seconds >= 946684800 && seconds < 4102444800) saved = DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)); }
        return new("dead-cells-zlib-v1", version, "摘要与分块校验通过；仅永久进度迁移，当前一局会重置", (data.Flags & 0x30) != 0 ? MigrationFidelity.Unsupported : MigrationFidelity.PermanentProgressOnly) { GameSavedAt = saved, DlcMarkers = data.Chunks.TryGetValue(8, out var mask) ? ["dead-cells-mask:" + BinaryPrimitives.ReadUInt32LittleEndian(mask).ToString("X8", CultureInfo.InvariantCulture)] : [] };
    }
    public ConvertedSlot Convert(ReadOnlySpan<byte> source, SavePlatform sourcePlatform, ReadOnlySpan<byte> targetTemplate)
    {
        var a = Inspect(source, sourcePlatform); var b = Inspect(targetTemplate, sourcePlatform == SavePlatform.Steam ? SavePlatform.Switch : SavePlatform.Steam);
        if (a.Fidelity == MigrationFidelity.Unsupported || b.Fidelity == MigrationFidelity.Unsupported || a.Version == "unknown" || a.Version != b.Version) throw new NotSupportedException("不转换 MOD、实验分支或不同版本的死亡细胞存档");
        var from = Parse(source); var target = Parse(targetTemplate);
        if (!from.Chunks.GetValueOrDefault(8, []).SequenceEqual(target.Chunks.GetValueOrDefault(8, []))) throw new InvalidDataException("死亡细胞存档的 DLC 标记不同");
        // Keep the target's header/version/DLC envelope; remove active-run and combined-run chunks.
        var flags = target.Flags & ~(2u | 4u); var chunks = new Dictionary<int, byte[]>(target.Chunks) { [0] = from.Chunks[0] };
        chunks.Remove(1); chunks.Remove(2); if (from.Chunks.TryGetValue(3, out var date)) { chunks[3] = date; flags |= 8; }
        using var payload = new MemoryStream();
        Span<byte> size = stackalloc byte[4];
        foreach (var pair in chunks.OrderBy(x => x.Key)) { BinaryPrimitives.WriteUInt32LittleEndian(size, checked((uint)pair.Value.Length)); payload.Write(size); payload.Write(pair.Value); }
        var header = target.Header.ToArray(); header.AsSpan(5, 20).Clear(); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(55), flags);
        using var output = new MemoryStream(); output.Write(header);
        using (var zipped = new ZLibStream(output, CompressionLevel.SmallestSize, true)) zipped.Write(payload.ToArray());
        var result = output.ToArray(); SHA1.HashData(result).CopyTo(result, 5); Parse(result);
        return new(result, MigrationFidelity.PermanentProgressOnly, ["仅替换永久进度", "当前一局重置", "目标版本、平台封装与 DLC 标记保留"]);
    }
    private sealed record Envelope(byte[] Header, uint Flags, Dictionary<int, byte[]> Chunks);
    private static Envelope Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 61 or > StableSaveFile.MaximumBytes || !bytes[..4].SequenceEqual(new byte[] { 0xDE, 0xAD, 0xCE, 0x11 }) || bytes[4] != 1) throw new InvalidDataException("死亡细胞存档头或格式版本不支持");
        var probe = bytes.ToArray(); probe.AsSpan(5, 20).Clear();
        // SHA-1 is mandated by this file format, not used as an authentication primitive.
        if (!SHA1.HashData(probe).AsSpan().SequenceEqual(bytes.Slice(5, 20))) throw new InvalidDataException("死亡细胞存档摘要校验失败");
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[55..]); if ((flags & ~0x1ffu) != 0 || (flags & 1) == 0) throw new InvalidDataException("死亡细胞存档标记不支持或缺少永久进度");
        using var input = new MemoryStream(bytes[59..].ToArray()); using var zlib = new ZLibStream(input, CompressionMode.Decompress); using var output = new MemoryStream();
        var buffer = new byte[65536]; int read;
        while ((read = zlib.Read(buffer)) != 0) { if (output.Length + read > StableSaveFile.MaximumBytes) throw new InvalidDataException("死亡细胞存档解压后超过预算"); output.Write(buffer, 0, read); }
        var payload = output.ToArray(); var offset = 0; var chunks = new Dictionary<int, byte[]>();
        foreach (var bit in new[] { 0, 1, 2, 3, 7, 8 })
        {
            if ((flags & (1u << bit)) == 0) continue;
            if (payload.Length - offset < 4) throw new InvalidDataException("死亡细胞分块长度缺失");
            var length = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset)); offset += 4;
            if (length > payload.Length - offset) throw new InvalidDataException("死亡细胞分块越界");
            if ((bit == 3 && length != 8) || (bit is 7 or 8 && length != 4)) throw new InvalidDataException("死亡细胞元数据块长度错误");
            chunks[bit] = payload.AsSpan(offset, (int)length).ToArray(); offset += (int)length;
        }
        if (offset != payload.Length || chunks[0].Length == 0) throw new InvalidDataException("死亡细胞存档包含未解析内容");
        return new(bytes[..59].ToArray(), flags, chunks);
    }
}
