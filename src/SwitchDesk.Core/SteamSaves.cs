using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SwitchDesk.Core;

public sealed record PcSaveCandidate(string AdapterId, string Path, string Owner, string Slot, string LocationKind)
{
    public bool KnownLocation { get; init; }
    public string Label => $"{Owner} · 槽位 {Slot} · {LocationKind}";
    public override string ToString() => Label;
}
public sealed record SteamBuild(uint AppId, string BuildId, string Branch, string InstallDirectory);
public static partial class SteamSaves
{
    public static SteamBuild[] InstalledBuilds(string steamRoot)
    {
        var found = new List<SteamBuild>();
        foreach (var library in LibraryRoots(steamRoot)) foreach (var adapter in SaveAdapters.All)
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{adapter.SteamAppId}.acf");
            if (!File.Exists(manifest) || new FileInfo(manifest).Length > 1024 * 1024) continue;
            var text = File.ReadAllText(manifest);
            string Value(string key) => Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*\"([^\"\\r\\n]*)\"", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Groups[1].Value;
            var id = Value("appid"); var build = Value("buildid"); var install = Value("installdir"); var branch = Value("BetaKey");
            if (id != adapter.SteamAppId.ToString(System.Globalization.CultureInfo.InvariantCulture) || build.Length == 0 || !build.All(char.IsAsciiDigit) || build == "0" || string.IsNullOrWhiteSpace(install) || install.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || install is "." or "..") continue;
            found.Add(new(adapter.SteamAppId, build, branch.Length == 0 ? "public" : branch, Path.Combine(library, "steamapps", "common", install)));
        }
        return found.ToArray();
    }
    // Deliberately enumerates known live names, not the newest file or stale .bak/AppData copies.
    public static PcSaveCandidate[] Discover(string? steamRoot, string windowsProfile, string documents)
    {
        var found = new List<PcSaveCandidate>();
        if (!string.IsNullOrEmpty(steamRoot) && Directory.Exists(Path.Combine(steamRoot, "userdata")))
        {
            foreach (var account in Directory.EnumerateDirectories(Path.Combine(steamRoot, "userdata")))
            {
                var accountId = Path.GetFileName(account); if (!accountId.All(char.IsAsciiDigit)) continue;
                Add("vampire-survivors", Path.Combine(account, "1794680", "remote", "SaveData"), "Steam " + accountId, "1", "Steam remote");
                for (var i = 0; i < 7; i++) Add("dead-cells", Path.Combine(account, "588650", "remote", $"user_{i}.dat"), "Steam " + accountId, i.ToString(System.Globalization.CultureInfo.InvariantCulture), "Steam remote");
            }
        }
        var owner = "Windows " + Path.GetFileName(Path.TrimEndingDirectorySeparator(windowsProfile));
        foreach (var (id, directory) in new[] { ("hades", Path.Combine(documents, "Saved Games", "Hades")), ("hades-2", Path.Combine(windowsProfile, "Saved Games", "Hades II")), ("hades-2", Path.Combine(windowsProfile, "Saved Games", "HadesII")) })
            for (var i = 1; i <= 4; i++) Add(id, Path.Combine(directory, $"Profile{i}.sav"), owner, i.ToString(System.Globalization.CultureInfo.InvariantCulture), "Windows 共用目录");
        foreach (var (id, directory) in new[] { ("hollow-knight", "Hollow Knight"), ("silksong", "Hollow Knight Silksong") })
            for (var i = 1; i <= 4; i++) Add(id, Path.Combine(windowsProfile, "AppData", "LocalLow", "Team Cherry", directory, $"user{i}.dat"), owner, i.ToString(System.Globalization.CultureInfo.InvariantCulture), "Windows 共用目录");
        return found.DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        void Add(string id, string path, string account, string slot, string location)
        {
            if (!File.Exists(path)) return;
            try { LocalSafety.DemandRegularFile(path); found.Add(new(id, Path.GetFullPath(path), account, slot, location) { KnownLocation = true }); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
    public static bool SteamIsRunning() => IsRunning("steam");
    public static bool GameIsRunning(string adapterId)
    {
        string[] names = adapterId switch
        {
            "vampire-survivors" => ["VampireSurvivors", "VampireSurvivors64"], "dead-cells" => ["deadcells", "deadcells_gl"],
            "hades" => ["Hades", "Hades32", "Hades64"], "hades-2" => ["Hades2"], "hollow-knight" => ["hollow_knight"], "silksong" => ["Hollow Knight Silksong"], _ => []
        };
        return names.Any(IsRunning);
    }
    private static bool IsRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try { return processes.Length > 0; } finally { foreach (var process in processes) process.Dispose(); }
    }
    public static void DemandClosed(string adapterId, bool writingPc)
    {
        if (GameIsRunning(adapterId)) throw new IOException("请先退出目标游戏，避免内存中的进度覆盖迁移结果");
        if (writingPc && SteamIsRunning()) throw new IOException("请先退出 Steam，再写入电脑存档；SwitchDesk 不会自动关闭程序或修改云设置");
    }
    public static string[] LibraryRoots(string steamRoot)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(steamRoot) };
        var file = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(file) && new FileInfo(file).Length < 1024 * 1024)
            foreach (Match match in LibraryPath().Matches(File.ReadAllText(file)))
            {
                var value = match.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal).Replace("\\\"", "\"", StringComparison.Ordinal);
                if (Path.IsPathFullyQualified(value) && Directory.Exists(value)) roots.Add(Path.GetFullPath(value));
            }
        return roots.ToArray();
    }
    [GeneratedRegex("\"path\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex LibraryPath();
}
