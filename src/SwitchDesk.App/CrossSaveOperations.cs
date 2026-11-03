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
    private void InitializePhase2()
    {
        ReloadContentSources();
        LoadPendingDiagnostics();
        vm.CrossSave.SelectionChanged += DiscoverPcSaves;
        vm.Bind("CrossSaveOpen", _ => { vm.CrossSave.Open = true; vm.CrossSave.Adapter ??= SaveAdapters.All[0]; DiscoverPcSaves(); }, _ => vm.Library.Selected?.TitleId != null);
        vm.Bind("CrossSaveClose", _ => vm.CrossSave.Open = false, _ => Volatile.Read(ref activeJobs) == 0);
        vm.Bind("CrossSaveBrowse", _ => BrowsePcSave());
        vm.Bind("CrossSaveRead", _ => _ = Guard(ReadCrossSaveAsync), _ => vm.CrossSave.Adapter != null && vm.CrossSave.SelectedPc != null && vm.Saves.Selected?.Live != null && vm.Online && Volatile.Read(ref activeJobs) == 0);
        // The shipped compatibility catalog is empty until version-specific gameplay evidence is recorded.
        bool CanMigrate() => vm.CrossSave.CanMigrate && vm.Online && Volatile.Read(ref activeJobs) == 0;
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
    private static string? SteamRoot()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("SteamPath") as string;
    }
    private void DiscoverPcSaves()
    {
        var adapter = vm.CrossSave.Adapter; vm.CrossSave.Invalidate(); vm.CrossSave.SwitchFiles = []; vm.CrossSave.SelectedSwitchFile = null;
        if (adapter == null) { vm.CrossSave.Candidates = []; return; }
        if (fixtureMode || Environment.GetCommandLineArgs().Contains("--smoke-test")) { vm.CrossSave.Candidates = []; return; }
        var candidates = SteamSaves.Discover(SteamRoot(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        vm.CrossSave.Candidates = candidates.Where(x => x.AdapterId == adapter.Id).ToArray(); vm.CrossSave.SelectedPc = vm.CrossSave.Candidates.FirstOrDefault();
        vm.CrossSave.PcSummary = vm.CrossSave.Candidates.Length > 0 ? "已找到有效位置；读取前请退出游戏" : "没有找到有效存档，可手动选择；不会使用旧备份或按时间猜测文件";
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
    private async Task ReadCrossSaveAsync()
    {
        var adapter = vm.CrossSave.Adapter ?? throw new IOException("请选择适配器"); var pc = vm.CrossSave.SelectedPc ?? throw new IOException("请选择电脑存档");
        lastCross = null; vm.CrossSave.CanMigrate = false;
        var save = SelectedSave(); var (_, identity) = RequireDevice("saves"); var selectedFile = vm.CrossSave.SelectedSwitchFile;
        if (save.TitleId != vm.Library.Selected?.TitleId) throw new IOException("游戏选择已变化，请重新打开比较面板");
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
            var available = snapshot.Manifest.Files.Where(f => f.Size is > 0 and <= StableSaveFile.MaximumBytes).Select(f => f.Path).ToArray();
            var chosen = available.Contains(selectedFile, StringComparer.Ordinal) ? selectedFile : available.SingleOrDefaultIfOne();
            var installedGame = (await peer.QueryAsync<GameTitle[]>("titles.list", ct: ct)).SingleOrDefault(g => g.TitleId == save.TitleId);
            if (chosen == null) return (Local: local, Snapshot: snapshot, Inspection: after, Files: available, Chosen: (string?)null, Remote: (byte[]?)null, Game: installedGame);
            using var zip = ZipFile.OpenRead(snapshot.Path); await using var input = zip.GetEntry("data/" + chosen)!.Open(); using var output = new MemoryStream(); await input.CopyToAsync(output, ct);
            return (Local: local, Snapshot: snapshot, Inspection: after, Files: available, Chosen: chosen, Remote: (byte[]?)output.ToArray(), Game: installedGame);
        });
        if (vm.Library.Selected?.TitleId != save.TitleId || vm.CrossSave.Adapter?.Id != adapter.Id || vm.CrossSave.SelectedPc?.Path != pc.Path || vm.Saves.Selected?.Live?.Key != save.Key || vm.CrossSave.SelectedSwitchFile != selectedFile)
        { vm.Notify("双端读取与备份已完成；当前选择已变化，请为新选择重新比较"); return; }
        vm.CrossSave.SwitchFiles = result.Files; vm.CrossSave.SelectedSwitchFile = result.Chosen;
        var pcInfo = TryInspect(adapter, result.Local.Bytes, SavePlatform.Steam);
        var pcTime = pcInfo.GameSavedAt ?? result.Local.ModifiedAt; var pcTimeLabel = pcInfo.GameSavedAt == null ? "有效文件修改时间" : "游戏记录的保存时间";
        vm.CrossSave.PcSummary = $"{pc.Label}\n{pcTimeLabel}  {pcTime.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n{pcInfo.Summary}\n存档格式字段  {pcInfo.Version}";
        if (result.Remote == null) { vm.CrossSave.SwitchSummary = "已备份完整 Switch 存档，请选择其中的进度文件，再点击读取并比较"; return; }
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
        if (vm.Library.Selected?.TitleId != save.TitleId || vm.CrossSave.Adapter?.Id != adapter.Id || vm.CrossSave.SelectedPc?.Path != pc.Path) { vm.CrossSave.Invalidate(); return; }
        var nxDlc = result.Game?.Components.Where(c => c.Kind is "DLC" or "DataPatch").Select(c => c.TitleId + ":" + c.Version).ToArray() ?? [];
        var rule = CompatibilityCatalog.Released.Find(adapter.Id, pcInfo, nxInfo, pcInfo.DlcMarkers, nxDlc, versions);
        lastCross = new(pair, adapter, result.Local, result.Remote, result.Snapshot, save, result.Inspection, identity, pcReading, nxReading, pcInfo, nxDlc, versions, rule);
        LoadLocalLists();
        vm.CrossSave.Comparison = SaveComparison.Compare(pair.Baseline, pcReading, nxReading).Explanation;
        if (history.Any(o => o.PairId == pairId && o.State == MigrationState.NeedsRecovery && o.AttentionReason == "SteamSessionChange")) vm.CrossSave.Comparison = "Steam 会话后的电脑文件变化需要核对；两端副本已保留，请手动选择来源";
        vm.CrossSave.Gate = "此版本组合尚未进入迁移支持清单；双端存档已分别备份，原进度保留";
        vm.CrossSave.CanMigrate = pc.KnownLocation && rule != null && pcInfo.Fidelity != MigrationFidelity.Unsupported && nxInfo.Fidelity != MigrationFidelity.Unsupported && result.Game?.Components.Any(c => c.Kind == "Base") == true && result.Inspection != null && identity.Capabilities.Contains("restore.atomic.v1") && SaveComparison.Compare(pair.Baseline, pcReading, nxReading).State != ComparisonState.Unchanged;
        if (vm.CrossSave.CanMigrate) vm.CrossSave.Gate = rule!.Fidelity == MigrationFidelity.Full ? "此版本组合支持完整续玩；只写入所选槽位，来源保留" : "仅永久进度迁移；当前一局会重置，确认前会列出影响";
        vm.Notify("双端比较完成 · 原进度保留", Path.Combine(settingsStore.Root, "Phase2", "Backups"));
    }
    private static string ReadingText(SaveReading reading) => $"{reading.TimeLabel}  {reading.DisplayTime.LocalDateTime:yyyy-MM-dd HH:mm:ss}" + (reading.MigratedAt is {} imported ? $"\n迁移时间  {imported.LocalDateTime:yyyy-MM-dd HH:mm:ss}" : "");
    private async Task MigrateCrossSaveAsync(SavePlatform source)
    {
        await ReadCrossSaveAsync(); var context = lastCross ?? throw new IOException("请完成双端读取与配对");
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
        CompleteReviewedCloudChanges(context.Pair.Id, operation.Id); vm.CrossSave.CanMigrate = false; vm.Notify("迁移完成 · 来源与保护快照均保留", operation.RecoveryDirectory); await ReadCrossSaveAsync();
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
