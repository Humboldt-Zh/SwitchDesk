using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SwitchDesk.Core;

namespace SwitchDesk.App;
public partial class MainWindow
{
    private async Task RenderSmokeAsync()
    {
        var args = Environment.GetCommandLineArgs(); var output = Path.GetFullPath(args[Array.IndexOf(args, "--smoke-test") + 1]); Directory.CreateDirectory(output);
        var report = new List<string>();
        var bindingErrors = new BindingErrorListener(); PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors); PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("UI check failed: " + name); report.Add("PASS " + name); }
        async Task Idle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout(); }
        async Task Render(string name, double scale = 1)
        {
            await Idle(); var visual = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * scale), (int)Math.Ceiling(visual.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        }
        ThemeManager.Apply("Light"); await Render("00-first-connect-light");
        vm.Fixture = true;
        device = new("UI-FIXTURE", "Switch OLED · 测试", "测试 19.0.1", "测试 1.8.0", "0.2.0-test", "emuMMC · 测试环境", "USB 3.0 · 测试", [], true); UpdateDevice(device); synced = DateTimeOffset.Now; vm.Connection = "离线记录 · 界面测试";
        string[] names = ["远山来信", "星海漫游", "暮色回廊", "夏日岛屿", "纸间旅程", "环形竞速", "深林物语", "雨后咖啡"];
        titles = Enumerable.Range(0, 500).Select(i => new GameTitle($"01000000{i:X8}", $"{i + 1:000}  {names[i % names.Length]}", "SwitchDesk 原创界面夹具", "1.2.0", i % 19 == 0 ? "已归档" : "已安装", i % 19 == 0 ? [] : [new($"01000000{i:X8}", "Base", 0, "SdCard", 5L << 30), new($"01000000{i + 2048:X8}", "Update", 65536, "SdCard", 1L << 30), new($"01000000{i + 4096:X8}", "DLC", 0, "BuiltInUser", 64L << 20)])).ToArray();
        saves = [new("fixture", titles[1].TitleId, titles[1].Name, "USER-A", "小林 · 测试用户", "Account", 2L << 20, device.Environment), new("shared", titles[1].TitleId, titles[1].Name, "", "共享存档", "Device", 1024, device.Environment)];
        vm.Storages = [new("SdCard", "SD 卡", 256L << 30, 120L << 30), new("BuiltInUser", "当前 emuMMC 用户分区", 29L << 30, 18L << 30)];
        var locals = new[] { new LibraryItem(Path.Combine(output, "test.nsp"), titles[1].Name, titles[1].TitleId, "Base", "0", 5L << 30, null, "已识别 · 测试"), new LibraryItem(Path.Combine(output, "unknown.xcz"), "未识别文件.xcz", null, "", "", 2L << 30, null, "待识别") };
        vm.Library.SaveTitles = saves.Select(x => x.TitleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        vm.Library.Merge(titles, locals); foreach (var card in vm.Library.All.Where(x => !x.Unknown)) card.Icon = FixtureIcon(Array.IndexOf(titles, card.Console));
        vm.Saves.SetData(saves, Enumerable.Range(0, 5).Select(i => new Snapshot(Path.Combine(output, "fixture-" + i + ".zip"), new SaveManifest(1, device.DeviceId, device.Environment, titles[1].TitleId, titles[1].Name, "USER-A", "小林 · 测试用户", "Account", DateTimeOffset.Now.AddDays(-i), [new("main.dat", 2L << 20, "fixture")], "fixture"))).ToArray());
        vm.Library.Selected = vm.Library.All.First(x => x.TitleId == titles[1].TitleId); UpdateDetails(); UpdateSyncText();
        await Idle(); Check(vm.Library.All.Count == 501, "500 games plus unknown file; known local content merged");
        Check(Descendants<Button>(Gallery).Count() < 70, "recycling gallery realizes fewer than 70 cards");
        var watch = Stopwatch.StartNew(); vm.Library.Search = "星海"; await Idle(); watch.Stop(); Check(vm.Library.Filtered.Count == 63 && watch.ElapsedMilliseconds < 1000, $"500-item search {watch.ElapsedMilliseconds} ms"); vm.Library.Search = "";
        await Render("01-library-light"); ThemeManager.Apply("Dark"); await Render("02-library-dark");
        Navigate("saves"); await Render("03-saves-dark"); Check(vm.Saves.History.Count == 5, "save history filters by selected game and user");
        var selectedKey = vm.Library.Selected!.Key; var search = vm.Library.Search; await DisconnectAsync(); Check(vm.Library.Selected.Key == selectedKey && vm.Library.Search == search && vm.Library.All.Count == 501, "disconnect preserves inventory, filters and selection");
        Navigate("files"); FilesGrid.ItemsSource = new[] { new RemoteEntry("switch", "/switch", true, 0), new RemoteEntry("config", "/config", true, 0), new RemoteEntry("测试文件.bin", "/测试文件.bin", false, 128L << 20) }; await Render("04-files-dark");
        Navigate("console"); await Render("05-console-dark"); Navigate("settings"); await Render("06-settings-dark");
        vm.RecordLog("界面测试 · 连接断开，保留缓存；未进行实机连接。", "界面测试 · 连接断开，保留缓存；未进行实机连接。"); Navigate("diagnostics"); await Render("07-diagnostics-dark");
        Navigate("library"); vm.Tasks.Add(new() { Name = "备份 · 星海漫游（夹具）", State = "完成", Indeterminate = false, Percent = 100, CanCancel = false });
        var commit = new Job { Name = "恢复 · 不可取消状态（夹具）" }; commit.Report(new(0, 0, "正在写回存档，请保持连接")); vm.Tasks.Add(commit); await Idle(); Check(!QueuePanel.IsVisible, "task history stays out of normal pages"); Navigate("diagnostics"); DiagnosticTabs.SelectedIndex = 0;
        Check(!commit.CanCancel && commit.Indeterminate, "save commit indeterminate and non-cancellable"); await Render("08-tasks-dark");
        Check(QueuePanel.IsVisible, "tasks are available inside diagnostics");
        Check(JobActionsColumn.Visibility == Visibility.Collapsed, "no empty action column for completed or locked tasks");
        var completedTask = vm.Tasks.Items.First(x => x.State == "完成");
        Check(!completedTask.CanCancel && completedTask.DiagnosticDetail.Length == 0, "completed task hides inapplicable actions and duplicate result");
        commit.State = "完成"; commit.Indeterminate = false; commit.Percent = 100; Navigate("library");
        var failure = new Job { Name = "读取主机资料 · 测试", State = "需要处理", Detail = "USB 写入中断 · 测试", CanCancel = false };
        vm.Tasks.Add(failure);
        vm.RecordLog("12:00:01  失败：读取主机资料 · USB 写入中断 · 测试", "失败：读取主机资料 · USB 写入中断 · 测试");
        vm.RecordLog("12:00:02  USB 写入中断 · 测试", "USB 写入中断 · 测试");
        Check(!vm.ConnectionEvents.Any(x => x.Contains("USB 写入中断")), "task failure is not repeated as a connection event");
        vm.Notify(failure.Detail); Navigate("diagnostics"); ThemeManager.Apply("Light"); await Idle();
        Check(!NoticeBox.IsVisible, "diagnostics does not repeat the task error as a banner");
        await Render("diagnostics-tasks-light"); vm.Notify(""); Navigate("library");
        InstallOverlay.Visibility = Visibility.Visible; InstallContext.Text = "界面测试清单 · Switch OLED / emuMMC · 不执行真实安装";
        installEntries.Add(new("原创建模测试.nsp") { State = "就绪" }); installEntries.Add(new("测试更新.nsz") { State = "空间不足（测试）" }); InstallSummary.Text = "预计需要 8 GiB · 可用 6 GiB"; InstallError.Text = "目标空间不足，请更换存储位置"; StartInstall.Content = "开始安装 2 项"; StartInstall.IsEnabled = false;
        await Render("09-install-review-dark"); InstallOverlay.Visibility = Visibility.Collapsed;
        vm.StreamGuide = true; await Render("10-stream-guide-dark"); vm.StreamGuide = false;
        DetailsTabs.SelectedIndex = 0;
        foreach (var theme in new[] { "Light", "Dark" }) foreach (var width in new[] { 1050, 1280, 1920 })
        {
            ThemeManager.Apply(theme); Width = width; Height = 820; LayoutDetails(); await Idle();
            Check(DetailColumn.ActualWidth >= 300 && LibraryPage.ActualWidth >= 450, $"{theme} {width} DIP detail and library fit");
            Check(vm.Library.Rows[0].Cards.Sum(x => x.Width + 16) <= LibraryPage.ActualWidth + 1, $"{theme} {width} DIP cards stay in viewport");
            var exportBounds = ExportButton.TransformToAncestor(Root).TransformBounds(new Rect(ExportButton.RenderSize));
            Check(exportBounds.Bottom <= Root.ActualHeight && ExportButton.ActualWidth > 100, $"{theme} {width} DIP export action stays visible");
            await Render($"matrix-{theme}-{width}-100"); await Render($"matrix-{theme}-{width}-200", 2);
        }
        Width = 1280; DetailsTabs.SelectedIndex = 0; ThemeManager.Apply("Light"); await Render("preview-light"); ThemeManager.Apply("Dark"); await Render("preview-dark");
        Check(!Descendants<TextBlock>(Details).Any(x => x.Text.Contains("SaveChoice {", StringComparison.Ordinal)), "user display contains no internal object dump");
        Check(bindingErrors.Messages.Count == 0, "no WPF binding errors: " + string.Join(" | ", bindingErrors.Messages));
        report.Add("DPI images are WPF render-scale checks at 96/192 DPI, not an OS monitor DPI transition test."); report.Add("All art, records and tasks above are labelled UI fixtures. No Switch hardware validation.");
        File.WriteAllLines(Path.Combine(output, "smoke-result.txt"), report);
    }
    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
    private static ImageSource FixtureIcon(int index)
    {
        string[] palette = ["#4B675E", "#314F70", "#5E4461", "#BA8058", "#5D7272", "#BC6F4C", "#3D6859", "#8B6853"];
        var drawing = new DrawingGroup(); using (var dc = drawing.Open())
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[Math.Abs(index) % palette.Length]));
            dc.DrawRectangle(brush, null, new Rect(0, 0, 256, 256)); dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(60, 255, 244, 205)), null, new Point(184, 66), 46, 46);
            var path = Geometry.Parse("M0,220 L70,85 135,190 182,125 256,230 256,256 0,256Z"); dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, path);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)), 2), new Point(22, 230), new Point(234, 230));
        }
        drawing.Freeze(); return new DrawingImage(drawing);
    }
}
