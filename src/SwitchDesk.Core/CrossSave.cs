using System.Security.Cryptography;
using System.Text.Json;

namespace SwitchDesk.Core;

public enum SavePlatform { Steam, Switch }
public enum MigrationFidelity { Full, PermanentProgressOnly, Unsupported }
public enum SaveTimeSource { FileModified, GameEmbedded, SaveCommit, ObservedOnly, MigratedSource }
public enum ComparisonState { FirstPairing, Unchanged, SteamChanged, SwitchChanged, Conflict }
public sealed record SaveReading(string Fingerprint, DateTimeOffset ObservedAt, DateTimeOffset? SavedAt,
    SaveTimeSource TimeSource, string Format, string Version, string Slot, string Summary, string? CommitId = null)
{
    public DateTimeOffset? MigratedAt { get; init; }
    public string TimeLabel => TimeSource switch { SaveTimeSource.MigratedSource => "来源的原始保存时间", SaveTimeSource.GameEmbedded => "游戏记录的保存时间", SaveTimeSource.FileModified => "有效存档文件修改时间", SaveTimeSource.SaveCommit => "存档文件系统提交时间", _ => "最近读取时间（保存时间不可用）" };
    public DateTimeOffset DisplayTime => SavedAt ?? ObservedAt;
}
public sealed record SaveBaseline(string SteamFingerprint, string SwitchFingerprint, DateTimeOffset MigratedAt);
public sealed record SavePair(string Id, string AdapterId, string SteamOwner, string PcPath, string PcSlot,
    string DeviceId, string Environment, string TitleId, string UserId, string SaveKey, string SwitchPath, string SwitchSlot,
    SaveBaseline? Baseline = null);
public sealed record SaveComparison(ComparisonState State, SavePlatform? RecommendedSource, string Explanation)
{
    public static SaveComparison Compare(SaveBaseline? baseline, SaveReading steam, SaveReading console)
    {
        if (baseline == null) return new(ComparisonState.FirstPairing, null, "首次配对，请指定保留哪一端的进度；时间不会触发自动覆盖");
        var pc = !Fingerprints.Equal(steam.Fingerprint, baseline.SteamFingerprint);
        var nx = !Fingerprints.Equal(console.Fingerprint, baseline.SwitchFingerprint);
        return (pc, nx) switch
        {
            (false, false) => new(ComparisonState.Unchanged, null, "双方内容未变化，无需重复迁移"),
            (true, false) => new(ComparisonState.SteamChanged, SavePlatform.Steam, "电脑进度已变化，可手动迁移到 Switch"),
            (false, true) => new(ComparisonState.SwitchChanged, SavePlatform.Switch, "Switch 进度已变化，可手动迁移到电脑"),
            _ => new(ComparisonState.Conflict, null, "双方均有变化，请比较进度后选择；两端快照都会保留")
        };
    }
}
public static class Fingerprints
{
    public static string Of(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value));
    public static bool Equal(string a, string b) => Valid(a) && Valid(b) && a.Equals(b, StringComparison.OrdinalIgnoreCase);
    public static bool Valid(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
public sealed record SlotInspection(string Format, string Version, string Summary, MigrationFidelity Fidelity)
{
    public DateTimeOffset? GameSavedAt { get; init; }
    public string[] DlcMarkers { get; init; } = [];
}
public sealed record ConvertedSlot(byte[] Bytes, MigrationFidelity Fidelity, string[] Effects);
public interface ISaveAdapter
{
    string Id { get; }
    uint SteamAppId { get; }
    string Name { get; }
    SlotInspection Inspect(ReadOnlySpan<byte> bytes, SavePlatform platform);
    ConvertedSlot Convert(ReadOnlySpan<byte> source, SavePlatform sourcePlatform, ReadOnlySpan<byte> targetTemplate);
}
public sealed record CompatibilityRule(string AdapterId, string SteamFormat, string SteamVersion, string SwitchFormat,
    string SwitchVersion, MigrationFidelity Fidelity, string[] SteamDlc, string[] SwitchDlc, string Evidence)
{
    public string SteamBuildId { get; init; } = "";
    public string SteamBranch { get; init; } = "";
    public string SwitchApplicationId { get; init; } = "";
    public uint? SwitchContentVersion { get; init; }
}
public sealed record GameVersionContext(string SteamBuildId, string SteamBranch, string SwitchApplicationId, uint? SwitchContentVersion);
/// <summary>Only shipped, reviewed game/version rules enable writes. A recognizable format is insufficient.</summary>
public sealed class CompatibilityCatalog(IReadOnlyList<CompatibilityRule> rules)
{
    public static CompatibilityCatalog Released { get; } = new([]);
    public CompatibilityRule? Find(string adapterId, SlotInspection pc, SlotInspection nx, IEnumerable<string> pcDlc, IEnumerable<string> nxDlc, GameVersionContext? versions = null)
        => rules.SingleOrDefault(r => r.AdapterId == adapterId && r.Fidelity != MigrationFidelity.Unsupported && !string.IsNullOrWhiteSpace(r.Evidence) &&
            versions != null && !string.IsNullOrWhiteSpace(versions.SteamBuildId) && !string.IsNullOrWhiteSpace(versions.SteamBranch) && ContentCatalog.ValidId(versions.SwitchApplicationId) && versions.SwitchContentVersion != null &&
            r.SteamBuildId == versions.SteamBuildId && r.SteamBranch == versions.SteamBranch && r.SwitchApplicationId.Equals(versions.SwitchApplicationId, StringComparison.OrdinalIgnoreCase) && r.SwitchContentVersion == versions.SwitchContentVersion &&
            r.SteamFormat == pc.Format && r.SteamVersion == pc.Version && r.SwitchFormat == nx.Format && r.SwitchVersion == nx.Version &&
            r.SteamDlc.Order(StringComparer.Ordinal).SequenceEqual(pcDlc.Order(StringComparer.Ordinal)) && r.SwitchDlc.Order(StringComparer.Ordinal).SequenceEqual(nxDlc.Order(StringComparer.Ordinal)));
}
public static class SaveAdapters
{
    public static IReadOnlyList<ISaveAdapter> All { get; } = [new VampireSaveAdapter(), new DeadCellsSaveAdapter(), new HadesSaveAdapter(false), new HadesSaveAdapter(true), new KnightSaveAdapter(false), new KnightSaveAdapter(true)];
    public static ISaveAdapter Get(string id) => All.Single(x => x.Id == id);
}
public sealed record StableSaveFile(string Path, byte[] Bytes, DateTimeOffset ModifiedAt, string Fingerprint)
{
    public const int MaximumBytes = 64 * 1024 * 1024;
    public static async Task<StableSaveFile> ReadAsync(string path, CancellationToken ct)
    {
        var full = System.IO.Path.GetFullPath(path); LocalSafety.DemandRegularFile(full);
        var before = new FileInfo(full); var modified = before.LastWriteTimeUtc; var length = before.Length;
        if (length is <= 0 or > MaximumBytes) throw new InvalidDataException("存档文件为空或超过 64 MiB 解析上限");
        // Deny concurrent Windows writers for the complete read, and detect rename/replacement too.
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var bytes = new byte[checked((int)length)]; await stream.ReadExactlyAsync(bytes, ct);
        var after = new FileInfo(full);
        if (stream.Length != length || after.Length != length || after.LastWriteTimeUtc != modified) throw new IOException("存档正在变化，请退出游戏后重试");
        return new(full, bytes, new DateTimeOffset(modified, TimeSpan.Zero), Fingerprints.Of(bytes));
    }
}
public static class LocalSafety
{
    public static void DemandDirectory(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("不通过链接或重解析路径写入缓存／恢复目录");
        if (!Directory.Exists(path)) throw new IOException("目标目录不存在");
    }
    public static void DemandRegularFile(string path)
    {
        var full = Path.GetFullPath(path);
        for (var current = full; current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("不通过链接或重解析路径操作存档／内容文件");
        if ((File.GetAttributes(full) & FileAttributes.Directory) != 0) throw new IOException("请选择存档文件");
    }
    public static void DemandJsonObject(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > StableSaveFile.MaximumBytes) throw new InvalidDataException("存档长度无效");
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
        var properties = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) properties.Push(new(StringComparer.OrdinalIgnoreCase));
            else if (reader.TokenType == JsonTokenType.EndObject) properties.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !properties.Peek().Add(reader.GetString()!)) throw new InvalidDataException("包含重复或仅大小写不同的 JSON 属性");
        }
        using var document = JsonDocument.Parse(bytes.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("存档应为 JSON 对象");
    }
}
