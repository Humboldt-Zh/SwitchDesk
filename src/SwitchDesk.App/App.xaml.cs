using System.Windows;
namespace SwitchDesk.App;
public partial class App : Application
{
    private Mutex? instance;
    public bool IsTestHost { get; set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!IsTestHost && !e.Args.Contains("--smoke-test"))
        {
            instance = new Mutex(true, @"Local\SwitchDesk-Desktop", out var first);
            if (!first) { MessageBox.Show("SwitchDesk 已在运行，请使用已打开的窗口。", "SwitchDesk"); instance.Dispose(); instance = null; Shutdown(); return; }
        }
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            var command = Environment.GetCommandLineArgs(); var index = Array.IndexOf(command, "--smoke-test");
            if (index >= 0)
            {
                System.IO.Directory.CreateDirectory(command[index + 1]);
                System.IO.File.WriteAllText(System.IO.Path.Combine(command[index + 1], "error.txt"), args.Exception.ToString());
                args.Handled = true; Shutdown(2); return;
            }
            MessageBox.Show(args.Exception.Message, "SwitchDesk", MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true;
        };
        if (!IsTestHost) { MainWindow = new MainWindow(); MainWindow.Show(); }
    }
    protected override void OnExit(ExitEventArgs e) { if (instance != null) { instance.ReleaseMutex(); instance.Dispose(); } base.OnExit(e); }
}
