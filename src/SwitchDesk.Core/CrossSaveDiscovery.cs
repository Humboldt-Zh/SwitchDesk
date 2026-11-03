using System.Text.Json;

namespace SwitchDesk.Core;

/// <summary>Discovery identities only. These entries never grant migration compatibility.</summary>
public sealed record CrossSaveGameDefinition(string AdapterId, IReadOnlyList<string> SwitchTitleIds, string IdentityEvidence)
{
    public ISaveAdapter Adapter => SaveAdapters.Get(AdapterId);
    public string Name => Adapter.Name;
    public bool HasSwitchIdentity => SwitchTitleIds.Count != 0;
    public bool MatchesTitle(string? titleId) => titleId != null && SwitchTitleIds.Contains(titleId, StringComparer.OrdinalIgnoreCase);
}

public static class CrossSaveGames
{
    // Confirmed against the local device's read-only title inventory on 2026-11-02.
    // No name matching, assumed region variants, or user-created arbitrary pairings.
    // The remaining adapters stay visible until their Switch identities are verified.
    public static IReadOnlyList<CrossSaveGameDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        new CrossSaveGameDefinition("vampire-survivors", Array.AsReadOnly(new[] { "010089A0197E4000" }), "2026-11-02 · 主机游戏清单只读核对"),
        new CrossSaveGameDefinition("dead-cells", Array.AsReadOnly(new[] { "0100646009FBE000" }), "2026-11-02 · 主机游戏清单只读核对"),
        new CrossSaveGameDefinition("hades", Array.Empty<string>(), "Switch 游戏标识待核对"),
        new CrossSaveGameDefinition("hades-2", Array.Empty<string>(), "Switch 游戏标识待核对"),
        new CrossSaveGameDefinition("hollow-knight", Array.Empty<string>(), "Switch 游戏标识待核对"),
        new CrossSaveGameDefinition("silksong", Array.Empty<string>(), "Switch 游戏标识待核对")
    });

    public static CrossSaveGameDefinition? ForTitle(string? titleId) => All.SingleOrDefault(x => x.MatchesTitle(titleId));
    public static CrossSaveGameDefinition Get(string adapterId) => All.Single(x => x.AdapterId == adapterId);
}

public sealed record CrossSaveFile(string Path, long Size)
{
    public bool CanCompare => Size is > 0 and <= StableSaveFile.MaximumBytes;
    public string Label => Path + " · " + Display.Size(Size) + (Size == 0 ? " · 空文件" : Size > StableSaveFile.MaximumBytes ? " · 超过 64 MiB 解析上限" : "");
}

public static class CrossSaveDiscovery
{
    /// <summary>Lists metadata only; reads no save payload and performs no backup or write.</summary>
    public static async Task<CrossSaveFile[]> ListFilesAsync(DeviceClient client, DeviceInfo device, SaveInfo save, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!device.ApplicationMode || !device.Capabilities.Contains("saves"))
            throw new DeviceException("ApplicationMode", "读取 Switch 槽位需要连接主机，并以完整应用模式运行配套程序");
        if (save.Environment != device.Environment)
            throw new DeviceException("SaveMismatch", "存档的系统环境已变化，请重新读取主机资料");
        var open = await client.QueryAsync<JsonElement>("saves.read.open", new { key = save.Key, titleId = save.TitleId, userId = save.UserId, type = save.Type }, ct);
        if (open.ValueKind != JsonValueKind.Object || !open.TryGetProperty("handle", out var handleValue) || handleValue.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("主机没有返回有效存档读取句柄");
        var handle = handleValue.GetString();
        if (string.IsNullOrWhiteSpace(handle)) throw new InvalidDataException("主机没有返回有效存档读取句柄");
        try
        {
            var entries = await client.QueryAsync<RemoteEntry[]>("handle.tree", new { handle }, ct);
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var files = new List<CrossSaveFile>();
            foreach (var entry in entries)
            {
                if (entry is not { Path: not null, Size: >= 0 }) throw new InvalidDataException("主机返回的存档文件列表无效");
                var path = PathSafety.Relative(entry.Path);
                if (!paths.Add(path)) throw new InvalidDataException("存档目录包含重复文件或目录");
                if (!entry.IsDirectory) files.Add(new(path, entry.Size));
            }
            return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        }
        finally { await Transfers.CloseAsync(client, handle); }
    }
}
