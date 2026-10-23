using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
}
