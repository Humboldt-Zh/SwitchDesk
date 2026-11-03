using System.Globalization;
using System.Text.Json;

namespace SwitchDesk.Core;

public sealed record CatalogComponent(string ApplicationId, string ComponentId, string Kind, uint Version)
{
    public string MetadataSource { get; init; } = "TitleDB";
    public DateTimeOffset? RetrievedAt { get; init; }
}
public sealed record SourceCatalog(string SourceId, string Name, DateTimeOffset FetchedAt, ContentIndex Index);
public sealed record CatalogSnapshot(string Source, DateTimeOffset FetchedAt, DateTimeOffset? SourceModifiedAt, CatalogComponent[] Components)
{
    public string? Endpoint { get; init; }
    public bool NeedsRefresh(DateTimeOffset now) => now - FetchedAt >= TimeSpan.FromHours(24) || now < FetchedAt;
}
public enum UpdateStatus { Unknown, CatalogVersionInstalled, UpdateAvailable, NewDlc, DlcUpdate, InstalledAhead }
public sealed record UpdateOffer(CatalogComponent Component, uint? InstalledVersion, UpdateStatus Status)
{
    public string Label => Status switch { UpdateStatus.NewDlc => "新 DLC", UpdateStatus.DlcUpdate => "DLC 更新", UpdateStatus.UpdateAvailable => "游戏更新", UpdateStatus.CatalogVersionInstalled => "已达到目录版本", UpdateStatus.InstalledAhead => "主机版本高于目录", _ => "目录暂无记录" };
}
public static class ContentCatalog
{
    public static CatalogSnapshot Combine(CatalogSnapshot catalog, IEnumerable<SourceCatalog> sources)
    {
        var all = catalog.Components.Concat(sources.SelectMany(s => s.Index.Entries.Where(e => e.Kind != "Base").Select(e =>
            new CatalogComponent(e.ApplicationId, e.ComponentId, e.Kind, e.Version) { MetadataSource = s.Name + " · 自有索引", RetrievedAt = s.FetchedAt })));
        var merged = all.GroupBy(c => (c.ApplicationId.ToUpperInvariant(), c.ComponentId.ToUpperInvariant(), c.Kind)).Select(g => g.OrderByDescending(c => c.Version).First()).ToArray();
        return catalog with { Components = merged };
    }
    public static bool ValidId(string? id) => id is { Length: 16 } && id.All(Uri.IsHexDigit);
    public static async Task<CatalogSnapshot> ReadTitleDbAsync(Stream input, DateTimeOffset fetched, DateTimeOffset? modified, CancellationToken ct)
    {
        using var bounded = new LimitReadStream(input, 32L * 1024 * 1024);
        using var reader = new StreamReader(bounded, leaveOpen: true);
        if (await reader.ReadLineAsync(ct) != "id|rightsId|version") throw new InvalidDataException("TitleDB 目录表头不兼容");
        var components = new Dictionary<string, CatalogComponent>(StringComparer.OrdinalIgnoreCase); var count = 0;
        while (await reader.ReadLineAsync(ct) is {} line)
        {
            if (++count > 200000 || line.Length > 256) throw new InvalidDataException("目录超过解析预算");
            var fields = line.Split('|'); if (fields.Length != 3 || !ValidId(fields[0])) throw new InvalidDataException("目录行格式无效");
            if (fields[2].Length == 0) continue; // Unknown never becomes version zero / 'latest'.
            if (!uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var version)) throw new InvalidDataException("目录数字版本无效");
            var id = ulong.Parse(fields[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture); var suffix = id & 0x1fff;
            if (suffix == 0) continue; // Base revisions alone do not announce a downloadable update.
            var kind = suffix == 0x800 ? "Update" : suffix > 0x1000 ? "DLC" : "Unknown";
            if (kind == "Unknown") continue;
            var component = new CatalogComponent((id & ~0x1fffUL).ToString("X16", CultureInfo.InvariantCulture), fields[0].ToUpperInvariant(), kind, version);
            if (!components.TryGetValue(component.ComponentId, out var old) || old.Version < version) components[component.ComponentId] = component;
        }
        return new("TitleDB versions.txt", fetched, modified, components.Values.ToArray());
    }
    public static UpdateOffer[] Compare(GameTitle game, CatalogSnapshot catalog)
    {
        var offers = catalog.Components.Where(c => c.ApplicationId.Equals(game.TitleId, StringComparison.OrdinalIgnoreCase)).Select(c =>
        {
            var versions = game.Components.Where(i => i.TitleId.Equals(c.ComponentId, StringComparison.OrdinalIgnoreCase)).Select(i => (uint?)i.Version).ToArray();
            var installed = versions.Length == 0 ? null : versions.Max();
            var status = installed == null ? c.Kind == "DataPatch" ? UpdateStatus.DlcUpdate : c.Kind == "DLC" ? UpdateStatus.NewDlc : UpdateStatus.UpdateAvailable : installed < c.Version ? c.Kind is "DLC" or "DataPatch" ? UpdateStatus.DlcUpdate : UpdateStatus.UpdateAvailable : installed > c.Version ? UpdateStatus.InstalledAhead : UpdateStatus.CatalogVersionInstalled;
            return new UpdateOffer(c, installed, status);
        }).ToArray();
        return offers.Length == 0 ? [new(new(game.TitleId, game.TitleId, "Unknown", 0), null, UpdateStatus.Unknown)] : offers;
    }
}
public enum ContentSourceKind { Local, Http }
public sealed record ContentSource(string Id, string Name, ContentSourceKind Kind, string Root, string? CredentialReference = null);
public sealed record ContentEntry(string ApplicationId, string ComponentId, string Kind, uint Version, long Size, string Sha256, string Location);
public sealed record ContentIndex(int FormatVersion, ContentEntry[] Entries);
public static class ContentSources
{
    public static ContentIndex Parse(ReadOnlySpan<byte> json, ContentSource source)
    {
        if (json.Length > 16 * 1024 * 1024) throw new InvalidDataException("内容索引超过 16 MiB");
        LocalSafety.DemandJsonObject(json); var index = JsonSerializer.Deserialize<ContentIndex>(json, Wire.Json) ?? throw new InvalidDataException("索引为空");
        if (index.FormatVersion != 1 || index.Entries == null || index.Entries.Length > 100000) throw new InvalidDataException("内容索引版本或条目数不支持");
        using var document = JsonDocument.Parse(json.ToArray());
        if (!document.RootElement.TryGetProperty("entries", out var rows) || rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("索引必须包含 entries 数组");
        foreach (var row in rows.EnumerateArray())
            if (!row.TryGetProperty("version", out var version) || !version.TryGetUInt32(out _)) throw new InvalidDataException("索引必须明确提供组件数字版本，不能省略为默认零");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in index.Entries)
        {
            if (entry == null || !ContentCatalog.ValidId(entry.ApplicationId) || !ContentCatalog.ValidId(entry.ComponentId) || entry.Kind is not ("Base" or "Update" or "DLC" or "DataPatch") || entry.Size <= 0 || !Fingerprints.Valid(entry.Sha256) || string.IsNullOrWhiteSpace(entry.Location)) throw new InvalidDataException("内容索引字段不完整");
            if (!keys.Add(entry.ComponentId + ":" + entry.Version)) throw new InvalidDataException("内容索引包含重复组件版本");
            Resolve(source, entry.Location);
        }
        return index;
    }
    public static string Resolve(ContentSource source, string location)
    {
        if (source.Kind is not (ContentSourceKind.Local or ContentSourceKind.Http)) throw new InvalidDataException("来源类型不支持");
        if (string.IsNullOrWhiteSpace(location) || location.Contains('\0') || location.Contains('\\')) throw new InvalidDataException("内容相对路径无效");
        var decoded = Uri.UnescapeDataString(location);
        if (decoded.Split('/', '\\').Any(x => x is "." or "..") || decoded.Contains('\\') || decoded.Contains('%')) throw new InvalidDataException("内容位置不能穿越来源目录");
        if (source.Kind == ContentSourceKind.Local)
        {
            if (Path.IsPathRooted(location) || location.Contains(':')) throw new InvalidDataException("本地内容位置必须在来源目录内");
            return PathSafety.Child(source.Root, PathSafety.Relative(location));
        }
        if (!Uri.TryCreate(source.Root, UriKind.Absolute, out var root) || root.Scheme is not ("http" or "https") || root.UserInfo.Length > 0 || root.Query.Length > 0 || root.Fragment.Length > 0 || !root.AbsolutePath.EndsWith('/')) throw new InvalidDataException("HTTP 来源应为不含凭据和查询的目录 URL，以 / 结尾");
        if (!Uri.TryCreate(root, location, out var result) || result.Scheme != root.Scheme || result.Host != root.Host || result.Port != root.Port || result.UserInfo.Length > 0 || result.Fragment.Length > 0 || !result.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal)) throw new InvalidDataException("下载地址超出配置的来源范围");
        return result.AbsoluteUri;
    }
    public static void ValidateInspected(ContentEntry selected, PackageInfo package)
    {
        if (!package.Recognized || package.Components.Length != 1) throw new InvalidDataException("管理更新只接受能精确对应一个所选组件的包；混合包请拆分后建立索引");
        var component = package.Components[0];
        if (!selected.ApplicationId.Equals(component.ApplicationId ?? package.TitleId, StringComparison.OrdinalIgnoreCase) || !selected.ComponentId.Equals(component.TitleId, StringComparison.OrdinalIgnoreCase) || selected.Version != component.Version || selected.Kind != component.Kind) throw new InvalidDataException("安装包真实内容与索引不一致，拒绝安装");
        if (component.RequiredSystemVersion == null || component.RequiredApplicationVersion == null) throw new DeviceException("CapabilityMissing", "需要支持内容依赖检查的新版配套程序");
    }
}
internal sealed class LimitReadStream(Stream source, long limit) : Stream
{
    private long consumed;
    public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => consumed; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) { var read = source.Read(buffer, offset, (int)Math.Min(count, Math.Max(1, limit - consumed + 1))); Account(read); return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    { var read = await StreamReads.WithInactivityTimeoutAsync(source, buffer[..(int)Math.Min(buffer.Length, Math.Max(1, limit - consumed + 1))], ct); Account(read); return read; }
    private void Account(int count) { consumed += count; if (consumed > limit) throw new InvalidDataException("目录响应超过 32 MiB 解析预算"); }
    public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
