using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using SwitchDesk.Core;

namespace SwitchDesk.App;

public sealed record SaveLibraryIdentity(string DeviceId, string Environment, string TitleId, string UserId, string SaveType);

public sealed record SaveLibrarySource(SaveLibraryIdentity Identity, string UserName, SaveInfo? Record,
    Snapshot[] Snapshots, bool IsCurrentDevice, bool Online, DateTimeOffset? SyncedAt, bool Connected = false)
{
    public string Label => $"{UserName}{(Identity.SaveType == "Device" ? " · 设备共享" : "")} · {Identity.Environment} · {DeviceText}";
    public string DeviceText => IsCurrentDevice ? "当前主机" : "主机 " + Identity.DeviceId[^Math.Min(6, Identity.DeviceId.Length)..];
    public string Status => Record == null ? "仅电脑备份" : Online ? "当前主机存档" : Connected ? "主机记录 · 当前模式不可读取" : "主机离线记录";
    public string IdentityText => $"{Identity.DeviceId} · {Identity.Environment} · {Identity.UserId} · {Identity.SaveType}";
    public DateTimeOffset LatestBackup => Snapshots.Select(s => s.Manifest.CreatedAt).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
    public override string ToString() => Label;
}

public sealed record SaveLibraryGame(string TitleId, string Name, SaveLibrarySource[] Sources)
{
    public int BackupCount => Sources.Sum(s => s.Snapshots.Length);
    public bool HasBackups => BackupCount > 0;
    public bool HasRecord => Sources.Any(s => s.Record != null);
    public string Initial => string.IsNullOrEmpty(Name) ? "◇" : Name[..1];
    public string CountText => $"{BackupCount} 个备份版本 · {Sources.Length} 个存档来源";
    public string Status => Sources.Any(s => s.Online && s.Record != null) ? "当前主机存档" : Sources.Any(s => s.Connected && s.Record != null) ? "主机记录 · 当前模式不可读取" : HasRecord ? "主机离线记录" : "仅电脑备份";
    public string LatestText => HasBackups ? $"最近备份 {Sources.Max(s => s.LatestBackup).ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "尚未备份";
}

public sealed record SaveLibraryVersion(Snapshot Snapshot, bool Recovery)
{
    public string TimeText => Snapshot.TimeText;
    public string SizeText => Snapshot.SizeText;
    public string KindText => Recovery ? "恢复前保护副本" : "普通备份";
}

public sealed class SaveLibraryViewModel : Observable
{
    private SaveLibraryGame[] all = [];
    private string search = "", sourceFilter = "全部";
    private SaveLibraryGame? selectedGame;
    private SaveLibrarySource? selectedSource;
    private Snapshot? selectedSnapshot;
    private SnapshotScanIssue[] issues = [];
    private bool rebuildingSources;
    private HashSet<string> recoveryPaths = new(StringComparer.OrdinalIgnoreCase);
    public SaveViewModel Detail { get; } = new();
    public ObservableCollection<SaveLibraryGame> Games { get; } = [];
    public ObservableCollection<SaveLibrarySource> Sources { get; } = [];
    public ObservableCollection<SaveLibraryVersion> Versions { get; } = [];
    public string[] SourceFilters { get; } = ["全部", "主机记录", "电脑备份"];
    public string Search { get => search; set { if (Set(ref search, value)) Filter(); } }
    public string SourceFilter { get => sourceFilter; set { if (Set(ref sourceFilter, value)) Filter(); } }
    public SaveLibraryGame? SelectedGame
    {
        get => selectedGame;
        set => OpenGame(value);
    }
    public bool TimelineOpen => SelectedGame != null;
    public SaveLibrarySource? SelectedSource
    {
        get => selectedSource;
        set { if (!rebuildingSources && Set(ref selectedSource, value)) UpdateDetail(); }
    }
    public Snapshot? SelectedSnapshot
    {
        get => selectedSnapshot;
        set { if (Set(ref selectedSnapshot, value)) CommandManager.InvalidateRequerySuggested(); }
    }
    public string CountText => $"{Games.Count} 个游戏 · {Games.Sum(g => g.BackupCount)} 个备份版本";
    public bool Empty => Games.Count == 0;
    public string EmptyHeading => all.Length == 0 ? "还没有存档记录" : "没有找到匹配的存档";
    public string EmptyDescription => all.Length == 0 ? "连接 Switch 读取主机存档，或在设置中选择已有备份目录。" : "试试其他名称，或清除存档页的搜索和来源筛选。";
    public bool HasIssues => issues.Length > 0;
    public string IssueSummary => HasIssues ? $"有 {issues.Length} 项备份文件或目录无法识别；其余备份仍可浏览。原文件均已保留。" : "";
    public string IssueDetails => string.Join("\n", issues.Select(i => $"{i.Path}：{i.Reason}"));
    public string SourceStatus => SelectedSource is not {} source ? "请选择存档来源" : source.Status +
        (source.Record != null && !source.Online && source.SyncedAt is {} time ? $" · 上次读取 {time.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "") +
        "；备份时间为电脑创建副本的时间。";
    public string HistoryEmptyText => SelectedSource?.Record != null ? "此来源尚无电脑备份。连接匹配主机并读取存档后可立即备份。" : "此来源没有可显示的备份。";
    public bool HistoryEmpty => Detail.History.Count == 0;
    public ICommand OpenGameCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public SaveLibraryViewModel()
    {
        OpenGameCommand = new Command(p => { if (p is SaveLibraryGame game) OpenGame(game); });
        BackCommand = new Command(_ => SelectedGame = null);
        ClearFiltersCommand = new Command(_ => { Search = ""; SourceFilter = "全部"; });
    }
    public void SetData(SaveInfo[] saves, SnapshotScanResult scan, DeviceInfo? info, bool online, DateTimeOffset? syncedAt)
    {
        var priorTitle = SelectedGame?.TitleId; var priorSource = SelectedSource?.Identity;
        issues = scan.Issues;
        recoveryPaths = (scan.RecoveryPaths ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var records = new Dictionary<SaveLibraryIdentity, SaveInfo>();
        if (info != null)
            foreach (var save in saves.Where(s => s.Environment == info.Environment))
                records[new(info.DeviceId, save.Environment, save.TitleId.ToUpperInvariant(), save.UserId, save.Type)] = save;
        var histories = scan.Snapshots.GroupBy(s => Identity(s.Manifest)).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Manifest.CreatedAt).ToArray());
        var sources = records.Keys.Concat(histories.Keys).Distinct().Select(id =>
        {
            records.TryGetValue(id, out var record); var history = histories.GetValueOrDefault(id) ?? [];
            var current = info != null && id.DeviceId == info.DeviceId && id.Environment == info.Environment;
            var live = current && online && record != null && info!.Capabilities.Contains("saves");
            return new SaveLibrarySource(id, record?.UserName ?? history.FirstOrDefault()?.Manifest.UserName ?? "未知用户", record, history, current, live, current ? syncedAt : null, current && online);
        }).ToArray();
        all = sources.GroupBy(s => s.Identity.TitleId).Select(g => new SaveLibraryGame(g.Key,
            g.Select(s => s.Record?.GameName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ??
            g.SelectMany(s => s.Snapshots).OrderByDescending(s => s.Manifest.CreatedAt).FirstOrDefault()?.Manifest.GameName ?? g.Key,
            g.OrderByDescending(s => s.IsCurrentDevice && s.Record != null).ThenByDescending(s => s.IsCurrentDevice).ThenByDescending(s => s.LatestBackup).ThenBy(s => s.Label).ToArray()))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        Filter();
        if (priorTitle != null && all.FirstOrDefault(g => g.TitleId == priorTitle) is {} selected) OpenGame(selected, priorSource);
        else if (priorTitle != null) SelectedGame = null;
        Changed(nameof(HasIssues)); Changed(nameof(IssueSummary)); Changed(nameof(IssueDetails));
    }
    private static SaveLibraryIdentity Identity(SaveManifest m) => new(m.DeviceId, m.Environment, m.TitleId.ToUpperInvariant(), m.UserId, m.SaveType);
    private void OpenGame(SaveLibraryGame? game, SaveLibraryIdentity? preferred = null)
    {
        var snapshotPath = SelectedSnapshot?.Path;
        rebuildingSources = true;
        try
        {
            if (Set(ref selectedGame, game, nameof(SelectedGame))) Changed(nameof(TimelineOpen));
            Sources.Clear(); if (game != null) foreach (var source in game.Sources) Sources.Add(source);
            // ComboBox clears its selection while ItemsSource is rebuilt. Do not
            // let that transient null discard the user's selected backup.
            selectedSource = Sources.FirstOrDefault(s => s.Identity == preferred) ?? Sources.FirstOrDefault(s => s.IsCurrentDevice && s.Record != null) ?? Sources.FirstOrDefault(s => s.IsCurrentDevice) ?? Sources.OrderByDescending(s => s.LatestBackup).FirstOrDefault();
            Changed(nameof(SelectedSource));
        }
        finally { rebuildingSources = false; }
        UpdateDetail(snapshotPath);
    }
    private void UpdateDetail(string? preferredPath = null)
    {
        var source = SelectedSource; var oldPath = preferredPath ?? SelectedSnapshot?.Path;
        Detail.SetData(source is { Online: true, Record: not null } ? [source.Record] : [], source?.Snapshots ?? []);
        Detail.SetContext(SelectedGame?.TitleId, null);
        Versions.Clear(); foreach (var snapshot in Detail.History) Versions.Add(new(snapshot, recoveryPaths.Contains(snapshot.Path)));
        SelectedSnapshot = Detail.History.FirstOrDefault(s => s.Path == oldPath);
        Changed(nameof(SourceStatus)); Changed(nameof(HistoryEmpty)); Changed(nameof(HistoryEmptyText));
        CommandManager.InvalidateRequerySuggested();
    }
    private void Filter()
    {
        Games.Clear();
        foreach (var game in all.Where(g => (SourceFilter != "主机记录" || g.HasRecord) && (SourceFilter != "电脑备份" || g.HasBackups) &&
            (string.IsNullOrWhiteSpace(Search) || g.Name.Contains(Search, StringComparison.CurrentCultureIgnoreCase) || g.TitleId.Contains(Search, StringComparison.OrdinalIgnoreCase)))) Games.Add(game);
        Changed(nameof(CountText)); Changed(nameof(Empty)); Changed(nameof(EmptyHeading)); Changed(nameof(EmptyDescription));
    }
}
