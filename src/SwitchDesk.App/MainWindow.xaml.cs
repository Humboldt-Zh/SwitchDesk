using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SwitchDesk.Core;

namespace SwitchDesk.App;
public partial class MainWindow : Window
{
    private readonly ShellViewModel vm = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim jobsGate = new(1);
    private readonly SettingsStore settingsStore;
    private readonly LibraryDatabase library;
    private readonly DeviceCache cache;
    private readonly Phase2State phase2;
    private AppSettings settings;
    private DeviceClient? client;
    private DeviceInfo? device;
    private GameTitle[] titles = [];
    private SaveInfo[] saves = [];
    private int activeJobs;
    private bool streaming, initialized, batchRunning, reviewing;
    private DateTimeOffset? synced;
    private string lastConnectionMessage = "";
    private readonly object logLock = new();
    private readonly HashSet<string> loadingIcons = [];
    private readonly ObservableCollection<InstallEntry> installEntries = [];
    private readonly ObservableCollection<UploadEntry> uploadEntries = [];
    private DeviceInfo? reviewedDevice;
    private string uploadDirectory = "/";
    private Point dragOrigin;
    private object? dragItem;
    private FrameworkElement? dragElement;
    private readonly bool fixtureMode;
    public MainWindow() : this(null, false) { }
    public MainWindow(string? dataDirectory, bool fixtureMode)
    {
        this.fixtureMode = fixtureMode;
        var args = Environment.GetCommandLineArgs(); var index = Array.IndexOf(args, "--data-dir");
        var root = dataDirectory ?? (index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwitchDesk"));
        settingsStore = new(root); Directory.CreateDirectory(root); settings = settingsStore.Load();
        library = new(Path.Combine(root, "library.db")); cache = new(Path.Combine(root, "ConsoleCache"));
        phase2 = new(Path.Combine(root, "Phase2", "state.db"));
        new MigrationWorkspace(phase2, Path.Combine(root, "Phase2"), CompatibilityCatalog.Released).RecoverInterruptedOperations();
        ThemeManager.Apply(settings.Theme); InitializeComponent(); SourceInitialized += (_, _) => ThemeManager.ApplyCaption(this); BindCommands(); InitializePhase2(); DataContext = vm;
        vm.Library.ListView = settings.ListView;
        vm.Library.SelectionChanged += () => { if (vm.Library.Selected != null) { vm.DetailOpen = true; UpdateDetails(); Animate(Details); } LayoutDetails(); };
        vm.Saves.SelectionChanged += () => { UpdateAutoPolicy(); vm.CrossSave.Invalidate(); }; vm.Saves.AutoChanged += SetAutoPolicy;
        vm.Tasks.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Tasks.HasActions)) JobActionsColumn.Visibility = vm.Tasks.HasActions ? Visibility.Visible : Visibility.Collapsed; };
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Notice)) UpdateNotice(); if (e.PropertyName == nameof(vm.DetailOpen)) LayoutDetails(); };
        vm.Library.Filtered.CollectionChanged += (_, _) => EmptyLibrary.Visibility = vm.Library.Filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InstallGrid.ItemsSource = installEntries; UploadGrid.ItemsSource = uploadEntries;
        var cached = cache.Latest();
        if (cached != null) { device = cached.Device; titles = cached.Games; saves = cached.Saves; synced = cached.SyncedAt; vm.Storages = cached.Storages; UpdateDevice(cached.Device); vm.Connection = "离线记录"; }
        VersionLabel.Text = $"SwitchDesk {BuildInfo.Version}"; LoadLocalLists(); ThemeChoice.SelectedValue = settings.Theme; UpdateSettingsText(); initialized = true;
        Navigate("library"); UpdateNotice();
        if (settingsStore.RecoveryMessage.Length > 0) vm.Notify(settingsStore.RecoveryMessage);
        if (args.Contains("--smoke-test") || fixtureMode) { vm.Fixture = fixtureMode; ShowActivated = false; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.Manual; Left = -16000; Top = -16000; }
    }
    private void BindCommands()
    {
        void Async(string name, Func<Task> action, Func<bool>? can = null) => vm.Bind(name, p => { _ = Guard(action); }, p => can?.Invoke() ?? true);
        bool Ready(string capability) => vm.Online && client?.IsBroken == false && device?.Capabilities.Contains(capability) == true;
        bool LiveSave() => Ready("saves") && vm.Saves.Selected?.Live != null;
        vm.Bind("Navigate", p => Navigate(p?.ToString() ?? "library"));
        vm.Bind("SelectGame", p => { if (p is GameCard card) { vm.Library.Selected = card; vm.DetailOpen = true; UpdateDetails(); LayoutDetails(); } });
        vm.Bind("CloseDetails", _ => vm.DetailOpen = false);
        vm.Bind("Source", p => { vm.Library.Source = p?.ToString() ?? "全部"; MarkFilters(); });
        vm.Bind("ClearFilters", _ => { vm.Library.Search = ""; vm.Library.Source = "全部"; vm.Library.StatusFilter = "全部状态"; Navigate("library"); });
        vm.Bind("Grid", _ => SetView(false)); vm.Bind("List", _ => SetView(true));
        vm.Bind("Dismiss", _ => vm.Notice = ""); vm.Bind("NoticeOpen", _ => Reveal(vm.NoticePath), _ => vm.NoticePath.Length > 0);
        Async("InstallPick", PickInstallAsync); Async("InstallLocal", () => ReviewInstallAsync([SelectedLocal().Path]), () => LocalFiles.SelectedItem is LibraryItem);
        Async("Export", ExportAsync, () => Ready("export") && vm.Library.Selected?.Console?.Components.Length > 0);
        Async("ExportDirectory", () => { var pick = new OpenFolderDialog { Title = "选择游戏导出目录", InitialDirectory = settings.ExportDirectory }; if (pick.ShowDialog(this) == true) { settings = settings with { ExportDirectory = pick.FolderName }; settingsStore.Save(settings); UpdateSettingsText(); } return Task.CompletedTask; });
        Async("Backup", async () => { var result = await BackupAsync(SelectedSave()); vm.Notify("已备份 · " + result.Manifest.GameName, Path.GetDirectoryName(result.Path)!); }, LiveSave);
        Async("Restore", RestoreAsync, () => LiveSave() && SnapshotsGrid.SelectedItem is Snapshot);
        vm.Bind("RestoreVersion", p => { if (p is Snapshot snapshot) { SnapshotsGrid.SelectedItem = snapshot; _ = Guard(RestoreAsync); } }, p => LiveSave() && p is Snapshot);
        Async("VerifySnapshot", VerifySnapshotAsync, () => SnapshotsGrid.SelectedItem is Snapshot);
        Async("AddLibrary", AddLibraryAsync); Async("ScanLibrary", () => ScanLibraryAsync(settings.LibraryDirectory));
        Async("VerifyLocal", VerifyLocalAsync, () => LocalFiles.SelectedItem is LibraryItem);
        Async("InspectLocal", InspectLocalAsync, () => Ready("inspect") && LocalFiles.SelectedItem is LibraryItem);
        vm.Bind("RevealLocal", _ => Reveal(Path.GetDirectoryName(SelectedLocal().Path)!), _ => LocalFiles.SelectedItem is LibraryItem);
        Async("UploadPick", PickUploadAsync, () => Ready("files")); Async("Download", DownloadAsync, () => Ready("files") && FilesGrid.SelectedItem != null);
        Async("Rename", RenameAsync, () => Ready("files") && FilesGrid.SelectedItem != null); Async("Delete", DeleteAsync, () => Ready("files") && FilesGrid.SelectedItem != null);
        Async("Mkdir", MkdirAsync, () => Ready("files"));
        Async("Refresh", () => RunJob("刷新主机资料", async (_, ct) => { await RefreshDeviceAsync(ct); return true; }), () => vm.Online);
        Async("Reconnect", async () => { streaming = false; await DisconnectAsync(); }, () => Volatile.Read(ref activeJobs) == 0);
        vm.Bind("CancelJob", p => { if (p is Job j && j.CanCancel) { j.Cancellation.Cancel(); j.Detail = "正在请求取消"; } }, p => p is Job { CanCancel: true });
        vm.Bind("RetryJob", p => { if (p is Job { Retry: {} retry }) _ = Guard(retry); }, p => p is Job { State: "需要处理", Retry: not null } && Volatile.Read(ref activeJobs) == 0);
        vm.Bind("StreamGuide", _ => { StreamCondition.Text = Volatile.Read(ref activeJobs) != 0 ? "传输任务正在运行；完成或取消后可启动播放器。" : ""; vm.StreamGuide = true; });
        vm.Bind("CloseStream", _ => vm.StreamGuide = false); Async("Stream", StreamAsync, () => Volatile.Read(ref activeJobs) == 0 && !streaming);
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    { LayoutDetails(); if (Environment.GetCommandLineArgs().Contains("--smoke-test")) { await RenderSmokeAsync(); Close(); return; } if (fixtureMode) return; Log(BuildInfo.ClientId + " 启动；等待配套程序。"); _ = Task.Run(ObserveSteamChangesAsync); await MonitorAsync(); }
    private void Navigate(string page)
    {
        SearchHeader.Visibility = page is "library" or "saves" ? Visibility.Visible : Visibility.Collapsed;
        HeaderInstall.Visibility = SearchHeader.Visibility;
        vm.Page = page; vm.Library.SavesOnly = page == "saves"; LibraryPage.Visibility = page is "library" or "saves" ? Visibility.Visible : Visibility.Collapsed;
        FilesPage.Visibility = page == "files" ? Visibility.Visible : Visibility.Collapsed; ConsolePage.Visibility = page == "console" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed; DiagnosticsPage.Visibility = page == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "saves") { DetailsTabs.SelectedIndex = 1; if (vm.Library.Selected != null) vm.DetailOpen = true; }
        if (page == "files" && vm.Online && FilesGrid.ItemsSource == null) _ = Guard(async () => await RunJob("打开 SD 目录", async (_, ct) => { await LoadFilesAsync(ct); return true; }));
        LayoutDetails(); MarkNavigation(); UpdateNotice();
    }
    private void MarkNavigation()
    { foreach (var button in Descendants<Button>(Root).Where(b => b.Command == vm.Commands["Navigate"])) { var selected = button.CommandParameter?.ToString() == vm.Page; button.SetResourceReference(BackgroundProperty, selected ? "Selected" : "Rail"); button.SetResourceReference(ForegroundProperty, selected ? "Accent" : "Secondary"); } }
    private void MarkFilters()
    { foreach (var button in Descendants<Button>(LibraryPage).Where(b => b.Command == vm.Commands["Source"])) { var selected = button.CommandParameter?.ToString() == vm.Library.Source; button.SetResourceReference(BackgroundProperty, selected ? "Selected" : "Canvas"); button.SetResourceReference(ForegroundProperty, selected ? "Accent" : "Secondary"); } }
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) { var child = VisualTreeHelper.GetChild(node, i); if (child is T typed) yield return typed; foreach (var nested in Descendants<T>(child)) yield return nested; } }
    private void OnWindowSize(object sender, SizeChangedEventArgs e) { if (initialized) LayoutDetails(); }
    private void LayoutDetails()
    {
        if (!initialized) return; vm.Compact = ActualWidth < 1180; NavigationColumn.Width = new(vm.Compact ? 72 : 198);
        foreach (var element in new FrameworkElement[] { BrandLabel, ConsoleLabel, ConsoleStatusLabels, VersionLabel, WorkspaceLabel }) element.Visibility = vm.Compact ? Visibility.Collapsed : Visibility.Visible;
        var show = vm.DetailOpen && vm.Library.Selected != null && vm.Page is "library" or "saves";
        DetailColumn.Width = new(show ? 304 : 0); Details.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Dispatcher.BeginInvoke(() => { ResizeGallery(Math.Max(180, LibraryPage.ActualWidth - 20)); MarkNavigation(); MarkFilters(); });
    }
    private void OnGallerySize(object sender, SizeChangedEventArgs e) { if (initialized && e.NewSize.Width > 0) ResizeGallery(Math.Max(180, e.NewSize.Width - 20)); }
    private void ResizeGallery(double width)
    {
        var viewer = Descendants<ScrollViewer>(Gallery).FirstOrDefault();
        var row = Math.Clamp((int)(viewer?.VerticalOffset ?? 0), 0, Math.Max(0, vm.Library.Rows.Count - 1));
        var anchor = vm.Library.Rows.ElementAtOrDefault(row)?.Cards.FirstOrDefault()?.Key;
        var oldColumns = vm.Library.Rows.FirstOrDefault()?.Cards.Length;
        vm.Library.Resize(width);
        if (anchor != null && oldColumns != vm.Library.Rows.FirstOrDefault()?.Cards.Length)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { var target = vm.Library.Rows.FirstOrDefault(r => r.Cards.Any(c => c.Key == anchor)); if (target != null) Gallery.ScrollIntoView(target); });
    }
    private void SetView(bool list) { vm.Library.ListView = list; GridViewButton.SetResourceReference(BackgroundProperty, list ? "Canvas" : "Selected"); ListViewButton.SetResourceReference(BackgroundProperty, list ? "Selected" : "Canvas"); settings = settings with { ListView = list }; settingsStore.Save(settings); }
    private static void Animate(FrameworkElement element) { if (SystemParameters.ClientAreaAnimation) element.BeginAnimation(OpacityProperty, new DoubleAnimation(.55, 1, TimeSpan.FromMilliseconds(140))); }
    private void UpdateNotice() { var repeatedTaskError = vm.Page == "diagnostics" && vm.Tasks.Items.Any(x => x.State == "需要处理" && x.Detail == vm.Notice); NoticeBox.Visibility = vm.Notice.Length > 0 && !repeatedTaskError ? Visibility.Visible : Visibility.Collapsed; NoticeOpen.Visibility = vm.NoticePath.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
    private void UpdateSettingsText()
    { LibraryLocation.Text = settings.LibraryDirectory; RetentionLabel.Text = $"当前状态：{(vm.Saves.AutoBackup ? "开启" : "关闭")} · 保留最近 {settings.Retention} 个不同版本"; SettingsSummary.Text = $"构建  {BuildInfo.ClientId}\n备份  {settings.BackupDirectory}\n导出  {settings.ExportDirectory}\n历史保留  {settings.Retention} 个不同版本\n播放器  {(settings.SysDvrClient.Length > 0 ? settings.SysDvrClient : "尚未设置")}"; }
    private void LoadLocalLists()
    {
        var scroll = Descendants<ScrollViewer>(Gallery).FirstOrDefault(); var offset = scroll?.VerticalOffset ?? 0;
        var anchor = vm.Library.Rows.ElementAtOrDefault((int)offset)?.Cards.FirstOrDefault()?.Key;
        var history = new SnapshotStore(settings.BackupDirectory).List().ToArray(); vm.Saves.SetData(saves, history);
        vm.Library.SaveTitles = saves.Select(x => x.TitleId).Concat(history.Select(x => x.Manifest.TitleId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known = titles.Select(x => x.TitleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var archiveGames = history.Where(x => !known.Contains(x.Manifest.TitleId)).GroupBy(x => x.Manifest.TitleId).Select(x => new GameTitle(x.Key, x.First().Manifest.GameName, "历史存档", "—", "仅历史存档", []));
        vm.Library.Merge(titles.Concat(archiveGames), library.List()); ApplyCatalogLabels(); UpdateDetails(); UpdateSyncText(); UpdateSettingsText();
        if (scroll != null) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { var row = anchor == null ? -1 : vm.Library.Rows.ToList().FindIndex(r => r.Cards.Any(c => c.Key == anchor)); scroll.ScrollToVerticalOffset(row >= 0 ? row : offset); });
    }
    private void UpdateDetails() { vm.Saves.SetContext(vm.Library.Selected?.TitleId, device); UpdateAutoPolicy(); UpdatePhase2Details(); CommandManager.InvalidateRequerySuggested(); }
    private void UpdateSyncText() => vm.SyncText = synced is {} time ? $"{(vm.Online ? "已同步" : "离线记录")} · {time.ToLocalTime():MM-dd HH:mm} · {device?.Environment}" : "连接 Switch，或添加电脑游戏目录";
    private void UpdateDevice(DeviceInfo info) { vm.Device = info; vm.Model = info.Model; vm.EnvironmentText = info.Environment + (info.ApplicationMode ? "" : " · Applet 模式"); }
    private void SetConnectionMessage(string message) { vm.Connection = message; if (message != lastConnectionMessage) { lastConnectionMessage = message; Log(message); } }
    private async Task MonitorAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                if (streaming || Volatile.Read(ref activeJobs) != 0) { await Task.Delay(1000, lifetime.Token); continue; }
                if (client?.IsBroken == true) await DisconnectAsync();
                if (client == null)
                {
                    var candidates = await Task.Run(UsbDiscovery.Enumerate, lifetime.Token); var message = candidates.Count == 0 ? (await Task.Run(UsbDiscovery.DiagnoseMissingInterface, lifetime.Token) ?? (synced != null ? "离线记录 · 等待连接" : "等待配套程序")) : "检测到接口 · 正在核对";
                    foreach (var candidate in candidates)
                    {
                        DeviceClient? attempt = null;
                        try
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
                            attempt = new(await Task.Run(() => new WinUsbStream(candidate.Path), timeout.Token)); var info = await attempt.HandshakeAsync(timeout.Token); client = attempt;
                            if (device?.DeviceId != info.DeviceId || device?.Environment != info.Environment) { titles = []; saves = []; vm.Storages = []; }
                            device = info; vm.Online = true; UpdateDevice(info); message = "已连接";
                            await RunJob("读取主机资料", async (_, ct) => { await RefreshDeviceAsync(ct); return true; }); ScheduleAutoBackups(); _ = RefreshCatalogInBackgroundAsync(); break;
                        }
                        catch (Exception ex)
                        { if (attempt != null) await attempt.DisposeAsync(); if (ReferenceEquals(client, attempt)) { client = null; vm.Online = false; } message = ex is Win32Exception { NativeErrorCode: 5 or 32 } ? "接口被占用 · 打开诊断" : ex.Message; }
                    }
                    SetConnectionMessage(message);
                }
                else
                {
                    try
                    {
                        if (!await jobsGate.WaitAsync(0, lifetime.Token)) { await Task.Delay(250, lifetime.Token); continue; }
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(4));
                        DeviceInfo info; try { info = await client.QueryAsync<DeviceInfo>("device.info", ct: timeout.Token); } finally { jobsGate.Release(); }
                        var changed = info.DeviceId != device?.DeviceId || info.Environment != device?.Environment; device = info; UpdateDevice(info);
                        if (changed) { titles = []; saves = []; await RunJob("切换系统环境", async (_, ct) => { await RefreshDeviceAsync(ct); return true; }); }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or JsonException or InvalidOperationException) { Log("连接中断：" + ex.Message); await DisconnectAsync(); }
                }
                await Task.Delay(1500, lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            catch (Exception ex) { Log("连接检查：" + ex.Message); try { await Task.Delay(3000, lifetime.Token); } catch (OperationCanceledException) { break; } }
        }
    }
    private async Task DisconnectAsync()
    { var old = client; client = null; vm.Online = false; if (old != null) await old.DisposeAsync(); FilesGrid.ItemsSource = null; SetConnectionMessage(synced != null ? "离线记录 · 等待连接" : "等待配套程序"); UpdateSyncText(); UpdateAutoPolicy(); }
    private (DeviceClient Client, DeviceInfo Device) RequireDevice(string? capability = null)
    {
        if (!vm.Online || client == null || client.IsBroken || device == null) throw new IOException("请连接 Switch 并打开配套程序；当前显示的是离线记录");
        if (capability != null && !device.Capabilities.Contains(capability)) throw new DeviceException("Unavailable", "当前主机环境不支持此操作：" + capability); return (client, device);
    }
    private async Task RefreshDeviceAsync(CancellationToken ct)
    {
        var (peer, info) = RequireDevice(); var storage = await peer.QueryAsync<StorageInfo[]>("storages.list", ct: ct); var games = await peer.QueryAsync<GameTitle[]>("titles.list", ct: ct);
        var list = info.Capabilities.Contains("saves") ? await peer.QueryAsync<SaveInfo[]>("saves.list", ct: ct) : []; var time = DateTimeOffset.UtcNow;
        await cache.SaveAsync(new(info, storage, games, list, time), ct); await Dispatcher.InvokeAsync(() => { vm.Storages = storage; titles = games; saves = list; synced = time; LoadLocalLists(); });
    }
    private async Task<T> RunJob<T>(string name, Func<Job, CancellationToken, Task<T>> action, Func<Task>? retry = null)
    {
        var job = new Job { Name = name, Retry = retry }; if (Dispatcher.CheckAccess()) vm.Tasks.Add(job); else await Dispatcher.InvokeAsync(() => vm.Tasks.Add(job)).Task.ConfigureAwait(false);
        Interlocked.Increment(ref activeJobs); using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, job.Cancellation.Token);
        try
        {
            await jobsGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try { _ = Dispatcher.BeginInvoke(() => { job.State = "准备"; job.Detail = "准备中"; }); Log("开始：" + name);
                var result = await Task.Run(() => action(job, linked.Token), linked.Token).ConfigureAwait(false);
                _ = Dispatcher.BeginInvoke(() => { job.State = "完成"; job.Percent = 100; job.Indeterminate = false; job.CanCancel = false; job.Detail = "已完成"; }); Log("完成：" + name); return result;
            } finally { jobsGate.Release(); }
        }
        catch (OperationCanceledException) { _ = Dispatcher.BeginInvoke(() => { job.State = "已取消"; job.CanCancel = false; job.Detail = "可重新执行；保留已完成结果"; }); Log("取消：" + name); throw; }
        catch (Exception ex) { _ = Dispatcher.BeginInvoke(() => { job.State = "需要处理"; job.CanCancel = false; job.Detail = ex.Message; }); Log($"失败：{name} · {ex.Message}"); throw; }
        finally { Interlocked.Decrement(ref activeJobs); _ = Dispatcher.BeginInvoke(() => { vm.Tasks.Refresh(); CommandManager.InvalidateRequerySuggested(); }); }
    }
    private IProgress<TransferProgress> Progress(Job job)
    {
        var watch = Stopwatch.StartNew(); long previous = 0; string stage = "";
        return new DirectProgress(p => { void Update() { if (stage != p.Stage || p.Completed < previous) { watch.Restart(); stage = p.Stage; } previous = p.Completed; job.Report(p); job.Rate = p.Total > 0 && watch.Elapsed.TotalSeconds > .1 ? Display.Size((long)(p.Completed / watch.Elapsed.TotalSeconds)) + "/s" : ""; }
            if (p.Stage.Contains("写回存档", StringComparison.Ordinal)) Dispatcher.Invoke(Update); else _ = Dispatcher.BeginInvoke(Update); });
    }
    private sealed class DirectProgress(Action<TransferProgress> callback) : IProgress<TransferProgress> { public void Report(TransferProgress value) => callback(value); }
    private async Task Guard(Func<Task> action)
    { try { await action(); } catch (OperationCanceledException) { vm.Notify("任务已取消，可在重连后重新执行"); } catch (Exception ex) { Log(ex.Message); if (ex.Data["SwitchDesk.RecoveryPath"] is string recovery) vm.Notify(ex.Message + " · 恢复前快照已保留", Path.GetDirectoryName(recovery)!); else vm.Notify(ex.Message); } }
    private void Log(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {text}";
        try { lock (logLock) { Directory.CreateDirectory(Path.Combine(settingsStore.Root, "Logs")); File.AppendAllText(Path.Combine(settingsStore.Root, "Logs", DateTime.Today.ToString("yyyyMMdd") + ".log"), line + Environment.NewLine); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { line += " · 本机日志未能写入，保留会话内记录"; }
        _ = Dispatcher.BeginInvoke(() => vm.RecordLog(line, text));
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    { if (Volatile.Read(ref activeJobs) != 0) { e.Cancel = true; Navigate("diagnostics"); vm.Notify("请等待任务完成，或在连接诊断中取消任务；存档写回期间不可取消"); return; } lifetime.Cancel(); if (client != null) await client.DisposeAsync(); library.Dispose(); phase2.Dispose(); }
}
