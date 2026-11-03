using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SwitchDesk.App;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;
internal static partial class Program
{
    // Executes the actual window commands on a dispatcher with an isolated data directory
    // and the framed protocol fixture. This never enumerates or talks to USB hardware.
    private static Task RunUiIntegrationAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var app = new SwitchDesk.App.App { IsTestHost = true }; app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var directory = Path.Combine(root, "wpf-integration"); var window = new MainWindow(directory, true);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            T Get<T>(string field) => (T)typeof(MainWindow).GetField(field, flags)!.GetValue(window)!;
            void Set(string field, object value) => typeof(MainWindow).GetField(field, flags)!.SetValue(window, value);
            object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
            async Task Invoke(string name, params object[] args) => await (Task)Call(name, args)!;
            async Task FlushUi() { await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
            async Task WaitForCrossFiles()
            {
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    var discovery = Get<Task>("crossFileDiscoveryTask");
                    await discovery.WaitAsync(TimeSpan.FromSeconds(5)); await FlushUi();
                    if (ReferenceEquals(discovery, Get<Task>("crossFileDiscoveryTask"))) return;
                }
                throw new Exception("cross-save file discovery did not settle");
            }
            window.Show();
            _ = Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert(app.Windows.OfType<MainWindow>().Count() == 1, "test startup created a normal USB-monitoring window");
                    const string crossTitleId = "010089A0197E4000";
                    var crossSaveA = Peer.Save with { Key = "cross-user-a", TitleId = crossTitleId, GameName = "Vampire Survivors", UserName = "跨平台用户 A" };
                    var crossSaveB = crossSaveA with { Key = "cross-user-b", UserId = "USER-B", UserName = "跨平台用户 B" };
                    await using var peer = new Peer
                    {
                        Titles = [new(Peer.Save.TitleId, Peer.Save.GameName, "Fixture", "1", "已安装", [new(Peer.Save.TitleId, "Base", 0, "SdCard", 1024)]),
                            new(crossTitleId, crossSaveA.GameName, "Fixture", "1", "已安装", [new(crossTitleId, "Base", 0, "SdCard", 1024)])],
                        SaveRecords = [Peer.Save, crossSaveA, crossSaveB]
                    };
                    var client = await peer.ConnectAsync(); var vm = Get<ShellViewModel>("vm");
                    Set("client", client); Set("device", Peer.Info); vm.Online = true;
                    await Invoke("RefreshDeviceAsync", CancellationToken.None);
                    vm.Library.Selected = vm.Library.All.Single(x => x.TitleId == Peer.Save.TitleId);
                    Call("UpdateDevice", Peer.Info); vm.Connection = "协议夹具 · 非实机连接";
                    var source = new ContentSource("ui-fixture", "自有来源 · 界面夹具", ContentSourceKind.Http, "https://fixture.invalid/library/");
                    var entry = new ContentEntry(Peer.Save.TitleId, "0100000000000800", "Update", 131072, 1024, Fingerprints.Of([1]), "fixture.nsp");
                    vm.UpdateOffers = [new(new(new(Peer.Save.TitleId, entry.ComponentId, "Update", entry.Version), 65536, UpdateStatus.UpdateAvailable), source, entry)]; vm.SelectedOffer = vm.UpdateOffers[0];
                    vm.UpdateSummary = "界面测试目录 · 非真实更新信息"; vm.ContentSources = [source];
                    await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                    var list = Walk<ListBox>((DependencyObject)window.Content).Single(x => ReferenceEquals(x.ItemsSource, vm.UpdateOffers)); list.BringIntoView();
                    await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                    Assert(Walk<CheckBox>(list).Any(c => c.IsChecked == true && c.Content?.ToString()?.Contains("131072", StringComparison.Ordinal) == true), "update review checkbox or label did not render");
                    Assert(!vm.Commands["InstallOffer"].CanExecute(null), "legacy companion enabled managed install without capability");
                    if (((Border)window.FindName("FixtureBanner")).Child is TextBlock banner) banner.Text = "界面测试数据 · 2 项协议夹具 · 非实机连接";
                    window.UpdateLayout();
                    var visual = (FrameworkElement)window.Content; var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var image = File.Create(Path.Combine(directory, "updates-fixture.png"))) encoder.Save(image);
                    var paths = new[] { "one.nsp", "two.nsz", "three.xci" }.Select(x => Path.Combine(directory, x)).ToArray();
                    foreach (var path in paths) await File.WriteAllBytesAsync(path, [1, 2, 3]);
                    await Invoke("ReviewInstallAsync", (object)paths);
                    var entries = Get<ObservableCollection<InstallEntry>>("installEntries");
                    Assert(entries.Count == 3 && entries.All(e => e.Source != null) && peer.InstalledNames.Count == 0, "review must not start installation");
                    peer.FailInstallation = "two.nsz"; await Invoke("ExecuteInstallAsync", false);
                    Assert(peer.InstalledNames.SequenceEqual(new[] { "one.nsp" }) && entries[1].State.StartsWith("失败", StringComparison.Ordinal) && entries[2].State != "完成", "failure must stop the batch");
                    peer.FailInstallation = null; await Invoke("ExecuteInstallAsync", true);
                    Assert(peer.InstalledNames.SequenceEqual(new[] { "one.nsp", "three.xci" }) && entries[1].State == "已跳过", "skip resumes without reinstalling completed content");
                    vm.Library.Selected = vm.Library.All.Single(x => x.TitleId == Peer.Save.TitleId);
                    var backup = (Task<Snapshot>)Call("BackupAsync", Peer.Save)!; var first = await backup;
                    var duplicate = await (Task<Snapshot>)Call("BackupAsync", Peer.Save)!; Assert(first.Path == duplicate.Path, "normal backup completes without confirmation and deduplicates");
                    Call("SetAutoPolicy", true); Assert(Get<AppSettings>("settings").AutoBackupKeys.Length == 1);
                    await FlushUi();
                    vm.Library.Selected = vm.Library.All.Single(x => x.TitleId == crossTitleId);
                    vm.Commands["CrossSaveOpen"].Execute(null); await WaitForCrossFiles();
                    Assert(vm.Page == "cross-save" && vm.CrossSave.SelectedGame?.AdapterId == "vampire-survivors" && vm.CrossSave.Users.Length == 2, "associated game did not open the independent cross-save page");
                    Assert(vm.CrossSave.SwitchFiles.Order(StringComparer.Ordinal).SequenceEqual(new[] { "main.dat", "nested/config.bin" }) && vm.CrossSave.SelectedSwitchFile == null, "cross-save must discover both real tree files before the first comparison");
                    Assert(peer.ActiveHandleCount == 0 && vm.CrossSave.CanChooseFile, "directory discovery left a handle open or the file selector disabled");

                    // Hold both responses so the old user's result cannot be hidden by
                    // an immediately completed replacement request.
                    var staleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var currentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var releaseStale = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var releaseCurrent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var treeAttempt = 0;
                    peer.BeforeHandleTree = async () =>
                    {
                        var attempt = Interlocked.Increment(ref treeAttempt);
                        if (attempt == 1) { staleEntered.TrySetResult(); await releaseStale.Task; }
                        else if (attempt == 2) { currentEntered.TrySetResult(); await releaseCurrent.Task; }
                    };
                    try
                    {
                        vm.CrossSave.SelectedUser = vm.CrossSave.Users.Single(x => x.UserId == crossSaveB.UserId);
                        var staleDiscovery = Get<Task>("crossFileDiscoveryTask"); await staleEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        vm.CrossSave.SelectedUser = vm.CrossSave.Users.Single(x => x.UserId == crossSaveA.UserId);
                        releaseStale.TrySetResult(); await staleDiscovery.WaitAsync(TimeSpan.FromSeconds(5));
                        await currentEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert(vm.CrossSave.SelectedUser?.UserId == crossSaveA.UserId && vm.CrossSave.SwitchFiles.Length == 0 && vm.CrossSave.FilesLoading, "a stale user's tree filled the replacement user's selector");
                        releaseCurrent.TrySetResult(); await WaitForCrossFiles();
                    }
                    finally { releaseStale.TrySetResult(); releaseCurrent.TrySetResult(); peer.BeforeHandleTree = null; }
                    Assert(peer.ActiveHandleCount == 0 && !client.IsBroken, "changing cross-save users broke the connection or leaked a handle");
                    var pcSlot = Path.Combine(directory, "user1.dat"); var pcBytes = KnightSaveAdapter.EncodePc(KnightJson(4)); await File.WriteAllBytesAsync(pcSlot, pcBytes);
                    var candidate = new PcSaveCandidate("vampire-survivors", pcSlot, "Windows fixture", "1", "isolated unsupported test slot"); vm.CrossSave.Candidates = [candidate]; vm.CrossSave.SelectedPc = candidate; vm.CrossSave.SelectedSwitchFile = "main.dat";
                    Assert(vm.Commands["CrossSaveRead"].CanExecute(null), "discovered file selection did not enable comparison");
                    var originalRemote = peer.Files.ToDictionary(x => x.Key, x => x.Value.ToArray()); var compareOpens = peer.ReadOpens;
                    await Invoke("ReadCrossSaveAsync"); await FlushUi();
                    Assert(vm.CrossSave.Comparison.Contains("首次配对", StringComparison.Ordinal) && !vm.Commands["CrossSaveToSwitch"].CanExecute(null) && !vm.Commands["CrossSaveToPc"].CanExecute(null), "unadmitted cross-save must remain backup-only");
                    Assert(peer.ReadOpens == compareOpens + 1 && peer.ActiveHandleCount == 0, "one comparison must back up once and close the save handle");
                    Assert(peer.RestoreCommits == 0 && peer.WriteOperations == 0 && peer.Files.All(x => x.Value.SequenceEqual(originalRemote[x.Key])) && (await File.ReadAllBytesAsync(pcSlot)).SequenceEqual(pcBytes), "comparison changed an original save");

                    var store = new SnapshotStore(Get<AppSettings>("settings").BackupDirectory);
                    var historicalGames = new List<Snapshot>();
                    for (var i = 1; i <= 5; i++)
                    {
                        var historical = Peer.Save with { Key = "history-" + i, TitleId = $"01000000000100{i:X2}", GameName = "仅备份游戏 " + i };
                        historicalGames.Add(await store.BackupAsync(client, Peer.Info, historical, null, default));
                    }
                    var otherDevice = await store.BackupAsync(client, Peer.Info with { DeviceId = "OTHER-DEVICE" }, Peer.Save, null, default);
                    vm.Library.Selected = vm.Library.All.Single(x => x.TitleId == Peer.Save.TitleId);
                    var key = vm.Library.Selected.Key; vm.Library.Search = "测试"; vm.Library.Source = "电脑";
                    Call("Navigate", "saves"); await FlushUi();
                    var savesPage = (FrameworkElement)window.FindName("SavesPage");
                    Assert(savesPage.IsVisible && !((FrameworkElement)window.FindName("LibraryPage")).IsVisible && vm.Library.Filtered.Count == 0, "save navigation reused the filtered game library");
                    Assert(vm.SaveLibrary.Games.Count == 7 && historicalGames.All(s => vm.SaveLibrary.Games.Any(g => g.TitleId == s.Manifest.TitleId && g.BackupCount == 1 && g.Status == "仅电脑备份")), "existing backup-only games disappeared behind library filters");
                    Assert(historicalGames.All(s => !vm.Library.All.Any(g => g.TitleId == s.Manifest.TitleId)), "save fixture unexpectedly supplied installation files");
                    vm.SaveLibrary.OpenGameCommand.Execute(vm.SaveLibrary.Games.Single(g => g.TitleId == Peer.Save.TitleId));
                    Assert(vm.Commands["RestoreVersion"].CanExecute(first) && !vm.Commands["RestoreVersion"].CanExecute(otherDevice), "restore failed to enforce device identity");
                    vm.SaveLibrary.SelectedSource = vm.SaveLibrary.Sources.Single(s => s.Identity.DeviceId == "OTHER-DEVICE");
                    Assert(vm.SaveLibrary.Versions.Single().Snapshot.Path == otherDevice.Path && !vm.Commands["RestoreVersion"].CanExecute(otherDevice), "foreign-device history became a live restore target");
                    vm.SaveLibrary.BackCommand.Execute(null); vm.SaveLibrary.Search = "仅备份游戏"; vm.SaveLibrary.SourceFilter = "电脑备份";
                    Assert(vm.SaveLibrary.Games.Count == 5, "save page's own filters did not retain the five backup-only games");
                    var timelineSnapshot = historicalGames[2];
                    vm.SaveLibrary.OpenGameCommand.Execute(vm.SaveLibrary.Games.Single(g => g.TitleId == timelineSnapshot.Manifest.TitleId));
                    vm.SaveLibrary.SelectedSnapshot = vm.SaveLibrary.Versions.Single().Snapshot; await FlushUi();
                    var timeline = Walk<ListBox>(savesPage).Single(x => ReferenceEquals(x.ItemsSource, vm.SaveLibrary.Versions));
                    Assert(timeline.IsVisible && timeline.Items.Count == 1 && vm.SaveLibrary.Detail.History.Single().Path == timelineSnapshot.Path, "opening a backup-only game did not render its timeline");
                    ((Border)window.FindName("InstallOverlay")).Visibility = Visibility.Collapsed;
                    vm.StreamGuide = true; await FlushUi();
                    var escape = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.Escape)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                    Call("OnWindowKey", window, escape); await FlushUi();
                    Assert(escape.Handled && !vm.StreamGuide && vm.SaveLibrary.SelectedGame?.TitleId == timelineSnapshot.Manifest.TitleId,
                        $"Escape routing: key={escape.Key}, handled={escape.Handled}, preview={vm.StreamGuide}, title={vm.SaveLibrary.SelectedGame?.TitleId}, expected={timelineSnapshot.Manifest.TitleId}, notice={vm.Notice}");
                    Assert(!vm.Commands["Backup"].CanExecute(null) && !vm.Commands["RestoreVersion"].CanExecute(timelineSnapshot), "history-only timeline enabled a main-console write");
                    await Invoke("DisconnectAsync"); await WaitForCrossFiles();
                    Assert(!vm.Online && vm.Library.Selected?.Key == key && vm.Library.Search == "测试" && vm.Library.Source == "电脑" && vm.Page == "saves", "offline library/page context changed");
                    Assert(vm.SaveLibrary.TimelineOpen && vm.SaveLibrary.Games.Count == 5 && vm.SaveLibrary.SelectedSnapshot?.Path == timelineSnapshot.Path && vm.SaveLibrary.Versions.Single().Snapshot.Path == timelineSnapshot.Path, "offline save timeline or independent filters changed");
                    Assert(!vm.Commands["Backup"].CanExecute(null) && !vm.Commands["RestoreVersion"].CanExecute(timelineSnapshot) && vm.CrossSave.SwitchFiles.Length == 0 && !vm.Commands["CrossSaveRead"].CanExecute(null), "offline state exposed a stale live save");
                    await using var reconnect = new Peer { Titles = peer.Titles, SaveRecords = peer.SaveRecords }; var newClient = await reconnect.ConnectAsync(); Set("client", newClient); vm.Online = true;
                    await Invoke("RefreshDeviceAsync", CancellationToken.None);
                    Assert(vm.Library.Selected?.Key == key && vm.Page == "saves" && vm.Library.Search == "测试" && vm.Library.Source == "电脑", "reconnect library context changed");
                    Assert(vm.SaveLibrary.SelectedGame?.TitleId == timelineSnapshot.Manifest.TitleId && vm.SaveLibrary.SelectedSnapshot?.Path == timelineSnapshot.Path && vm.SaveLibrary.Games.Count == 5, "reconnect lost save timeline selection or filters");
                    reconnect.FreeBytes = 1; await Invoke("ReviewInstallAsync", (object)paths); var start = (Button)window.FindName("StartInstall"); Assert(!start.IsEnabled);
                    await Invoke("ExecuteInstallAsync", false); Assert(reconnect.InstalledNames.Count == 0, "space preflight allowed a write");
                    await Invoke("DisconnectAsync");
                    completion.TrySetResult();
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { window.Close(); app.Shutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
    private static IEnumerable<T> Walk<T>(DependencyObject root) where T : DependencyObject
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var item in Walk<T>(child)) yield return item; } }
}
