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
            window.Show();
            _ = Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert(app.Windows.OfType<MainWindow>().Count() == 1, "test startup created a normal USB-monitoring window");
                    await using var peer = new Peer { Titles = [new(Peer.Save.TitleId, Peer.Save.GameName, "Fixture", "1", "已安装", [new(Peer.Save.TitleId, "Base", 0, "SdCard", 1024)])] };
                    var client = await peer.ConnectAsync(); var vm = Get<ShellViewModel>("vm");
                    Set("client", client); Set("device", Peer.Info); vm.Online = true;
                    await Invoke("RefreshDeviceAsync", CancellationToken.None);
                    vm.Library.Selected = vm.Library.All.Single();
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
                    if (((Border)window.FindName("FixtureBanner")).Child is TextBlock banner) banner.Text = "界面测试数据 · 1 项协议夹具 · 非实机连接";
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
                    vm.Library.Selected = vm.Library.All.Single();
                    var backup = (Task<Snapshot>)Call("BackupAsync", Peer.Save)!; var first = await backup;
                    var duplicate = await (Task<Snapshot>)Call("BackupAsync", Peer.Save)!; Assert(first.Path == duplicate.Path, "normal backup completes without confirmation and deduplicates");
                    Call("SetAutoPolicy", true); Assert(Get<AppSettings>("settings").AutoBackupKeys.Length == 1);
                    vm.Commands["CrossSaveOpen"].Execute(null); vm.CrossSave.Adapter = SaveAdapters.Get("hollow-knight");
                    var pcSlot = Path.Combine(directory, "user1.dat"); var pcBytes = KnightSaveAdapter.EncodePc(KnightJson(4)); await File.WriteAllBytesAsync(pcSlot, pcBytes);
                    var candidate = new PcSaveCandidate("hollow-knight", pcSlot, "Windows fixture", "1", "isolated test slot"); vm.CrossSave.Candidates = [candidate]; vm.CrossSave.SelectedPc = candidate; vm.CrossSave.SelectedSwitchFile = "main.dat";
                    var originalRemote = peer.Files["main.dat"].ToArray(); await Invoke("ReadCrossSaveAsync");
                    Assert(vm.CrossSave.Comparison.Contains("首次配对", StringComparison.Ordinal) && !vm.Commands["CrossSaveToSwitch"].CanExecute(null) && !vm.Commands["CrossSaveToPc"].CanExecute(null), "unadmitted cross-save must remain backup-only");
                    Assert(peer.RestoreCommits == 0 && peer.Files["main.dat"].SequenceEqual(originalRemote) && (await File.ReadAllBytesAsync(pcSlot)).SequenceEqual(pcBytes), "comparison changed an original save");
                    vm.CrossSave.Open = false;
                    var key = vm.Library.Selected!.Key; vm.Library.Search = "测试"; Call("Navigate", "saves");
                    await Invoke("DisconnectAsync"); Assert(!vm.Online && vm.Library.Selected?.Key == key && vm.Library.Search == "测试" && vm.Page == "saves", "offline context changed");
                    Assert(!vm.Commands["Backup"].CanExecute(null) && vm.Saves.History.Count == 1);
                    await using var reconnect = new Peer { Titles = peer.Titles }; var newClient = await reconnect.ConnectAsync(); Set("client", newClient); vm.Online = true;
                    await Invoke("RefreshDeviceAsync", CancellationToken.None); Assert(vm.Library.Selected?.Key == key && vm.Page == "saves" && vm.Library.Search == "测试", "reconnect context changed");
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
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
    private static IEnumerable<T> Walk<T>(DependencyObject root) where T : DependencyObject
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var item in Walk<T>(child)) yield return item; } }
}
