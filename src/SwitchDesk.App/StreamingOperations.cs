using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SwitchDesk.Core;

namespace SwitchDesk.App;

public partial class MainWindow
{
    private readonly SemaphoreSlim streamGate = new(1);
    private UsbStreamingSession? streamSession;
    private CancellationTokenSource? streamCancellation;
    private bool closingApplication, closeReady;

    private void ShowStreamGuide()
    {
        vm.StreamGuide = true;
        if (streaming) return;
        StreamCondition.Text = Volatile.Read(ref activeJobs) != 0 ? "传输任务仍在运行，完成或取消后再开始投屏。" : "";
        StreamStatusText.Text = "准备就绪 · 在主机打开支持录像的游戏";
        StreamVideoHost.Visibility = Visibility.Collapsed; StreamIdlePanel.Visibility = Visibility.Visible;
        StreamViewportHeading.Text = "一根 USB 线，在这里看游戏画面";
        StreamViewportHint.Text = "电脑客户端随完整运行包提供，无需另行下载。\n首次使用需在 Switch 上启用 SysDVR USB 模式。";
        try
        {
            _ = UsbStreamingRuntime.Resolve(AppContext.BaseDirectory, settings.SysDvrClient);
            StreamRuntimeLabel.Text = string.IsNullOrWhiteSpace(settings.SysDvrClient) ? "内置 SysDVR 6.3 · USB 数据线 · 720p / 30fps" : "自定义官方 SysDVR 客户端 · USB 数据线";
        }
        catch (IOException ex) { StreamRuntimeLabel.Text = "USB 投屏组件未就绪"; StreamCondition.Text = ex.Message; }
    }

    private async Task StreamAsync()
    {
        await streamGate.WaitAsync(lifetime.Token);
        try
        {
            if (streaming || closingApplication) return;
            if (Volatile.Read(ref activeJobs) != 0) { StreamCondition.Text = "请等待当前任务结束，再开始 USB 投屏。"; return; }
            // Resolve the complete bundled runtime before releasing the user's management session.
            var executable = UsbStreamingRuntime.Resolve(AppContext.BaseDirectory, settings.SysDvrClient);
            streaming = true; vm.StreamGuide = true; StreamCondition.Text = ""; StreamAudioEnabled.IsEnabled = false;
            CommandManager.InvalidateRequerySuggested();
            StreamStatusText.Text = "正在切换 USB 模式…";
            await jobsGate.WaitAsync(lifetime.Token);
            try
            {
                if (Volatile.Read(ref activeJobs) != 0) throw new IOException("后台任务正在结束，请稍后再开始投屏。");
                if (client is { IsBroken: false } peer)
                {
                    try
                    {
                        using var release = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); release.CancelAfter(TimeSpan.FromSeconds(5));
                        await peer.CallAsync("device.release", ct: release.Token);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException)
                    { Log("结束 USB 管理会话：" + ex.Message); }
                }
                await DisconnectAsync();
            }
            finally { jobsGate.Release(); }
            streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            StreamViewportHeading.Text = "等待游戏画面";
            StreamViewportHint.Text = "管理连接已释放。请在主机 SysDVR 设置中选择 USB，\n再打开支持录像的游戏；HOME 和系统设置不在采集范围内。";
            StreamStatusText.Text = "等待主机切换到 SysDVR USB 模式…";
            SetConnectionMessage("USB 投屏模式 · 管理连接已释放");
            Log("已结束管理会话；等待 SysDVR USB 设备后启动内置客户端。");
            _ = WaitAndStartStreamAsync(executable, StreamAudioEnabled.IsChecked == true, streamCancellation);
        }
        catch (Exception ex)
        {
            if (streamSession != null)
            {
                try { await streamSession.DisposeAsync(); streamSession = null; }
                catch (Exception stopError)
                {
                    StreamCondition.Text = ex.Message + "；客户端未能停止：" + stopError.Message;
                    StreamStatusText.Text = "客户端仍待清理，请点击停止投屏重试";
                    Log(StreamCondition.Text); CommandManager.InvalidateRequerySuggested(); return;
                }
            }
            streamCancellation?.Dispose(); streamCancellation = null;
            streaming = false; StreamAudioEnabled.IsEnabled = true; CommandManager.InvalidateRequerySuggested();
            StreamCondition.Text = ex.Message; StreamStatusText.Text = "USB 投屏未启动";
            Log("USB 投屏启动失败：" + ex.Message);
        }
        finally { streamGate.Release(); }
    }

    private async Task WaitAndStartStreamAsync(string executable, bool audioEnabled, CancellationTokenSource cancellation)
    {
        var ct = cancellation.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (await Task.Run(UsbStreamingRuntime.IsUsbDevicePresent, ct)) break;
                await Task.Delay(500, ct);
            }
            await streamGate.WaitAsync(ct);
            UsbStreamingSession session;
            try
            {
                if (ct.IsCancellationRequested || !streaming || closingApplication || !ReferenceEquals(streamCancellation, cancellation)) return;
                session = UsbStreamingSession.Start(executable, "SwitchDesk USB " + Guid.NewGuid().ToString("N"), audioEnabled);
                streamSession = session;
                StreamStatusText.Text = "已检测到 SysDVR USB · 正在连接游戏画面";
            }
            finally { streamGate.Release(); }
            await ObserveStreamAsync(session, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(streamCancellation, cancellation) && !closingApplication)
            {
                Log("连接内置投屏客户端失败：" + ex.Message);
                await StopStreamAsync(false, "USB 投屏未连接：" + ex.Message);
            }
        }
    }

    private async Task ObserveStreamAsync(UsbStreamingSession session, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew(); var attached = false; var waitingExplained = false;
        try
        {
            while (!ct.IsCancellationRequested && ReferenceEquals(streamSession, session))
            {
                if (session.HasExited)
                {
                    var details = session.OutputTail.Trim();
                    var text = attached ? "投屏客户端已退出；请在主机 SysDVR 设置中选择“停止串流”，再打开 SwitchDesk 配套。" : "尚未连接到 SysDVR 视频。请检查 Switch 模块、USB 模式、驱动和兼容游戏。";
                    if (details.Length > 0) Log("投屏客户端诊断：" + details);
                    await StopStreamAsync(false, text);
                    return;
                }
                var videoWindow = session.TryFindVideoWindow();
                if (!attached && videoWindow != 0)
                {
                    StreamVideoHost.Visibility = Visibility.Visible;
                    await Dispatcher.InvokeAsync(() => { UpdateLayout(); }, DispatcherPriority.Loaded, ct);
                    if (ct.IsCancellationRequested || !ReferenceEquals(streamSession, session)) return;
                    if (StreamVideoHost.HostHandle != 0)
                    {
                        StreamVideoHost.Attach(videoWindow, session.ProcessId);
                        StreamIdlePanel.Visibility = Visibility.Collapsed;
                        StreamStatusText.Text = "USB 投屏窗口已连接 · 画面与声音由 SysDVR 提供";
                        StreamCondition.Text = ""; attached = true;
                        Log("SysDVR 原生视频窗口已嵌入 SwitchDesk；画面内容仍需实机确认。");
                    }
                }
                if (!attached && !waitingExplained && timer.Elapsed > TimeSpan.FromSeconds(12))
                {
                    waitingExplained = true;
                    StreamCondition.Text = "仍在等待视频窗口：请在 Switch 启用 SysDVR USB 并打开兼容游戏。首次使用还需按官方说明配置 USB 驱动。";
                }
                await Task.Delay(200, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(streamSession, session) && !closingApplication)
            {
                Log("USB 投屏显示失败：" + ex.Message);
                await StopStreamAsync(false, "无法嵌入投屏窗口：" + ex.Message);
            }
        }
    }

    private async Task StopStreamAsync(bool closeOverlay = false, string? message = null)
    {
        await streamGate.WaitAsync();
        try
        {
            var session = streamSession;
            var cancellation = streamCancellation;
            cancellation?.Cancel();
            StreamVideoHost.Visibility = Visibility.Collapsed;
            try
            {
                // Stop our process first: destroying a host with a live native child can destroy that child.
                if (session != null) await session.DisposeAsync();
            }
            catch (Exception ex)
            {
                StreamCondition.Text = "客户端尚未停止，USB 管理保持暂停：" + ex.Message;
                StreamStatusText.Text = "停止未完成 · 可再次点击停止投屏";
                CommandManager.InvalidateRequerySuggested(); throw;
            }
            StreamVideoHost.Detach(); cancellation?.Dispose(); streamSession = null; streamCancellation = null;
            var wasStreaming = streaming; streaming = false; StreamAudioEnabled.IsEnabled = true;
            StreamIdlePanel.Visibility = Visibility.Visible;
            StreamViewportHeading.Text = "USB 投屏已停止";
            StreamViewportHint.Text = "开始投屏可再次连接游戏画面。\n恢复管理前，请在主机 SysDVR 设置中选择“停止串流”，\n再打开 SwitchDesk 配套。";
            StreamStatusText.Text = "投屏已停止 · 主机关闭 SysDVR USB 后可恢复管理";
            StreamCondition.Text = message ?? "";
            if (wasStreaming) { SetConnectionMessage("投屏已停止 · 主机先关闭 SysDVR USB"); Log("PC 投屏客户端已停止；主机 USB 模式需在 SysDVR 设置中关闭。"); }
            if (closeOverlay) vm.StreamGuide = false;
            CommandManager.InvalidateRequerySuggested();
        }
        finally { streamGate.Release(); }
    }

    private void OnOpenStreamingSetup(object sender, RoutedEventArgs e)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "StreamingSetup");
        if (!Directory.Exists(directory)) { StreamCondition.Text = "此构建缺少 Switch 端安装资源，请使用随附 StreamingSetup 的完整运行包。"; return; }
        Reveal(directory);
    }
}
