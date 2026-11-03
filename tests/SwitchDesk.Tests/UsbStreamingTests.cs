using System.Diagnostics;
using System.IO;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task RunUsbStreamingTestsAsync()
    {
        string FixtureRuntime(string name)
        {
            var directory = Path.Combine(root, "usb-streaming", name); Directory.CreateDirectory(directory);
            foreach (var file in new[] { "SysDVR-Client.exe", "SysDVR-Client.dll", "SysDVR-Client.deps.json", "SysDVR-Client.runtimeconfig.json", "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "clrjit.dll", "System.Private.CoreLib.dll", "runtimes/win-x64/native/avcodec-59.dll", "runtimes/win-x64/native/avdevice-59.dll", "runtimes/win-x64/native/avfilter-8.dll", "runtimes/win-x64/native/avformat-59.dll", "runtimes/win-x64/native/avutil-57.dll", "runtimes/win-x64/native/swresample-4.dll", "runtimes/win-x64/native/swscale-6.dll", "runtimes/win-x64/native/SDL2.dll", "runtimes/win-x64/native/SDL2_image.dll", "runtimes/win-x64/native/libusb-1.0.dll", "runtimes/win-x64/native/cimgui.dll" })
            {
                var target = Path.Combine(directory, file); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, "Fixture only; never executed");
            }
            return Path.Combine(directory, "SysDVR-Client.exe");
        }
        await Test("streaming resolves bundled runtime without a user path and allows explicit override", () => Sync(() =>
        {
            var app = Path.Combine(root, "usb-streaming", "application");
            var bundled = FixtureRuntime(Path.Combine("application", "Streaming", "SysDVR"));
            Assert(UsbStreamingRuntime.Resolve(app) == bundled);
            Assert(UsbStreamingRuntime.Resolve(app, " ") == bundled);
            var selected = FixtureRuntime("advanced override with spaces");
            Assert(UsbStreamingRuntime.Resolve(app, selected) == selected);
        }));
        await Test("streaming refuses missing runtime or missing managed dependencies before launching", async () =>
        {
            await Throws<FileNotFoundException>(() => Sync(() => UsbStreamingRuntime.Resolve(Path.Combine(root, "usb-streaming", "missing"))));
            var executable = FixtureRuntime("incomplete"); File.Delete(Path.ChangeExtension(executable, ".runtimeconfig.json"));
            await Throws<InvalidDataException>(() => Sync(() => UsbStreamingRuntime.Validate(executable)));
        });
        await Test("streaming refuses missing native decoding dependencies and missing self-contained framework", async () =>
        {
            var executable = FixtureRuntime("missing-native");
            File.Delete(Path.Combine(Path.GetDirectoryName(executable)!, "runtimes", "win-x64", "native", "libusb-1.0.dll"));
            await Throws<InvalidDataException>(() => Sync(() => UsbStreamingRuntime.Validate(executable)));
            executable = FixtureRuntime("missing-framework"); File.Delete(Path.Combine(Path.GetDirectoryName(executable)!, "coreclr.dll"));
            await Throws<InvalidDataException>(() => Sync(() => UsbStreamingRuntime.Validate(executable)));
        });
        await Test("streaming command arguments preserve literal titles without a shell", async () =>
        {
            var executable = FixtureRuntime("literal arguments"); const string title = "SwitchDesk \"USB screen\" & $(literal)";
            var info = UsbStreamingRuntime.CreateStartInfo(executable, title, false, 25);
            Assert(!info.UseShellExecute && info.CreateNoWindow && info.RedirectStandardOutput && info.RedirectStandardError);
            Assert(info.ArgumentList.SequenceEqual(new[] { "usb", "--legacy", "--title", title, "--no-audio", "--volume", "25" }));
            Assert(info.WorkingDirectory == Path.GetDirectoryName(executable) && info.Arguments.Length == 0);
            await Throws<ArgumentOutOfRangeException>(() => Sync(() => UsbStreamingRuntime.CreateStartInfo(executable, title, true, 101)));
            await Throws<ArgumentException>(() => Sync(() => UsbStreamingRuntime.CreateStartInfo(executable, "invalid\ntitle")));
        });
        await Test("streaming window discovery requires the owned PID, unique title and a visible window", () => Sync(() =>
        {
            const string title = "SwitchDesk fixture - SysDVR-Client";
            Assert(UsbStreamingWindow.Matches(43, 43, title, title, true));
            Assert(UsbStreamingWindow.Matches(43, 43, title + " [720p]", title, true));
            Assert(!UsbStreamingWindow.Matches(44, 43, title, title, true));
            Assert(!UsbStreamingWindow.Matches(43, 43, title + "Other", title, true));
            Assert(!UsbStreamingWindow.Matches(43, 43, "unrelated player", title, true));
            Assert(!UsbStreamingWindow.Matches(43, 43, title, title, false));
            Assert(!UsbStreamingWindow.Matches(0, 0, title, title, true));
        }));
        await Test("streaming USB discovery matches pinned SysDVR IDs including composite interfaces only", () => Sync(() =>
        {
            Assert(UsbStreamingRuntime.UsbVendorId == 0x18D1 && UsbStreamingRuntime.UsbProductId == 0x4EE0);
            Assert(UsbStreamingRuntime.MatchUsbDevicePath(@"\\?\usb#vid_18d1&pid_4ee0#fixture#{guid}"));
            Assert(UsbStreamingRuntime.MatchUsbDevicePath(@"\\?\USB#VID_18D1&PID_4EE0&MI_00#fixture#{guid}"));
            Assert(UsbStreamingRuntime.MatchUsbDevicePath(@"USB\VID_18D1&PID_4EE0&MI_01"));
            Assert(!UsbStreamingRuntime.MatchUsbDevicePath(@"\\?\usb#vid_057e&pid_3000#SwitchDesk#{guid}"));
            Assert(!UsbStreamingRuntime.MatchUsbDevicePath(@"USB\VID_18D11&PID_4EE0"));
            Assert(!UsbStreamingRuntime.MatchUsbDevicePath(@"USB\VID_18D1&PID_4EE00"));
            Assert(!UsbStreamingRuntime.MatchUsbDevicePath("notvid_18d1&pid_4ee0"));
            Assert(!UsbStreamingRuntime.MatchUsbDevicePath(null));
        }));
        if (!OperatingSystem.IsWindows()) return;
        ProcessStartInfo Shell(string command)
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("/d"); info.ArgumentList.Add("/c"); info.ArgumentList.Add(command); return info;
        }
        await Test("streaming captures immediate exit and both output streams without an event subscription race", async () =>
        {
            await using var session = UsbStreamingSession.StartOwned(Shell("echo stream-out & echo stream-error 1>&2 & exit /b 23"), "fixture-immediate-exit");
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(8)); await session.StopAsync();
            Assert(session.HasExited && session.ExitCode == 23 && session.TryFindVideoWindow() == 0);
            Assert(session.OutputTail.Contains("stream-out", StringComparison.Ordinal) && session.OutputTail.Contains("stream-error", StringComparison.Ordinal));
        });
        await Test("streaming waits without a video window and concurrent stop terminates only its own process", async () =>
        {
            // Loopback ping supplies a bounded, sleeping fixture; it does not contact the Switch or the Internet.
            const string wait = "echo WAITING_FIXTURE & ping -n 60 127.0.0.1 >nul";
            await using var owned = UsbStreamingSession.StartOwned(Shell(wait), "fixture-owned");
            await using var unrelated = UsbStreamingSession.StartOwned(Shell(wait), "fixture-unrelated");
            var until = Stopwatch.StartNew();
            while (!owned.OutputTail.Contains("WAITING_FIXTURE", StringComparison.Ordinal) && until.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            Assert(!owned.HasExited && owned.TryFindVideoWindow() == 0, "no window must not be treated as a player failure");
            var first = owned.StopAsync(); var second = owned.StopAsync(); Assert(ReferenceEquals(first, second));
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(8));
            Assert(owned.HasExited && !unrelated.HasExited, "stopping a stream affected a separate process");
        });
        await Test("streaming diagnostic tail remains bounded and retains the latest output", async () =>
        {
            await using var session = UsbStreamingSession.StartOwned(Shell("for /L %i in (1,1,300) do @echo fixture-line-%i"), "fixture-log-tail");
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(8)); await session.StopAsync();
            Assert(session.OutputTail.Length <= 32768 && session.OutputTail.Split(Environment.NewLine).Length <= 128);
            Assert(session.OutputTail.Contains("fixture-line-300", StringComparison.Ordinal));
        });
        await Test("streaming preserves a live process after a failed stop and retries disposal", async () =>
        {
            var attempts = 0;
            var session = UsbStreamingSession.StartOwned(Shell("echo RETRY_STOP_FIXTURE & ping -n 60 127.0.0.1 >nul"), "fixture-stop-retry", process =>
            {
                if (Interlocked.Increment(ref attempts) == 1) throw new System.ComponentModel.Win32Exception(5, "Injected termination failure");
                process.Kill(entireProcessTree: true);
            });
            try
            {
                var id = session.ProcessId;
                await Throws<IOException>(async () => await session.DisposeAsync());
                Assert(!session.HasExited && session.ProcessId == id && session.ExitCode == null, "failed disposal lost the live owned process");
                using (var stillRunning = Process.GetProcessById(id)) Assert(!stillRunning.HasExited);
                await session.DisposeAsync();
                Assert(session.HasExited && attempts == 2, "failed stop was not retried or process did not exit");
            }
            finally { await session.DisposeAsync(); }
        });
    }
}
