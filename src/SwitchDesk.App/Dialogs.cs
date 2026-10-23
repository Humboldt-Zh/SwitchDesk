using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SwitchDesk.Core;

namespace SwitchDesk.App;
public static class Dialogs
{
    private static Window Shell(Window owner, string title, double width = 560) => new()
    { Owner = owner, Title = title, Style = (Style)Application.Current.FindResource(typeof(Window)), Width = width, SizeToContent = SizeToContent.Height, MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 64), ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
    public static bool Confirm(Window owner, string title, string description, string action)
    {
        var dialog = Shell(owner, title); var body = new StackPanel { Margin = new(24) }; dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = description, Margin = new(0, 20, 0, 24), TextWrapping = TextWrapping.Wrap });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new(0, 0, 12, 0) };
        var accept = new Button { Content = action, Style = (Style)Application.Current.FindResource("Primary") };
        accept.Click += (_, _) => dialog.DialogResult = true; row.Children.Add(cancel); row.Children.Add(accept); body.Children.Add(row);
        cancel.Focus(); return dialog.ShowDialog() == true;
    }
    public static string? Text(Window owner, string title, string label, string initial = "")
    {
        var dialog = Shell(owner, title, 450); var body = new StackPanel { Margin = new(24) }; dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(new TextBlock { Text = label, Margin = new(0, 0, 0, 12) }); var input = new TextBox { Text = initial }; body.Children.Add(input);
        var ok = new Button { Content = "确定", IsDefault = true, Margin = new(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => dialog.DialogResult = true; body.Children.Add(ok); input.Focus();
        return dialog.ShowDialog() == true ? input.Text : null;
    }
    public static AppSettings? Settings(Window owner, AppSettings current)
    {
        var dialog = Shell(owner, "设置", 720); var body = new StackPanel { Margin = new(24) }; dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        TextBox PathRow(string title, string value, bool executable = false)
        {
            body.Children.Add(new TextBlock { Text = title, Margin = new(0, 12, 0, 8) }); var row = new DockPanel(); var input = new TextBox { Text = value };
            var browse = new Button { Content = "选择", Margin = new(10, 0, 0, 0) }; DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(input); body.Children.Add(row);
            browse.Click += (_, _) =>
            {
                if (executable) { var pick = new OpenFileDialog { Filter = "SysDVR 官方客户端|*.exe" }; if (pick.ShowDialog(dialog) == true) input.Text = pick.FileName; }
                else { var pick = new OpenFolderDialog(); if (pick.ShowDialog(dialog) == true) input.Text = pick.FolderName; }
            }; return input;
        }
        var backup = PathRow("存档备份目录", current.BackupDirectory); var library = PathRow("电脑游戏库目录", current.LibraryDirectory);
        var export = PathRow("游戏导出目录", current.ExportDirectory); var player = PathRow("SysDVR 6.3 Windows 客户端", current.SysDvrClient, true);
        body.Children.Add(new TextBlock { Text = "自动备份保留版本数（Recovery 恢复前快照独立保留）", Margin = new(0, 14, 0, 8) });
        var retention = new TextBox { Text = current.Retention.ToString() }; body.Children.Add(retention);
        var links = new Button { Content = "打开 SysDVR 官方下载与配置说明 ↗", Margin = new(0, 16, 0, 0) };
        links.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/exelix11/SysDVR/releases/tag/v6.3") { UseShellExecute = true }); body.Children.Add(links);
        var ok = new Button { Content = "保存", Margin = new(0, 20, 0, 0), IsDefault = true };
        ok.Click += (_, _) => { if (int.TryParse(retention.Text, out var count) && count is >= 1 and <= 1000) dialog.DialogResult = true; else MessageBox.Show(dialog, "保留版本数应为 1–1000"); }; body.Children.Add(ok);
        return dialog.ShowDialog() == true ? current with { BackupDirectory = backup.Text, LibraryDirectory = library.Text, ExportDirectory = export.Text, SysDvrClient = player.Text, Retention = int.Parse(retention.Text) } : null;
    }
}
