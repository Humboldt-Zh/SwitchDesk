using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using SwitchDesk.Core;

namespace SwitchDesk.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
    public void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public sealed class Command(Action<object?> execute, Func<object?, bool>? enabled = null) : ICommand
{
    public bool CanExecute(object? parameter) => enabled?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(parameter); }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
public sealed record NavigationItem(string Id, string Glyph, string Label);
public sealed class ComponentItem(TitleComponent value) : Observable
{
    public TitleComponent Value { get; } = value;
    private bool selected = true;
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public string Name => Value.KindText;
    public string Detail => $"{Value.StorageText} · v{Value.Version}";
    public string SizeText => Display.Size(Value.Size);
}
public sealed class GameCard : Observable
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? TitleId { get; init; }
    public GameTitle? Console { get; init; }
    public LibraryItem[] Local { get; init; } = [];
    public bool OnConsole => Console != null;
    public bool OnComputer => Local.Length != 0;
    public bool Unknown => TitleId == null;
    public string Source => OnConsole && OnComputer ? "主机 + 电脑" : OnConsole ? "主机" : "电脑";
    public string Status => Unknown ? "待识别" : Console?.State ?? "电脑中";
    public string SizeText => Console?.SizeText ?? Display.Size(Local.Sum(x => x.Size));
    public string Initial => Unknown ? "◇" : Name[..Math.Min(1, Name.Length)];
    public string Publisher => Console?.Publisher ?? (Unknown ? "连接主机后可识别安装包" : "电脑游戏库");
    public string Summary => Console is {} g ? $"{g.SizeText} · {g.ComponentsText}" : string.Join(" / ", Local.Select(x => x.SizeText));
    public string Version => Console?.DisplayVersion ?? Local.FirstOrDefault()?.Version ?? "—";
    public ComponentItem[] Components { get; init; } = [];
    private ImageSource? icon; private bool selected; private double width = 188;
    public ImageSource? Icon { get => icon; set => Set(ref icon, value); }
    public bool IsSelected { get => selected; set => Set(ref selected, value); }
    public double ArtSize => Width - 8;
    public double Width { get => width; set { if (Set(ref width, value)) Changed(nameof(ArtSize)); } }
}
public sealed record GameRow(GameCard[] Cards);
public sealed class LibraryViewModel : Observable
{
    private GameCard[] all = [];
    public IReadOnlyList<GameCard> All => all;
    public ObservableCollection<GameCard> Filtered { get; } = [];
    public ObservableCollection<GameRow> Rows { get; } = [];
    private string search = "", source = "全部";
    private bool listView, savesOnly; private int columns = 3; private double available = 620;
    public HashSet<string> SaveTitles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool SavesOnly { get => savesOnly; set { if (Set(ref savesOnly, value)) Filter(); } }
    private GameCard? selected;
    public string Search { get => search; set { if (Set(ref search, value)) Filter(); } }
    public string Source { get => source; set { if (Set(ref source, value)) Filter(); } }
    public bool ListView { get => listView; set => Set(ref listView, value); }
    public GameCard? Selected { get => selected; set { if (selected != null) selected.IsSelected = false; if (Set(ref selected, value)) SelectionChanged?.Invoke(); if (selected != null) selected.IsSelected = true; } }
    public event Action? SelectionChanged;
    public string CountText => $"{Filtered.Count} 个游戏与文件";
    public bool HasEntries => all.Length > 0;
    public string EmptyHeading => all.Length == 0 ? "把游戏放在一起" : savesOnly && SaveTitles.Count == 0 ? "还没有存档记录" : "没有找到匹配的游戏";
    public string EmptyDescription => all.Length == 0 ? "用数据线连接 Switch，并打开配套程序。你也可以先添加电脑里的游戏目录。" : savesOnly && SaveTitles.Count == 0 ? "连接主机读取存档；已有的电脑快照也会显示在这里。" : "试试其他名称，或清除搜索和来源筛选。";
    public static string? NormalizeId(string? id) => id is { Length: 16 } && id.All(Uri.IsHexDigit) ? id.ToUpperInvariant() : null;
    public void Merge(IEnumerable<GameTitle> console, IEnumerable<LibraryItem> local)
    {
        var previous = all.ToDictionary(x => x.Key); var selectedKey = Selected?.Key;
        var games = console.ToArray();
        var components = games.SelectMany(g => g.Components.Select(c => (c.TitleId, Base: g.TitleId))).GroupBy(x => x.TitleId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Base, StringComparer.OrdinalIgnoreCase);
        string? BaseId(string? id) => NormalizeId(id) is {} valid ? components.GetValueOrDefault(valid, valid) : null;
        var groups = local.GroupBy(x => BaseId(x.TitleId) is { } id ? "title:" + id : "file:" + Path.GetFullPath(x.Path), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var merged = new List<GameCard>();
        foreach (var game in games)
        {
            var key = "title:" + game.TitleId.ToUpperInvariant(); groups.Remove(key, out var files);
            merged.Add(new() { Key = key, Name = game.Name, TitleId = game.TitleId, Console = game, Local = files ?? [], Components = game.Components.Select(x => new ComponentItem(x)).ToArray() });
        }
        foreach (var pair in groups)
        {
            var file = pair.Value[0]; merged.Add(new() { Key = pair.Key, Name = file.Name, TitleId = NormalizeId(file.TitleId), Local = pair.Value });
        }
        all = merged.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        foreach (var card in all) if (previous.TryGetValue(card.Key, out var old))
        {
            card.Icon = old.Icon;
            foreach (var component in card.Components)
                if (old.Components.FirstOrDefault(x => x.Value.TitleId == component.Value.TitleId && x.Value.Storage == component.Value.Storage) is {} existing) component.Selected = existing.Selected;
        }
        Filter(); Selected = all.FirstOrDefault(x => x.Key == selectedKey); Changed(nameof(All));
    }
    private void Filter()
    {
        var match = all.Where(x => (!savesOnly || x.TitleId != null && SaveTitles.Contains(x.TitleId)) && (source != "主机" || x.OnConsole) && (source != "电脑" || x.OnComputer) &&
            (string.IsNullOrWhiteSpace(search) || x.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) || (x.TitleId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))).ToArray();
        Filtered.Clear(); foreach (var item in match) Filtered.Add(item); RebuildRows(); Changed(nameof(CountText)); Changed(nameof(HasEntries)); Changed(nameof(EmptyHeading)); Changed(nameof(EmptyDescription));
    }
    public void Resize(double width)
    {
        var next = Math.Max(1, (int)((width + 16) / 196)); available = width;
        if (columns == next) { foreach (var card in Filtered) card.Width = Math.Clamp((available - (columns - 1) * 16) / columns, 180, 208); return; }
        columns = next; RebuildRows();
    }
    private void RebuildRows()
    {
        Rows.Clear(); var cardWidth = Math.Clamp((available - (columns - 1) * 16) / columns, 180, 208);
        foreach (var item in Filtered) item.Width = cardWidth;
        for (var i = 0; i < Filtered.Count; i += columns) Rows.Add(new(Filtered.Skip(i).Take(columns).ToArray()));
    }
}
public sealed class SaveViewModel : Observable
{
    public SaveInfo[] All { get; private set; } = [];
    public Snapshot[] Backups { get; private set; } = [];
    public ObservableCollection<SaveChoice> Users { get; } = [];
    public ObservableCollection<Snapshot> History { get; } = [];
    private SaveChoice? selected; private bool autoBackup; private bool updating;
    public bool HasLive => Selected?.Live != null;
    public SaveChoice? Selected { get => selected; set { if (Set(ref selected, value)) { UpdateHistory(); Changed(nameof(HasLive)); SelectionChanged?.Invoke(); } } }
    public bool AutoBackup { get => autoBackup; set { if (Set(ref autoBackup, value) && !updating) AutoChanged?.Invoke(value); } }
    public event Action? SelectionChanged;
    public event Action<bool>? AutoChanged;
    public string HistoryText => History.Count == 0 ? "还没有备份 · 立即备份或拖到电脑" : $"最近备份 {History[0].TimeText}";
    public void SetData(SaveInfo[] saves, Snapshot[] history) { All = saves; Backups = history; }
    public void SelectGame(string? titleId, DeviceInfo? info)
    {
        var key = Selected?.Key; Users.Clear();
        if (titleId != null)
        {
            foreach (var save in All.Where(s => s.TitleId.Equals(titleId, StringComparison.OrdinalIgnoreCase))) Users.Add(new(save.Key, save.UserName, save.Type, save));
            foreach (var snapshot in Backups.Where(s => s.Manifest.TitleId.Equals(titleId, StringComparison.OrdinalIgnoreCase) && (info == null || s.Manifest.DeviceId == info.DeviceId && s.Manifest.Environment == info.Environment)))
            {
                var m = snapshot.Manifest;
                if (!Users.Any(u => u.UserId == m.UserId && u.Type == m.SaveType)) Users.Add(new("history:" + m.UserId + ":" + m.SaveType, m.UserName, m.SaveType, null, m.UserId));
            }
        }
        Selected = Users.FirstOrDefault(x => x.Key == key) ?? Users.FirstOrDefault(); UpdateHistory();
    }
    private void UpdateHistory()
    {
        History.Clear(); if (Selected is {} user) foreach (var item in Backups.Where(x => x.Manifest.UserId == user.UserId && x.Manifest.SaveType == user.Type && x.Manifest.TitleId.Equals(currentTitle, StringComparison.OrdinalIgnoreCase) && (currentDevice == null || x.Manifest.DeviceId == currentDevice.DeviceId && x.Manifest.Environment == currentDevice.Environment))) History.Add(item);
        Changed(nameof(HistoryText));
    }
    private string currentTitle = ""; private DeviceInfo? currentDevice;
    public void SetContext(string? title, DeviceInfo? info) { currentTitle = title ?? ""; currentDevice = info; SelectGame(title, info); }
    public void SetAutomatic(bool value) { updating = true; AutoBackup = value; updating = false; }
}
public sealed record SaveChoice(string Key, string Name, string Type, SaveInfo? Live, string? HistoricalUser = null)
{
    public string UserId => Live?.UserId ?? HistoricalUser ?? "";
    public string Label => Name + (Type == "Device" ? " · 设备共享" : "") + (Live == null ? " · 仅历史" : "");
    public override string ToString() => Label;
}
public sealed class Job : Observable
{
    public required string Name { get; init; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
    public string TimeText => CreatedAt.ToLocalTime().ToString("HH:mm:ss");
    public CancellationTokenSource Cancellation { get; } = new();
    public Func<Task>? Retry { get; set; }
    private string state = "排队", detail = "等待执行", rate = ""; private double percent; private bool indeterminate = true, canCancel = true;
    public string State { get => state; set { if (Set(ref state, value)) { Changed(nameof(Active)); Changed(nameof(CanCancel)); Changed(nameof(CanRetry)); Changed(nameof(DiagnosticDetail)); Changed(nameof(HasDiagnosticDetail)); CommandManager.InvalidateRequerySuggested(); } } }
    public string Detail { get => detail; set { if (Set(ref detail, value)) { Changed(nameof(DiagnosticDetail)); Changed(nameof(HasDiagnosticDetail)); } } }
    public string Rate { get => rate; set => Set(ref rate, value); }
    public bool Active => State is "排队" or "准备" or "传输" or "主机处理";
    public bool CanRetry => State == "需要处理" && Retry != null;
    public string DiagnosticDetail => State == "完成" || Detail == State || Detail is "等待执行" or "准备中" or "已完成" ? "" : Detail;
    public bool HasDiagnosticDetail => DiagnosticDetail.Length != 0;
    public bool CanCancel { get => canCancel && Active; set { Set(ref canCancel, value); CommandManager.InvalidateRequerySuggested(); } }
    public bool Indeterminate { get => indeterminate; set { if (Set(ref indeterminate, value)) Changed(nameof(ProgressText)); } }
    public double Percent { get => percent; set { Set(ref percent, value); Changed(nameof(ProgressText)); } }
    public string ProgressText => Indeterminate ? State : (Active ? "当前阶段 " : "") + $"{Percent:0}%" + (Rate.Length > 0 ? " · " + Rate : "");
    public void Report(TransferProgress progress)
    {
        Detail = progress.Stage; Indeterminate = progress.Total <= 0; Percent = progress.Percent;
        var commit = progress.Stage.Contains("写回存档", StringComparison.Ordinal);
        State = commit ? "主机处理" : progress.Total > 0 ? "传输" : "准备";
        if (commit) { CanCancel = false; Detail = "正在写回存档 · 不可取消，请保持连接"; }
    }
}
public sealed class TasksViewModel : Observable
{
    public ObservableCollection<Job> Items { get; } = [];
    public bool HasItems => Items.Count != 0;
    public bool HasActions => Items.Any(x => x.CanCancel || x.CanRetry);
    public Job? Current => Items.FirstOrDefault(j => j.Active) ?? Items.FirstOrDefault();
    public string Summary => Current == null ? "任务已就绪" : $"{Current.Name} · {(Current.Active ? Current.Detail : Current.State)}";
    public string CountText => Items.Count(j => j.Active) is var n && n > 0 ? $"{n} 项进行中" : $"{Items.Count} 项历史";
    public void Add(Job job) { Items.Insert(0, job); job.PropertyChanged += (_, _) => Refresh(); Refresh(); }
    public void Refresh() { Changed(nameof(Current)); Changed(nameof(Summary)); Changed(nameof(CountText)); Changed(nameof(HasItems)); Changed(nameof(HasActions)); }
}
public sealed class ShellViewModel : Observable
{
    public LibraryViewModel Library { get; } = new();
    public SaveViewModel Saves { get; } = new();
    public TasksViewModel Tasks { get; } = new();
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<string> ConnectionEvents { get; } = [];
    private string lastConnectionEvent = "";
    public void RecordLog(string line, string message)
    {
        Logs.Add(line); while (Logs.Count > 500) Logs.RemoveAt(0);
        if (message.StartsWith("开始：", StringComparison.Ordinal) || message.StartsWith("完成：", StringComparison.Ordinal) ||
            message.StartsWith("失败：", StringComparison.Ordinal) || message.StartsWith("取消：", StringComparison.Ordinal) ||
            Tasks.Items.Any(x => x.State == "需要处理" && x.Detail == message) || message == lastConnectionEvent) return;
        lastConnectionEvent = message; ConnectionEvents.Add(line); while (ConnectionEvents.Count > 200) ConnectionEvents.RemoveAt(0);
    }
    public NavigationItem[] Navigation { get; } = [new("library", "\uE8F1", "游戏库"), new("saves", "\uE74E", "存档"), new("files", "\uE8B7", "文件"), new("console", "\uE7F4", "主机")];
    public Dictionary<string, ICommand> Commands { get; } = [];
    private string page = "library", connection = "等待连接", notice = "", noticePath = "", environment = "未连接", sync = "连接后自动同步", model = "我的 Switch";
    private bool online, detailOpen, compact, fixture, streamGuide; private DeviceInfo? device;
    private StorageInfo[] storages = [];
    public string Page { get => page; set { if (Set(ref page, value)) { Changed(nameof(PageTitle)); CommandManager.InvalidateRequerySuggested(); } } }
    public string PageTitle => Page switch { "saves" => "存档", "files" => "SD 文件", "console" => "主机", "settings" => "设置", "diagnostics" => "连接诊断", _ => "游戏库" };
    public string Model { get => model; set => Set(ref model, value); }
    public string Connection { get => connection; set => Set(ref connection, value); }
    public string EnvironmentText { get => environment; set => Set(ref environment, value); }
    public string SyncText { get => sync; set => Set(ref sync, value); }
    public bool Online { get => online; set { if (Set(ref online, value)) CommandManager.InvalidateRequerySuggested(); } }
    public DeviceInfo? Device { get => device; set => Set(ref device, value); }
    public StorageInfo[] Storages { get => storages; set { if (Set(ref storages, value)) { Changed(nameof(SdStorage)); Changed(nameof(ShowRailStorage)); } } }
    public StorageInfo? SdStorage => Storages.FirstOrDefault(x => x.Id == "SdCard");
    public bool ShowRailStorage => !Compact && SdStorage != null;
    public bool DetailOpen { get => detailOpen; set => Set(ref detailOpen, value); }
    public bool Compact { get => compact; set { if (Set(ref compact, value)) Changed(nameof(ShowRailStorage)); } }
    public bool Fixture { get => fixture; set => Set(ref fixture, value); }
    public bool StreamGuide { get => streamGuide; set => Set(ref streamGuide, value); }
    public string Notice { get => notice; set => Set(ref notice, value); }
    public string NoticePath { get => noticePath; set => Set(ref noticePath, value); }
    public void Notify(string text, string path = "") { NoticePath = path; Notice = text; }
    public void Bind(string name, Action<object?> action, Func<object?, bool>? can = null) => Commands[name] = new Command(action, can);
}
public sealed class InstallEntry(string path) : Observable
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public InstallSource? Source { get; set; }
    private string state = "等待识别"; public string State { get => state; set => Set(ref state, value); }
    public string Detail => Source == null ? "需连接主机进行内容识别" : $"{Source.Package.GameName ?? Name} · {Display.Size(Source.Package.RequiredBytes)} · {string.Join(" / ", Source.Package.Components.Select(x => x.KindText + " v" + x.Version).Distinct())}";
    public void Refresh() => Changed(nameof(Detail));
}
public sealed class UploadEntry(string local, string target, bool conflict) : Observable
{
    public string Local { get; } = local;
    public string Name => Path.GetFileName(Local);
    public bool Conflict { get; } = conflict;
    private string targetName = target; private bool skip;
    public string TargetName { get => targetName; set => Set(ref targetName, value); }
    public bool Skip { get => skip; set => Set(ref skip, value); }
    public string Hint => Conflict ? "同名冲突 · 修改名称或勾选跳过" : "新文件";
}
