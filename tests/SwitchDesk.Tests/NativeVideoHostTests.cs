using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using SwitchDesk.App;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static Task NativeVideoHostTests() => Test("native video host owns HWND attachment, pixel resize and safe detach", RunNativeVideoHostCheckAsync);

    // Program.Main routes --native-video-fixture here before starting its test suite.
    // This is a hidden/offscreen window owned by this test only, never a USB player.
    private static int RunNativeVideoFixture()
    {
        var window = NativeHostFixture.CreateWindowEx(0, "static", "SwitchDesk native video test", 0x00CF0000,
            -32000, -32000, 640, 360, 0, 0, 0, 0);
        if (window == 0) return 2;
        Console.WriteLine(window.ToInt64().ToString(CultureInfo.InvariantCulture)); Console.Out.Flush();
        var watch = Stopwatch.StartNew();
        try
        {
            while (NativeHostFixture.IsWindow(window) && watch.Elapsed < TimeSpan.FromSeconds(30))
            {
                while (NativeHostFixture.PeekMessage(out var message, 0, 0, 0, 1))
                { NativeHostFixture.TranslateMessage(ref message); NativeHostFixture.DispatchMessage(ref message); }
                Thread.Sleep(5);
            }
            return 0;
        }
        finally { if (NativeHostFixture.IsWindow(window)) NativeHostFixture.DestroyWindow(window); }
    }

    private static async Task RunNativeVideoHostCheckAsync()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Test process path unavailable"))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add("--native-video-fixture");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start native window fixture");
        nint video = 0;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert(long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value != 0, "fixture did not return a HWND");
            video = (nint)value;
            var owner = process.Id;
            Assert(NativeVideoHost.IsWindowOwnedByProcess(video, owner), "fixture HWND does not belong to child process");
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var dispatcher = Dispatcher.CurrentDispatcher;
                NativeVideoHost? host = null; Window? window = null;
                _ = dispatcher.InvokeAsync(async () =>
                {
                    Exception? failure = null;
                    try
                    {
                        // Do not create another Application: existing WPF integration
                        // owns the process's single Application instance later in Run.
                        host = new NativeVideoHost();
                        window = new Window { Content = host, Width = 640, Height = 360, Left = -32000, Top = -32000,
                            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
                        window.Show(); window.UpdateLayout();
                        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        Assert(host.HostHandle != 0 && !host.HasChild, "native host did not create its own window");
                        var style = NativeHostFixture.GetStyle(video);
                        var wrongPidRejected = false;
                        try { host.Attach(video, owner + 1); } catch (ArgumentException) { wrongPidRejected = true; }
                        Assert(wrongPidRejected && !host.HasChild && NativeHostFixture.GetParent(video) == 0, "wrong-PID attachment changed an unrelated window");

                        host.Attach(video, owner);
                        Assert(host.HasChild && host.ChildHandle == video && host.ChildProcessId == owner, "host did not retain exact child ownership");
                        Assert(NativeHostFixture.GetParent(video) == host.HostHandle && (NativeHostFixture.GetStyle(video) & 0x40000000) != 0, "player did not become a native child");
                        Assert((NativeHostFixture.GetStyle(video) & 0x00C00000) == 0, "player caption was retained");
                        CheckNativeVideoSize(host, video);
                        window.Width = 853; window.Height = 517; window.UpdateLayout();
                        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        host.ResizeChild(); CheckNativeVideoSize(host, video);

                        host.Detach();
                        Assert(!host.HasChild && host.ChildHandle == 0 && NativeHostFixture.IsWindow(video), "detach destroyed or retained the player's window");
                        Assert(NativeHostFixture.GetParent(video) == 0 && NativeHostFixture.GetStyle(video) == style, "detach did not restore top-level styles/parent");
                        Assert(!NativeHostFixture.IsWindowVisible(video), "default detach exposed an external player window");

                        host.Attach(video, owner);
                        var ownedHost = host.HostHandle;
                        window.Close(); window = null; host.Dispose();
                        Assert(NativeHostFixture.IsWindow(video) && NativeHostFixture.GetParent(video) == 0, "disposing the host closed the caller-owned window");
                        Assert(!NativeHostFixture.IsWindow(ownedHost), "host HWND leaked on ordinary disposal");
                        Assert(NativeHostFixture.GetStyle(video) == style, "host disposal lost player styles");
                    }
                    catch (Exception error) { failure = error; }
                    finally
                    {
                        try { window?.Close(); host?.Dispose(); } catch (Exception cleanup) { failure ??= cleanup; }
                        if (failure == null) completion.TrySetResult(); else completion.TrySetException(failure);
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                });
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            await completion.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            if (video != 0 && NativeVideoHost.IsWindowOwnedByProcess(video, process.Id)) NativeHostFixture.PostMessage(video, 0x0010, 0, 0);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await process.WaitForExitAsync(stop.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        }
    }

    private static void CheckNativeVideoSize(NativeVideoHost host, nint video)
    {
        var parentRead = NativeHostFixture.GetClientRect(host.HostHandle, out var parent);
        var childRead = NativeHostFixture.GetClientRect(video, out var child);
        Assert(parentRead && childRead, "could not inspect native client sizes");
        // Both RECTs are native physical pixels. This is not a real monitor DPI-switch test.
        Assert(parent.Right > 0 && parent.Bottom > 0 && child.Right == parent.Right && child.Bottom == parent.Bottom, "player did not fill the host client area");
    }

    private static class NativeHostFixture
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct Message { internal nint Window; internal uint Id; internal nuint WParam; internal nint LParam; internal uint Time; internal int X, Y; internal uint Private; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] internal static extern nint GetParent(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(nint window, out Rect rect);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetStyleValue(nint window, int index);
        internal static uint GetStyle(nint window) => unchecked((uint)GetStyleValue(window, -16));
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll")] internal static extern nint DispatchMessage(ref Message message);
    }
}
