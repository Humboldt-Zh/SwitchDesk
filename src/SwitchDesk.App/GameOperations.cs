using System.IO;
using System.Windows;
using Microsoft.Win32;
using SwitchDesk.Core;

namespace SwitchDesk.App;
public partial class MainWindow
{
    private string AutoKey(SaveInfo save, DeviceInfo info) => info.DeviceId + "|" + info.Environment + "|" + save.Key;
    private SaveInfo SelectedSave() => vm.Saves.Selected?.Live ?? throw new IOException("请选择此游戏的主机存档");
    private void UpdateAutoPolicy()
    { vm.Saves.SetAutomatic(vm.Saves.Selected?.Live is {} save && device != null && settings.AutoBackupKeys.Contains(AutoKey(save, device))); UpdateSettingsText(); }
    private void SetAutoPolicy(bool enabled)
    {
        if (device == null || vm.Saves.Selected?.Live is not {} save) { vm.Saves.SetAutomatic(false); return; }
        var keys = settings.AutoBackupKeys.ToHashSet(); var key = AutoKey(save, device); if (enabled) keys.Add(key); else keys.Remove(key);
        settings = settings with { AutoBackupKeys = keys.ToArray() }; settingsStore.Save(settings); UpdateSettingsText();
    }
    private void ScheduleAutoBackups()
    {
        if (device == null) return; var targets = saves.Where(s => settings.AutoBackupKeys.Contains(AutoKey(s, device))).ToArray(); if (targets.Length == 0) return;
        _ = Guard(async () => { foreach (var save in targets) await BackupAsync(save); vm.Notify($"自动备份完成 · {targets.Length} 项", settings.BackupDirectory); });
    }
    private Task<Snapshot> BackupAsync(SaveInfo save) => RunJob("备份 · " + save.GameName, async (job, ct) =>
    {
        var (peer, info) = RequireDevice("saves"); var store = new SnapshotStore(settings.BackupDirectory);
        var snapshot = await store.BackupAsync(peer, info, save, Progress(job), ct); store.Prune(settings.Retention, ct, snapshot.Manifest); _ = Dispatcher.BeginInvoke(LoadLocalLists); return snapshot;
    }, async () => { var result = await BackupAsync(save); vm.Notify("已备份", Path.GetDirectoryName(result.Path)!); });
    private async Task RestoreAsync()
    {
        var save = SelectedSave(); var snapshot = SnapshotsGrid.SelectedItem as Snapshot ?? throw new IOException("请选择历史版本"); var (_, info) = RequireDevice("saves"); var m = snapshot.Manifest;
        if (m.DeviceId != info.DeviceId || m.Environment != info.Environment || m.TitleId != save.TitleId || m.UserId != save.UserId || m.SaveType != save.Type) throw new IOException("所选版本与当前主机、系统环境或用户不匹配");
        if (!Dialogs.Confirm(this, "恢复存档", $"游戏  {save.GameName}\n用户  {save.UserName}\n系统  {info.Environment}\n版本  {snapshot.TimeText}\n\n先完整备份当前存档，再恢复所选版本。", "备份当前存档并恢复")) return;
        await RunJob("恢复 · " + save.GameName, async (job, ct) => { var (peer, current) = RequireDevice("saves"); await new SnapshotStore(settings.BackupDirectory).RestoreAsync(peer, current, save, snapshot.Path, Progress(job), ct); _ = Dispatcher.BeginInvoke(LoadLocalLists); return true; });
        vm.Notify("存档已恢复 · 恢复前备份保留在 Recovery", Path.Combine(settings.BackupDirectory, "Recovery"));
    }
    private async Task VerifySnapshotAsync()
    { var snapshot = SnapshotsGrid.SelectedItem as Snapshot ?? throw new IOException("请选择历史版本"); await RunJob("校验存档快照", async (_, ct) => await SnapshotStore.VerifyAsync(snapshot.Path, ct)); vm.Notify("快照校验通过", Path.GetDirectoryName(snapshot.Path)!); }
    private async Task PickInstallAsync()
    { var pick = new OpenFileDialog { Filter = "Switch 安装包|*.nsp;*.xci;*.nsz;*.xcz", Multiselect = true }; if (pick.ShowDialog(this) == true) await ReviewInstallAsync(pick.FileNames); }
    private string TargetStorage => InstallStorage.SelectedValue?.ToString() ?? "SdCard";
    private async Task ReviewInstallAsync(string[] paths)
    {
        if (batchRunning || reviewing) throw new IOException("请先等待当前安装清单处理完毕");
        InstallOverlay.Visibility = Visibility.Visible; installEntries.Clear(); reviewedDevice = null;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        { var item = new InstallEntry(path); if (!new[] { ".nsp", ".xci", ".nsz", ".xcz" }.Contains(Path.GetExtension(path).ToLowerInvariant())) item.State = "格式不支持"; installEntries.Add(item); }
        await InspectInstallEntriesAsync();
    }
    private async Task InspectInstallEntriesAsync()
    {
        reviewing = true; StartInstall.IsEnabled = false; InstallError.Text = "";
        try
        {
            var (_, info) = RequireDevice("inspect"); reviewedDevice = info; InstallContext.Text = $"{info.Model} · {info.Environment} · 开始前将重新核对主机、文件和空间";
            await RunJob("整理安装清单", async (_, ct) =>
            {
                foreach (var entry in installEntries.ToArray().Where(x => x.State != "格式不支持" && x.State != "完成" && x.State != "已跳过"))
                {
                    try
                    {
                        var before = new FileInfo(entry.Path); var length = before.Length; var time = before.LastWriteTimeUtc;
                        var (peer, _) = RequireDevice("inspect"); var package = await Transfers.InspectAsync(peer, entry.Path, ct); var source = new InstallSource(entry.Path, length, time, package); source.Validate();
                        var existing = titles.SelectMany(g => g.Components).Where(x => package.Components.Any(c => c.TitleId.Equals(x.TitleId, StringComparison.OrdinalIgnoreCase))).ToArray();
                        await Dispatcher.InvokeAsync(() => { entry.Source = source; entry.State = existing.Length > 0 ? "已有 " + string.Join(" / ", existing.Select(x => x.KindText + " v" + x.Version)) : "就绪"; entry.Refresh(); });
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Dispatcher.InvokeAsync(() => { entry.Source = null; entry.State = ex.Message; entry.Refresh(); }); }
                }
                var (current, _) = RequireDevice(); var volumes = await current.QueryAsync<StorageInfo[]>("storages.list", ct: ct); await Dispatcher.InvokeAsync(() => vm.Storages = volumes); return true;
            });
        }
        catch (Exception ex) { InstallError.Text = ex.Message; }
        finally { reviewing = false; UpdateInstallSummary(); }
    }
    private void UpdateInstallSummary()
    {
        if (!initialized) return;
        var pending = installEntries.Where(x => x.State != "完成" && x.State != "已跳过").ToArray(); var required = pending.Sum(x => x.Source?.Package.RequiredBytes ?? 0); var target = vm.Storages.FirstOrDefault(x => x.Id == TargetStorage);
        InstallSummary.Text = $"预计需要 {Display.Size(required)} · 可用 {(target == null ? "待连接" : Display.Size(target.FreeBytes))}"; StartInstall.Content = $"开始安装 {pending.Length} 项";
        StartInstall.IsEnabled = !batchRunning && !reviewing && pending.Length > 0 && pending.All(x => x.Source != null) && vm.Online && required <= (target?.FreeBytes ?? 0);
        if (target != null && required > target.FreeBytes) InstallError.Text = "目标空间不足，请更换存储位置或整理主机空间";
        else if (InstallError.Text.StartsWith("目标空间不足", StringComparison.Ordinal)) InstallError.Text = "";
        InstallStorage.IsEnabled = !batchRunning && !reviewing;
        RetryInstall.Visibility = SkipInstall.Visibility = installEntries.Any(x => x.State.StartsWith("失败", StringComparison.Ordinal)) ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task ExecuteInstallAsync(bool skipFailed = false)
    {
        if (batchRunning || reviewing) return;
        if (skipFailed) foreach (var entry in installEntries.Where(x => x.State.StartsWith("失败", StringComparison.Ordinal))) entry.State = "已跳过";
        var pending = installEntries.Where(x => x.State != "完成" && x.State != "已跳过").ToArray(); var targetId = TargetStorage;
        if (pending.Length == 0) { UpdateInstallSummary(); return; }
        batchRunning = true; InstallError.Text = ""; InstallStorage.IsEnabled = StartInstall.IsEnabled = RetryInstall.IsEnabled = SkipInstall.IsEnabled = false;
        try
        {
            var reviewed = reviewedDevice ?? throw new IOException("请先重新识别清单");
            await RunJob("批量安装 · " + pending.Length + " 项", async (job, ct) =>
            {
                var (peer, _) = RequireDevice("install"); var current = await peer.QueryAsync<DeviceInfo>("device.info", ct: ct); var volumes = await peer.QueryAsync<StorageInfo[]>("storages.list", ct: ct);
                var target = volumes.FirstOrDefault(v => v.Id == targetId) ?? throw new IOException("目标存储不可用"); InstallBatch.Validate(pending.Select(x => x.Source ?? throw new IOException("存在未识别文件")), reviewed, current, target);
                foreach (var entry in pending)
                {
                    try { entry.Source!.Validate(); await Dispatcher.InvokeAsync(() => entry.State = "安装中"); await Transfers.InstallAsync(peer, entry.Path, targetId, Progress(job), ct); await Dispatcher.InvokeAsync(() => entry.State = "完成"); }
                    catch (Exception ex) { await Dispatcher.InvokeAsync(() => entry.State = "失败 · " + ex.Message); throw; }
                }
                await RefreshDeviceAsync(ct); return true;
            });
            vm.Notify($"批量安装完成 · {pending.Length} 项"); InstallOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { InstallError.Text = ex.Message + "；已完成项目保留，后续项目等待重试或跳过。"; }
        finally { batchRunning = false; RetryInstall.IsEnabled = SkipInstall.IsEnabled = true; UpdateInstallSummary(); }
    }
    private async void OnStartInstall(object sender, RoutedEventArgs e) => await ExecuteInstallAsync();
    private async void OnRetryInstall(object sender, RoutedEventArgs e) => await ExecuteInstallAsync();
    private async void OnSkipInstall(object sender, RoutedEventArgs e) => await ExecuteInstallAsync(true);
    private async void OnReviewAgain(object sender, RoutedEventArgs e) { if (!batchRunning && !reviewing) await InspectInstallEntriesAsync(); }
    private void OnInstallStorage(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { if (initialized) UpdateInstallSummary(); }
    private void OnCloseInstall(object sender, RoutedEventArgs e) { if (!batchRunning && !reviewing) InstallOverlay.Visibility = Visibility.Collapsed; else InstallError.Text = "请在任务栏取消，或等待当前任务完成。"; }
    private void OnInstallDragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void OnInstallDrop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await Guard(() => ReviewInstallAsync(paths)); }
    private async Task ExportAsync()
    {
        var card = vm.Library.Selected ?? throw new IOException("请选择游戏"); var game = card.Console ?? throw new IOException("游戏尚未安装在主机");
        var components = card.Components.Where(x => x.Selected).Select(x => x.Value).ToArray(); if (components.Length == 0) throw new IOException("请勾选要导出的本体、更新或 DLC"); var directory = settings.ExportDirectory;
        foreach (var component in components) await RunJob("导出 · " + game.Name + " · " + component.KindText, async (job, ct) => { var (peer, info) = RequireDevice("export"); await Transfers.ExportGameAsync(peer, info, game, component, directory, Progress(job), ct); return true; });
        await ScanLibraryAsync(directory, false); vm.Notify($"已导出 {components.Length} 个组件", directory);
    }
    private LibraryItem SelectedLocal() => LocalFiles.SelectedItem as LibraryItem ?? throw new IOException("请选择电脑中的文件");
    private async Task AddLibraryAsync()
    { var pick = new OpenFolderDialog { Title = "添加电脑游戏目录" }; if (pick.ShowDialog(this) == true) { settings = settings with { LibraryDirectory = pick.FolderName }; settingsStore.Save(settings); await ScanLibraryAsync(pick.FolderName); } }
    private async Task ScanLibraryAsync(string directory, bool notify = true)
    { await RunJob("扫描电脑游戏目录", async (_, ct) => { await library.ScanAsync(directory, ct); await Dispatcher.InvokeAsync(LoadLocalLists); return true; }); if (notify) vm.Notify("电脑游戏库已更新", directory); }
    private async Task VerifyLocalAsync()
    { var item = SelectedLocal(); var result = await RunJob("校验 · " + item.Name, async (_, ct) => { var value = await library.VerifyAsync(item, ct); await Dispatcher.InvokeAsync(LoadLocalLists); return value; }, VerifyLocalAsync); vm.Notify(result.Status, Path.GetDirectoryName(item.Path)!); }
    private async Task InspectLocalAsync()
    {
        var item = SelectedLocal(); await RunJob("识别 · " + item.Name, async (_, ct) =>
        { var (peer, _) = RequireDevice("inspect"); var result = await Transfers.InspectAsync(peer, item.Path, ct); if (!result.Recognized) throw new IOException(result.Detail ?? "无法识别安装包");
          library.Upsert(item with { Name = result.GameName ?? item.Name, TitleId = result.TitleId, Kind = string.Join("+", result.Components.Select(x => x.Kind).Distinct()), Version = string.Join(",", result.Components.Select(x => x.Version).Distinct()), Status = "已识别" }); await Dispatcher.InvokeAsync(LoadLocalLists); return true; }); vm.Notify("安装包已识别");
    }
}
