using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Win32;
using SwitchDesk.Core;

namespace SwitchDesk.App;

public partial class MainWindow
{
    private sealed record CrossContext(SavePair Pair, ISaveAdapter Adapter, StableSaveFile Pc, byte[] Nx, Snapshot TargetSnapshot, SaveInfo Save,
        SaveDeviceInspection? Inspection, DeviceInfo Device, SaveReading PcReading, SaveReading NxReading, SlotInspection PcFormat, string[] NxDlc, GameVersionContext? Versions, CompatibilityRule? Rule);
    private CrossContext? lastCross;
    private bool crossContextUpdating, crossActionBusy;
    private PcSaveCandidate[] crossPcCandidates = [];
    private bool crossPcScanned;
    private string? crossPcDiscoveryError;
    private long crossDiscoveryGeneration;
    private CancellationTokenSource? crossDiscoveryWait;
    private string? crossDiscoveredKey, crossPendingKey;
    private Task crossFileDiscoveryTask = Task.CompletedTask;
    private void InitializePhase2()
    {
        ReloadContentSources();
        LoadPendingDiagnostics();
        vm.CrossSave.SelectionChanged += () => { if (!crossContextUpdating) { CancelCrossSaveDiscovery(); DiscoverPcSaves(); RefreshCrossSaveContext(); } };
        vm.CrossSave.TargetChanged += () => { if (!crossContextUpdating) RefreshCrossSaveContext(); };
        vm.CrossSave.UserChanged += () => { if (!crossContextUpdating) RequestCrossSaveFiles(); };
        vm.CrossSave.PreviewInvalidated += () => lastCross = null;
        vm.Bind("CrossSaveOpen", _ => OpenCrossSaveFromGame(), _ => vm.CrossSave.CanSelect && vm.Library.Selected?.TitleId != null);
        vm.Bind("CrossSaveSelectGame", p => { if (p is CrossSaveGameItem game) vm.CrossSave.SelectedGame = game; }, _ => vm.CrossSave.CanSelect);
        vm.Bind("CrossSaveHome", _ => OpenCrossSaveHome(), _ => vm.CrossSave.CanSelect);
        vm.Bind("CrossSaveClose", _ => OpenCrossSaveHome(), _ => vm.CrossSave.CanSelect);
        vm.Bind("CrossSaveBrowse", _ => BrowsePcSave(), _ => vm.CrossSave.CanSelect && vm.CrossSave.Adapter != null);
        vm.Bind("CrossSaveRefreshFiles", _ => RequestCrossSaveFiles(true), _ => vm.CrossSave.CanSelect && CurrentCrossSaveKey() != null && vm.Online && !vm.CrossSave.FilesLoading);
        vm.Bind("CrossSaveBackupPc", _ => _ = Guard(BackupCrossSavePcAsync), _ => vm.CrossSave.CanSelect && vm.CrossSave.Adapter != null && vm.CrossSave.SelectedPc != null);
        vm.Bind("CrossSaveRead", _ => _ = Guard(ReadCrossSaveAsync), _ => vm.CrossSave.CanSelect && vm.CrossSave.SelectedPc != null && vm.CrossSave.SelectedSwitchFile != null && !vm.CrossSave.FilesLoading && CurrentCrossSaveKey() is {} key && key == crossDiscoveredKey && vm.Online);
        // The shipped compatibility catalog is empty until version-specific gameplay evidence is recorded.
        bool CanMigrate() => vm.CrossSave.CanMigrate && vm.CrossSave.CanSelect && vm.Online;
        vm.Bind("CrossSaveToSwitch", _ => _ = Guard(() => MigrateCrossSaveAsync(SavePlatform.Steam)), _ => CanMigrate());
        vm.Bind("CrossSaveToPc", _ => _ = Guard(() => MigrateCrossSaveAsync(SavePlatform.Switch)), _ => CanMigrate());
        vm.Bind("CheckUpdates", _ => _ = Guard(CheckUpdatesAsync), _ => vm.Library.Selected?.Console != null && Volatile.Read(ref activeJobs) == 0);
        vm.Bind("AddContentSource", _ => AddContentSource());
        vm.Bind("RemoveContentSource", _ => RemoveContentSource(), _ => vm.SelectedSource != null);
        vm.Bind("DownloadOffer", _ => _ = Guard(DownloadOfferAsync), _ => vm.SelectedOffer?.Entry != null && Volatile.Read(ref activeJobs) == 0);
        vm.Bind("InstallOffer", _ => _ = Guard(InstallOfferAsync), _ => vm.UpdateOffers.Any(x => x.Selected && x.Entry != null) && vm.Online && device?.Capabilities.Contains("install.plan.v1") == true && Volatile.Read(ref activeJobs) == 0);
        vm.Bind("BuildContentIndex", _ => _ = Guard(BuildContentIndexAsync), _ => vm.Online && device?.Capabilities.Contains("package.dependencies.v1") == true && Volatile.Read(ref activeJobs) == 0);
        vm.Bind("OpenPhase2Recovery", _ => Reveal(Path.Combine(settingsStore.Root, "Phase2", "Recovery")));
        vm.Bind("InspectPending", _ => _ = Guard(InspectPendingOperationsAsync), _ => Volatile.Read(ref activeJobs) == 0);
    }
    private void OnLeaveCrossSave(object sender, System.Windows.RoutedEventArgs e) => vm.CrossSave.Open = false;
    private void OpenCrossSaveHome()
    {
        CancelCrossSaveDiscovery(); vm.CrossSave.SelectedGame = null; vm.CrossSave.Open = false;
        ScanCrossPcSaves();
        RefreshCrossSaveContext(); Navigate("cross-save");
    }
    private void OpenCrossSaveFromGame()
    {
        var definition = CrossSaveGames.ForTitle(vm.Library.Selected?.TitleId);
        if (definition == null)
        {
            OpenCrossSaveHome(); vm.Notify("此游戏尚未登记跨平台游戏标识；请选择已列出的游戏查看发现与备份状态"); return;
        }
        Navigate("cross-save");
        vm.CrossSave.SelectedGame = vm.CrossSave.Games.Single(x => x.AdapterId == definition.AdapterId);
        vm.CrossSave.Open = true; RefreshCrossSaveContext();
    }
    private static string? SteamRoot()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("SteamPath") as string;
    }
    private void DiscoverPcSaves()
    {
        var adapter = vm.CrossSave.Adapter; vm.CrossSave.Invalidate();
        if (adapter == null) { vm.CrossSave.Candidates = []; return; }
        ScanCrossPcSaves();
        vm.CrossSave.Candidates = crossPcCandidates.Where(x => x.AdapterId == adapter.Id).ToArray(); vm.CrossSave.SelectedPc = vm.CrossSave.Candidates.FirstOrDefault();
        vm.CrossSave.PcSummary = vm.CrossSave.Candidates.Length > 0 ? "已找到有效位置；读取前请退出游戏" : "没有找到有效存档，可手动选择；不会使用旧备份或按时间猜测文件";
    }
    private void ScanCrossPcSaves()
    {
        crossPcScanned = true; crossPcDiscoveryError = null;
        if (fixtureMode || Environment.GetCommandLineArgs().Contains("--smoke-test")) return;
        try { crossPcCandidates = SteamSaves.Discover(SteamRoot(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { crossPcDiscoveryError = "电脑 · 扫描未完成，可选择文件"; }
    }

    // Called after inventory refresh. This page never borrows the library's game/user selection.
    private void RefreshCrossSaveContext()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(() => RefreshCrossSaveContext()); return; }
        if (crossContextUpdating) return;
        if (!crossPcScanned) ScanCrossPcSaves();
        crossContextUpdating = true;
        try
        {
            foreach (var game in vm.CrossSave.Games)
            {
                var pcCount = crossPcCandidates.Count(x => x.AdapterId == game.AdapterId);
                if (vm.CrossSave.SelectedGame?.AdapterId == game.AdapterId) pcCount = Math.Max(pcCount, vm.CrossSave.Candidates.Length);
                game.PcStatus = pcCount > 0 ? $"电脑 · 已发现 {pcCount} 个存档槽位" : crossPcDiscoveryError ?? "电脑 · 尚未发现，可选择文件";
                game.SwitchStatus = !game.Definition.HasSwitchIdentity ? "Switch · 游戏识别待补" : !vm.Online ? "Switch · 未连接（已有缓存不代表在线）" : device?.ApplicationMode != true || !device.Capabilities.Contains("saves") ? "Switch · 请使用完整应用模式" : saves.Any(x => game.Definition.MatchesTitle(x.TitleId) && x.Environment == device.Environment) ? "Switch · 已发现存档" : titles.Any(x => game.Definition.MatchesTitle(x.TitleId)) ? "Switch · 已安装，尚未发现存档" : "Switch · 尚未发现此游戏";
            }
            var definition = vm.CrossSave.SelectedGame?.Definition;
            CrossSaveTargetGame[] targets = definition == null ? [] : titles.Where(x => definition.MatchesTitle(x.TitleId)).Select(x => new CrossSaveTargetGame(x.TitleId, x.Name))
                .Concat(saves.Where(x => definition.MatchesTitle(x.TitleId) && x.Environment == device?.Environment).Select(x => new CrossSaveTargetGame(x.TitleId, x.GameName)))
                .DistinctBy(x => x.TitleId, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            var titleId = vm.CrossSave.SelectedTargetGame?.TitleId;
            vm.CrossSave.TargetGames = targets;
            vm.CrossSave.SelectedTargetGame = targets.FirstOrDefault(x => x.TitleId.Equals(titleId, StringComparison.OrdinalIgnoreCase)) ?? targets.FirstOrDefault();
            var selectedKey = vm.CrossSave.SelectedUser?.Key;
            vm.CrossSave.Users = !vm.Online || vm.CrossSave.SelectedTargetGame == null ? [] : saves
                .Where(x => x.TitleId.Equals(vm.CrossSave.SelectedTargetGame.TitleId, StringComparison.OrdinalIgnoreCase) && x.Environment == device?.Environment)
                .Select(x => new SaveChoice(x.Key, x.UserName, x.Type, x)).ToArray();
            vm.CrossSave.SelectedUser = vm.CrossSave.Users.FirstOrDefault(x => x.Key == selectedKey) ?? vm.CrossSave.Users.FirstOrDefault();
        }
        finally { crossContextUpdating = false; }
        RefreshCrossSaveAvailability(); RequestCrossSaveFiles();
    }
    private void RefreshCrossSaveAvailability()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(() => RefreshCrossSaveAvailability()); return; }
        vm.CrossSave.CanSelect = !crossActionBusy && Volatile.Read(ref activeJobs) == 0;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
    private string? CurrentCrossSaveKey()
    {
        var definition = vm.CrossSave.SelectedGame?.Definition; var save = vm.CrossSave.SelectedUser?.Live;
        if (definition == null || save == null || device == null || !definition.MatchesTitle(save.TitleId) || !save.TitleId.Equals(vm.CrossSave.SelectedTargetGame?.TitleId, StringComparison.OrdinalIgnoreCase) || save.Environment != device.Environment) return null;
        return string.Join('\0', device.DeviceId, device.Environment, definition.AdapterId, save.TitleId.ToUpperInvariant(), save.UserId, save.Type, save.Key);
    }
    private void CancelCrossSaveDiscovery()
    {
        ++crossDiscoveryGeneration;
        // Only cancel waiting for jobsGate. Cancelling a framed request in flight would break USB.
        crossDiscoveryWait?.Cancel(); crossDiscoveryWait = null;
        crossDiscoveredKey = null; crossPendingKey = null; lastCross = null;
        vm.CrossSave.ClearFiles("选择 Switch 游戏和用户后，自动读取槽位"); vm.CrossSave.Invalidate();
    }
    private void InvalidateCrossSaveConnection()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(() => InvalidateCrossSaveConnection()); return; }
        CancelCrossSaveDiscovery();
        crossContextUpdating = true;
        try { vm.CrossSave.Users = []; vm.CrossSave.SelectedUser = null; }
        finally { crossContextUpdating = false; }
        vm.CrossSave.FileStatus = "连接 Switch 后重新读取槽位；旧比较已失效";
        vm.CrossSave.SwitchSummary = "当前为离线记录，不能读取或写入 Switch 存档";
        RefreshCrossSaveAvailability();
    }
    private void RequestCrossSaveFiles(bool force = false)
    {
        if (crossContextUpdating) return;
        var key = CurrentCrossSaveKey();
        if (!force && key != null && (key == crossDiscoveredKey && vm.CrossSave.FilesReady || key == crossPendingKey)) return;
        CancelCrossSaveDiscovery();
        if (!vm.CrossSave.HasSelection) return;
        if (vm.CrossSave.SelectedGame?.Definition.HasSwitchIdentity != true) { vm.CrossSave.FileStatus = "Switch 游戏识别待补；可先发现和备份电脑存档"; return; }
        if (!vm.Online || client == null || client.IsBroken) { vm.CrossSave.FileStatus = "连接 Switch 后自动读取槽位"; return; }
        if (device?.ApplicationMode != true || !device.Capabilities.Contains("saves")) { vm.CrossSave.FileStatus = "请以完整应用模式运行 Switch 配套程序后刷新"; return; }
        if (vm.CrossSave.TargetGames.Length == 0) { vm.CrossSave.FileStatus = "尚未发现对应的 Switch 游戏；请先刷新主机资料"; return; }
        if (vm.CrossSave.Users.Length == 0) { vm.CrossSave.FileStatus = "此游戏尚未发现存档；请先在 Switch 游戏中保存一次，再刷新主机资料"; return; }
        if (key == null) { vm.CrossSave.FileStatus = "请选择 Switch 游戏和用户"; return; }
        if (vm.Page != "cross-save") { vm.CrossSave.FileStatus = "进入跨平台页面后读取槽位"; return; }
        var wait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); crossDiscoveryWait = wait;
        crossPendingKey = key; vm.CrossSave.FilesLoading = true; vm.CrossSave.FileStatus = "正在读取 Switch 槽位…";
        crossFileDiscoveryTask = DiscoverCrossSaveFilesAsync(key, crossDiscoveryGeneration, client, device, vm.CrossSave.SelectedUser!.Live!, wait);
    }
    private async Task DiscoverCrossSaveFilesAsync(string key, long generation, DeviceClient peer, DeviceInfo identity, SaveInfo save, CancellationTokenSource wait)
    {
        var locked = false;
        bool Current() => generation == crossDiscoveryGeneration && vm.Online && ReferenceEquals(peer, client) && key == CurrentCrossSaveKey();
        try
        {
            await jobsGate.WaitAsync(wait.Token); locked = true;
            if (!Current()) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var files = await CrossSaveDiscovery.ListFilesAsync(peer, identity, save, timeout.Token);
            if (!Current()) return;
            vm.CrossSave.SetFiles(files); crossDiscoveredKey = key;
            var count = files.Count(x => x.CanCompare);
            vm.CrossSave.FileStatus = files.Length == 0 ? "此用户存档中没有文件；请先在游戏中保存一次" : count == 0 ? "文件已发现，但均为空或超过 64 MiB 解析上限；可在存档页面备份完整存档" : count == 1 ? "已选中唯一的有效文件；读取并比较时会先备份双端" : $"发现 {count} 个可读取文件；请选择要比较的进度文件或槽位";
            vm.CrossSave.SwitchSummary = $"{save.UserName} · 已读取文件目录，尚未读取存档内容";
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested || lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or OperationCanceledException)
        { if (Current()) vm.CrossSave.FileStatus = "读取槽位失败：" + ex.Message + " · 可点击重新读取槽位"; }
        finally
        {
            if (locked) jobsGate.Release();
            if (generation == crossDiscoveryGeneration) { crossPendingKey = null; vm.CrossSave.FilesLoading = false; RefreshCrossSaveAvailability(); }
            if (ReferenceEquals(crossDiscoveryWait, wait)) crossDiscoveryWait = null;
            wait.Dispose();
        }
    }
    private void BrowsePcSave()
    {
        var adapter = vm.CrossSave.Adapter ?? throw new IOException("请选择游戏适配器"); var pick = new OpenFileDialog { Title = "选择当前有效的游戏存档（单个槽位）", Filter = "存档文件|*.*" };
        if (pick.ShowDialog(this) != true) return;
        if (!fixtureMode)
        {
            var known = SteamSaves.Discover(SteamRoot(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)).FirstOrDefault(p => p.AdapterId == adapter.Id && p.Path.Equals(Path.GetFullPath(pick.FileName), StringComparison.OrdinalIgnoreCase));
            if (known != null) { vm.CrossSave.Candidates = vm.CrossSave.Candidates.Append(known).DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray(); vm.CrossSave.SelectedPc = known; return; }
        }
        var slot = Dialogs.Text(this, "存档槽位", "此文件对应的游戏槽位", "1"); if (string.IsNullOrWhiteSpace(slot)) return;
        var found = new PcSaveCandidate(adapter.Id, pick.FileName, "Windows " + Environment.UserName, slot.Trim(), "手动选择，请核对游戏实际使用的位置");
        vm.CrossSave.Candidates = vm.CrossSave.Candidates.Append(found).DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray(); vm.CrossSave.SelectedPc = found;
    }
    private async Task WithCrossSaveActionAsync(Func<Task> action)
    {
        if (crossActionBusy || Volatile.Read(ref activeJobs) != 0) throw new IOException("请等待当前任务完成后再操作存档");
        crossActionBusy = true; RefreshCrossSaveAvailability();
        try { await action(); }
        finally { crossActionBusy = false; RefreshCrossSaveAvailability(); }
    }
    private Task BackupCrossSavePcAsync() => WithCrossSaveActionAsync(async () =>
    {
        var adapter = vm.CrossSave.Adapter ?? throw new IOException("请选择游戏");
        var pc = vm.CrossSave.SelectedPc ?? throw new IOException("请选择电脑存档");
        if (pc.AdapterId != adapter.Id) throw new IOException("电脑存档与所选游戏不匹配，请重新选择");
        SteamSaves.DemandClosed(adapter.Id, false);
        var snapshot = await RunJob("备份电脑存档 · " + adapter.Name, async (_, ct) =>
        {
            var file = await StableSaveFile.ReadAsync(pc.Path, ct); var format = TryInspect(adapter, file.Bytes, SavePlatform.Steam);
            var reading = new SaveReading(file.Fingerprint, DateTimeOffset.UtcNow, format.GameSavedAt ?? file.ModifiedAt, format.GameSavedAt == null ? SaveTimeSource.FileModified : SaveTimeSource.GameEmbedded, format.Format, format.Version, pc.Slot, format.Summary);
            return await new PcSnapshotStore(Path.Combine(settingsStore.Root, "Phase2", "Backups")).BackupAsync(pc, file, reading, settings.Retention, ct);
        });
        vm.Notify("电脑存档已备份 · 原进度保留", snapshot.Directory);
    });
    private Task ReadCrossSaveAsync() => WithCrossSaveActionAsync(ReadCrossSaveCoreAsync);
    private async Task ReadCrossSaveCoreAsync()
    {
        var adapter = vm.CrossSave.Adapter ?? throw new IOException("请选择适配器"); var pc = vm.CrossSave.SelectedPc ?? throw new IOException("请选择电脑存档");
        lastCross = null; vm.CrossSave.CanMigrate = false;
        var save = vm.CrossSave.SelectedUser?.Live ?? throw new IOException("请选择当前 Switch 游戏的用户存档");
        var (_, identity) = RequireDevice("saves"); var selectedFile = vm.CrossSave.SelectedSwitchFile ?? throw new IOException("请先选择 Switch 进度文件或槽位");
        var contextKey = CurrentCrossSaveKey() ?? throw new IOException("Switch 游戏与所选适配器不匹配，请重新选择");
        if (pc.AdapterId != adapter.Id || vm.CrossSave.FilesLoading || !vm.CrossSave.FilesReady || contextKey != crossDiscoveredKey)
            throw new IOException("请先完成当前游戏和用户的槽位读取，再选择对应存档");
        SteamSaves.DemandClosed(adapter.Id, false);
        var result = await RunJob("读取双端存档 · " + adapter.Name, async (job, ct) =>
        {
            var (peer, info) = RequireDevice("saves");
            if (info.DeviceId != identity.DeviceId || info.Environment != identity.Environment) throw new IOException("主机环境已变化");
            var local = await StableSaveFile.ReadAsync(pc.Path, ct);
            var localInfo = TryInspect(adapter, local.Bytes, SavePlatform.Steam);
            var localReading = new SaveReading(local.Fingerprint, DateTimeOffset.UtcNow, localInfo.GameSavedAt ?? local.ModifiedAt, localInfo.GameSavedAt == null ? SaveTimeSource.FileModified : SaveTimeSource.GameEmbedded, localInfo.Format, localInfo.Version, pc.Slot, localInfo.Summary);
            await new PcSnapshotStore(Path.Combine(settingsStore.Root, "Phase2", "Backups")).BackupAsync(pc, local, localReading, settings.Retention, ct);
            phase2.Put("pc-observations", Fingerprints.Of(System.Text.Encoding.UTF8.GetBytes(pc.Path)), localReading);
            SaveDeviceInspection? before = null, after = null;
            var args = new { key = save.Key, titleId = save.TitleId, userId = save.UserId, environment = save.Environment };
            if (info.Capabilities.Contains("saves.inspect")) before = await peer.QueryAsync<SaveDeviceInspection>("saves.inspect", args, ct);
            var snapshot = await new SnapshotStore(settings.BackupDirectory).BackupAsync(peer, info, save, Progress(job), ct);
            if (before != null)
            {
                after = await peer.QueryAsync<SaveDeviceInspection>("saves.inspect", args, ct);
                if (!Fingerprints.Equal(before.Fingerprint, after.Fingerprint)) throw new IOException("读取期间 Switch 存档发生变化，请关闭游戏并重试");
            }
            var available = snapshot.Manifest.Files.Select(f => new CrossSaveFile(f.Path, f.Size)).ToArray();
            var chosen = available.Any(f => f.Path == selectedFile && f.CanCompare) ? selectedFile : null;
            var installedGame = (await peer.QueryAsync<GameTitle[]>("titles.list", ct: ct)).SingleOrDefault(g => g.TitleId == save.TitleId);
            if (chosen == null) return (Local: local, Snapshot: snapshot, Inspection: after, Files: available, Chosen: (string?)null, Remote: (byte[]?)null, Game: installedGame);
            using var zip = ZipFile.OpenRead(snapshot.Path); await using var input = zip.GetEntry("data/" + chosen)!.Open(); using var output = new MemoryStream(); await input.CopyToAsync(output, ct);
            return (Local: local, Snapshot: snapshot, Inspection: after, Files: available, Chosen: chosen, Remote: (byte[]?)output.ToArray(), Game: installedGame);
        });
        if (CurrentCrossSaveKey() != contextKey || vm.CrossSave.Adapter?.Id != adapter.Id || vm.CrossSave.SelectedPc != pc || vm.CrossSave.SelectedSwitchFile != selectedFile)
        { vm.Notify("双端读取与备份已完成；当前选择已变化，请为新选择重新比较"); return; }
        vm.CrossSave.SetFiles(result.Files); vm.CrossSave.SelectedSwitchFile = result.Chosen; crossDiscoveredKey = contextKey;
        var pcInfo = TryInspect(adapter, result.Local.Bytes, SavePlatform.Steam);
        var pcTime = pcInfo.GameSavedAt ?? result.Local.ModifiedAt; var pcTimeLabel = pcInfo.GameSavedAt == null ? "有效文件修改时间" : "游戏记录的保存时间";
        vm.CrossSave.PcSummary = $"{pc.Label}\n{pcTimeLabel}  {pcTime.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n{pcInfo.Summary}\n存档格式字段  {pcInfo.Version}";
        if (result.Remote == null)
        {
            vm.CrossSave.FileStatus = "存档文件列表已变化；两端备份已保留，请重新选择进度文件再比较";
            vm.CrossSave.SwitchSummary = "已备份完整 Switch 存档；原选择的文件已不存在或无法解析"; return;
        }
        var nxInfo = TryInspect(adapter, result.Remote, SavePlatform.Switch); var observed = DateTimeOffset.UtcNow;
        DateTimeOffset? saved = nxInfo.GameSavedAt ?? (result.Inspection?.SavedAtUnixSeconds is >= 946684800 and < 4102444800 ? DateTimeOffset.FromUnixTimeSeconds(result.Inspection.SavedAtUnixSeconds.Value) : null);
        var timeSource = nxInfo.GameSavedAt != null ? SaveTimeSource.GameEmbedded : saved != null ? SaveTimeSource.SaveCommit : SaveTimeSource.ObservedOnly;
        var pcReading = new SaveReading(result.Local.Fingerprint, observed, pcTime, pcInfo.GameSavedAt == null ? SaveTimeSource.FileModified : SaveTimeSource.GameEmbedded, pcInfo.Format, pcInfo.Version, pc.Slot, pcInfo.Summary);
        var nxReading = new SaveReading(Fingerprints.Of(result.Remote), observed, saved, timeSource, nxInfo.Format, nxInfo.Version, result.Chosen!, nxInfo.Summary, result.Inspection?.CommitId);
        vm.CrossSave.SwitchSummary = $"{save.UserName} · {result.Chosen}\n{nxReading.TimeLabel}  {nxReading.DisplayTime.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n{nxInfo.Summary}\n存档版本  {nxInfo.Version}";
        var pairId = Fingerprints.Of(JsonSerializer.SerializeToUtf8Bytes(new { adapter = adapter.Id, pc.Path, pc.Owner, pc.Slot, identity.DeviceId, identity.Environment, save.TitleId, save.UserId, save.Key, result.Chosen }, Wire.Json));
        var previous = phase2.Get<SavePair>("pairs", pairId);
        var pair = new SavePair(pairId, adapter.Id, pc.Owner, pc.Path, pc.Slot, identity.DeviceId, identity.Environment, save.TitleId, save.UserId, save.Key, result.Chosen!, result.Chosen!, previous?.Value.Baseline);
        phase2.Put("pairs", pairId, pair, previous?.Revision ?? 0);
        var history = phase2.List<MigrationOperation>("migrations");
        var latest = history.Where(o => o.PairId == pairId).OrderByDescending(o => o.CompletedAt ?? o.CreatedAt).FirstOrDefault();
        if (latest is { State: MigrationState.Complete, Source: SavePlatform.Switch } && !Fingerprints.Equal(pcReading.Fingerprint, latest.OutputFingerprint) && Fingerprints.Equal(pcReading.Fingerprint, latest.TargetFingerprint))
        {
            var record = phase2.Get<MigrationOperation>("migrations", latest.Id)!.Value;
            phase2.Put("migrations", latest.Id, latest with { State = MigrationState.NeedsRecovery, AttentionReason = "SteamSessionChange", Error = "电脑回到覆盖前的存档内容，可能受到云端替换；旧版本与导入版本均已保留，请比较后手动选择" }, record.Revision);
            history = phase2.List<MigrationOperation>("migrations"); LoadPendingDiagnostics(); steamWatchesDirty = true;
        }
        pcReading = MigrationLineage.CarryOriginalTime(pcReading, pairId, SavePlatform.Steam, history); nxReading = MigrationLineage.CarryOriginalTime(nxReading, pairId, SavePlatform.Switch, history);
        vm.CrossSave.PcSummary = $"{pc.Label}\n{ReadingText(pcReading)}\n{pcInfo.Summary}\n存档格式字段  {pcInfo.Version}";
        vm.CrossSave.SwitchSummary = $"{save.UserName} · {result.Chosen}\n{ReadingText(nxReading)}\n{nxInfo.Summary}\n存档格式字段  {nxInfo.Version}";
        GameVersionContext? versions = null;
        if (!fixtureMode && SteamRoot() is {} steam)
        {
            var builds = (await Task.Run(() => SteamSaves.InstalledBuilds(steam), lifetime.Token)).Where(b => b.AppId == adapter.SteamAppId).ToArray(); var build = builds.Length == 1 ? builds[0] : null;
            var appVersion = result.Game?.Components.Where(c => c.Kind is "Base" or "Update").Select(c => (uint?)c.Version).Max();
            if (build != null) versions = new(build.BuildId, build.Branch, save.TitleId, appVersion);
        }
        if (CurrentCrossSaveKey() != contextKey || vm.CrossSave.Adapter?.Id != adapter.Id || vm.CrossSave.SelectedPc != pc || vm.CrossSave.SelectedSwitchFile != result.Chosen) { vm.CrossSave.Invalidate(); return; }
        var nxDlc = result.Game?.Components.Where(c => c.Kind is "DLC" or "DataPatch").Select(c => c.TitleId + ":" + c.Version).ToArray() ?? [];
        var rule = CompatibilityCatalog.Released.Find(adapter.Id, pcInfo, nxInfo, pcInfo.DlcMarkers, nxDlc, versions);
        LoadLocalLists();
        if (CurrentCrossSaveKey() != contextKey || vm.CrossSave.SelectedPc != pc || vm.CrossSave.SelectedSwitchFile != result.Chosen || crossDiscoveredKey != contextKey || !vm.CrossSave.FilesReady)
        { vm.CrossSave.Invalidate(); return; }
        lastCross = new(pair, adapter, result.Local, result.Remote, result.Snapshot, save, result.Inspection, identity, pcReading, nxReading, pcInfo, nxDlc, versions, rule);
        vm.CrossSave.Comparison = SaveComparison.Compare(pair.Baseline, pcReading, nxReading).Explanation;
        if (history.Any(o => o.PairId == pairId && o.State == MigrationState.NeedsRecovery && o.AttentionReason == "SteamSessionChange")) vm.CrossSave.Comparison = "Steam 会话后的电脑文件变化需要核对；两端副本已保留，请手动选择来源";
        vm.CrossSave.Gate = "此版本组合尚未进入迁移支持清单；双端存档已分别备份，原进度保留";
        vm.CrossSave.CanMigrate = pc.KnownLocation && rule != null && pcInfo.Fidelity != MigrationFidelity.Unsupported && nxInfo.Fidelity != MigrationFidelity.Unsupported && result.Game?.Components.Any(c => c.Kind == "Base") == true && result.Inspection != null && identity.Capabilities.Contains("restore.atomic.v1") && SaveComparison.Compare(pair.Baseline, pcReading, nxReading).State != ComparisonState.Unchanged;
        if (vm.CrossSave.CanMigrate) vm.CrossSave.Gate = rule!.Fidelity == MigrationFidelity.Full ? "此版本组合支持完整续玩；只写入所选槽位，来源保留" : "仅永久进度迁移；当前一局会重置，确认前会列出影响";
        vm.CrossSave.FileStatus = "已完成双端备份与比较；选择变化后需重新比较";
        vm.Notify("双端比较完成 · 原进度保留", Path.Combine(settingsStore.Root, "Phase2", "Backups"));
    }
    private static string ReadingText(SaveReading reading) => $"{reading.TimeLabel}  {reading.DisplayTime.LocalDateTime:yyyy-MM-dd HH:mm:ss}" + (reading.MigratedAt is {} imported ? $"\n迁移时间  {imported.LocalDateTime:yyyy-MM-dd HH:mm:ss}" : "");
    private Task MigrateCrossSaveAsync(SavePlatform source) => WithCrossSaveActionAsync(() => MigrateCrossSaveCoreAsync(source));
    private async Task MigrateCrossSaveCoreAsync(SavePlatform source)
    {
        await ReadCrossSaveCoreAsync(); var context = lastCross ?? throw new IOException("请完成双端读取与配对");
        if (!vm.CrossSave.CanMigrate || context.Rule == null) throw new NotSupportedException(vm.CrossSave.Gate);
        SteamSaves.DemandClosed(context.Adapter.Id, source == SavePlatform.Switch);
        var workspace = new MigrationWorkspace(phase2, Path.Combine(settingsStore.Root, "Phase2"), CompatibilityCatalog.Released);
        var operation = await RunJob("准备存档迁移", async (_, ct) => await workspace.PrepareAsync(context.Pair, context.Adapter, source, context.Pc.Bytes, context.Nx, context.PcFormat.DlcMarkers, context.NxDlc, ct, context.Versions, context.PcReading, context.NxReading));
        var action = operation.Fidelity == MigrationFidelity.PermanentProgressOnly ? "仅迁移永久进度" : "备份目标并迁移";
        if (!Dialogs.Confirm(this, action, $"游戏  {context.Adapter.Name}\n用户  {context.Save.UserName}\n系统  {context.Device.Environment}\n目标  {(source == SavePlatform.Steam ? "Switch" : "电脑")} · 槽位 {context.Pair.PcSlot} / {context.Pair.SwitchSlot}\n\n{string.Join("\n", operation.Effects)}\n\n来源与覆盖前目标副本已保留。", action))
        { var record = phase2.Get<MigrationOperation>("migrations", operation.Id)!.Value; phase2.Put("migrations", operation.Id, operation with { State = MigrationState.Abandoned }, record.Revision); return; }
        await RunJob(action + " · " + context.Adapter.Name, async (job, ct) =>
        {
            var (peer, info) = RequireDevice("saves.inspect");
            await ValidateCrossVersionsAsync(context, peer, ct);
            if (source == SavePlatform.Steam)
                await workspace.CommitSwitchAsync(operation.Id, context.Pair, peer, info, context.Save, context.TargetSnapshot.Path, context.Inspection!.Fingerprint, new SnapshotStore(settings.BackupDirectory), () => { SteamSaves.DemandClosed(context.Adapter.Id, false); return Task.CompletedTask; }, Progress(job), ct);
            else
                await workspace.CommitPcAsync(operation.Id, () => { SteamSaves.DemandClosed(context.Adapter.Id, true); return Task.CompletedTask; }, ct, async () =>
                {
                    await ValidateCrossVersionsAsync(context, peer, ct);
                    var inspected = await peer.QueryAsync<SaveDeviceInspection>("saves.inspect", new { key = context.Save.Key, titleId = context.Save.TitleId, userId = context.Save.UserId, environment = context.Device.Environment }, ct);
                    if (!Fingerprints.Equal(inspected.Fingerprint, context.Inspection!.Fingerprint)) throw new IOException("Switch 来源在确认后发生变化，请重新比较");
                });
            return true;
        });
        CompleteReviewedCloudChanges(context.Pair.Id, operation.Id); vm.CrossSave.CanMigrate = false; vm.Notify("迁移完成 · 来源与保护快照均保留", operation.RecoveryDirectory); await ReadCrossSaveCoreAsync();
    }
    private static async Task ValidateCrossVersionsAsync(CrossContext context, DeviceClient peer, CancellationToken ct)
    {
        var info = await peer.QueryAsync<DeviceInfo>("device.info", ct: ct);
        if (info.DeviceId != context.Device.DeviceId || info.Environment != context.Device.Environment) throw new IOException("确认后主机或系统环境已变化");
        var game = (await peer.QueryAsync<GameTitle[]>("titles.list", ct: ct)).SingleOrDefault(g => g.TitleId == context.Pair.TitleId);
        var version = game?.Components.Where(c => c.Kind is "Base" or "Update").Select(c => (uint?)c.Version).Max();
        var dlc = game?.Components.Where(c => c.Kind is "DLC" or "DataPatch").Select(c => c.TitleId + ":" + c.Version).Order(StringComparer.Ordinal).ToArray() ?? [];
        var steam = SteamRoot(); var builds = steam == null ? [] : SteamSaves.InstalledBuilds(steam).Where(b => b.AppId == context.Adapter.SteamAppId).ToArray(); var build = builds.Length == 1 ? builds[0] : null;
        if (context.Versions == null || build == null || build.BuildId != context.Versions.SteamBuildId || build.Branch != context.Versions.SteamBranch || version != context.Versions.SwitchContentVersion || !dlc.SequenceEqual(context.NxDlc.Order(StringComparer.Ordinal)))
            throw new IOException("游戏版本、分支或 DLC 在确认后发生变化，请重新比较");
    }
    private static SlotInspection TryInspect(ISaveAdapter adapter, byte[] bytes, SavePlatform platform)
    {
        try { return adapter.Inspect(bytes, platform); }
        catch (Exception e) when (e is InvalidDataException or JsonException or NotSupportedException)
        { return new("unrecognized", "unknown", "文件已备份，格式检查：" + e.Message, MigrationFidelity.Unsupported); }
    }
}
internal static class ArrayChoice
{
    internal static T? SingleOrDefaultIfOne<T>(this T[] values) where T : class => values.Length == 1 ? values[0] : null;
}
