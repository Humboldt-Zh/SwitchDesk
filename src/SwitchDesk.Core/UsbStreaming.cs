using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

[assembly: InternalsVisibleTo("SwitchDesk.Tests")]

namespace SwitchDesk.Core;

/// <summary>Unmodified SysDVR is a separate, bundled runtime; it is not loaded into this process.</summary>
public static class UsbStreamingRuntime
{
    public const string ClientFileName = "SysDVR-Client.exe";
    public const string BundledRelativeDirectory = "Streaming/SysDVR";
    // SysDVR v6.3 / 531e9c25: sysmodule/source/USB/Serial.c and Client/Sources/UsbContext.cs.
    public const ushort UsbVendorId = 0x18D1;
    public const ushort UsbProductId = 0x4EE0;
    private static readonly string[] RequiredFiles =
    [
        "SysDVR-Client.dll", "SysDVR-Client.deps.json", "SysDVR-Client.runtimeconfig.json",
        "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "clrjit.dll", "System.Private.CoreLib.dll",
        "runtimes/win-x64/native/avcodec-59.dll", "runtimes/win-x64/native/avdevice-59.dll",
        "runtimes/win-x64/native/avfilter-8.dll", "runtimes/win-x64/native/avformat-59.dll",
        "runtimes/win-x64/native/avutil-57.dll", "runtimes/win-x64/native/swresample-4.dll",
        "runtimes/win-x64/native/swscale-6.dll", "runtimes/win-x64/native/SDL2.dll",
        "runtimes/win-x64/native/SDL2_image.dll", "runtimes/win-x64/native/libusb-1.0.dll",
        "runtimes/win-x64/native/cimgui.dll"
    ];

    public static string Resolve(string baseDirectory, string? configuredOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var executable = string.IsNullOrWhiteSpace(configuredOverride)
            ? Path.Combine(Path.GetFullPath(baseDirectory), "Streaming", "SysDVR", ClientFileName)
            : Path.GetFullPath(configuredOverride);
        Validate(executable);
        return executable;
    }

    /// <summary>Enumerates Windows USB paths only; it does not claim an interface, read a serial, or change a driver.</summary>
    public static bool IsUsbDevicePresent()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var guid = new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED");
        var devices = UsbPresence.SetupDiGetClassDevs(ref guid, null, 0, 0x12); // PRESENT | DEVICEINTERFACE
        if (devices == new nint(-1)) return false;
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new UsbPresence.InterfaceData { Size = (uint)Marshal.SizeOf<UsbPresence.InterfaceData>() };
                if (!UsbPresence.SetupDiEnumDeviceInterfaces(devices, 0, ref guid, index, ref data)) break;
                UsbPresence.SetupDiGetDeviceInterfaceDetail(devices, ref data, 0, 0, out var required, 0);
                if (required is < 8 or > 65536) continue;
                var detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!UsbPresence.SetupDiGetDeviceInterfaceDetail(devices, ref data, detail, required, out _, 0)) continue;
                    if (MatchUsbDevicePath(Marshal.PtrToStringUni(detail + 4))) return true;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
            return false;
        }
        finally { UsbPresence.SetupDiDestroyDeviceInfoList(devices); }
    }

    public static bool MatchUsbDevicePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 65536) return false;
        return Identifier("vid_18d1") && Identifier("pid_4ee0");
        bool Identifier(string token)
        {
            var offset = 0;
            while (offset < path.Length)
            {
                var at = path.IndexOf(token, offset, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return false;
                var end = at + token.Length;
                if ((at == 0 || path[at - 1] is '\\' or '#' or '&' or '/') &&
                    (end == path.Length || path[end] is '\\' or '#' or '&' or '/')) return true;
                offset = end;
            }
            return false;
        }
    }

    public static void Validate(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) ||
            !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new FileNotFoundException("未找到随包提供的 SysDVR 客户端。请使用完整 Windows 运行包，或在高级设置中选择官方客户端。", executable);
        var directory = Path.GetDirectoryName(executable)!;
        var missing = RequiredFiles
            .Where(name => !File.Exists(Path.Combine(directory, name))).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException("SysDVR 6.3 客户端或随附运行时不完整，请保留客户端整个目录。缺少：" + string.Join("、", missing));
    }

    public static ProcessStartInfo CreateStartInfo(string executable, string title, bool audioEnabled = true, int volume = 100)
    {
        Validate(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.Length > 160 || title.Any(char.IsControl)) throw new ArgumentException("播放器标题包含无效字符。", nameof(title));
        if (volume is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(volume));
        var info = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "usb", "--legacy", "--title", title }) info.ArgumentList.Add(argument);
        if (!audioEnabled) info.ArgumentList.Add("--no-audio");
        info.ArgumentList.Add("--volume"); info.ArgumentList.Add(volume.ToString(CultureInfo.InvariantCulture));
        return info;
    }

    private static class UsbPresence
    {
        [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { internal uint Size; internal Guid InterfaceGuid; internal uint Flags; internal nint Reserved; }
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint SetupDiGetClassDevs(ref Guid guid, string? enumerator, nint parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInterfaces(nint devices, nint deviceInfo, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(nint devices, ref InterfaceData data, nint detail, uint size, out uint required, nint deviceInfo);
        [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(nint devices);
    }
}

public static class UsbStreamingWindow
{
    public static bool Matches(int ownerProcessId, int expectedProcessId, string? actualTitle, string expectedTitle, bool visible)
        => visible && expectedProcessId > 0 && ownerProcessId == expectedProcessId && !string.IsNullOrWhiteSpace(expectedTitle) &&
           actualTitle != null && (actualTitle.Equals(expectedTitle, StringComparison.Ordinal) || actualTitle.StartsWith(expectedTitle + " ", StringComparison.Ordinal));
}

/// <summary>Owns only its own child process. A missing video window means it may still be waiting for USB.</summary>
public sealed class UsbStreamingSession : IDisposable, IAsyncDisposable
{
    private readonly Process process;
    private readonly Action<Process> terminate;
    private readonly string expectedWindowTitle;
    private readonly Queue<string> output = new();
    private readonly object outputLock = new(), stopLock = new();
    private readonly TaskCompletionSource exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stdoutClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stderrClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? stopTask;
    private int exited, disposed, exitCode, exitCodeKnown, outputCharacters;
    private nint lastWindow;
    public int ProcessId { get; private set; }
    public bool HasExited => CaptureExit();
    public int? ExitCode { get { CaptureExit(); return Volatile.Read(ref exitCodeKnown) != 0 ? exitCode : null; } }
    public Task Completion => exit.Task;
    public string OutputTail { get { lock (outputLock) return string.Join(Environment.NewLine, output); } }
    public event EventHandler? Exited;

    private UsbStreamingSession(Process process, string title, Action<Process>? terminate)
    {
        this.process = process;
        this.terminate = terminate ?? (static owned => owned.Kill(entireProcessTree: true));
        expectedWindowTitle = title + " - SysDVR-Client";
        process.Exited += OnExited;
        process.OutputDataReceived += OnOutput;
        process.ErrorDataReceived += OnError;
    }

    public static UsbStreamingSession Start(string executable, string title, bool audioEnabled = true, int volume = 100)
        => StartOwned(UsbStreamingRuntime.CreateStartInfo(executable, title, audioEnabled, volume), title);

    internal static UsbStreamingSession StartOwned(ProcessStartInfo info, string title, Action<Process>? terminate = null)
    {
        if (info.UseShellExecute || !info.RedirectStandardOutput || !info.RedirectStandardError)
            throw new ArgumentException("播放器必须使用独立进程和受控输出启动。", nameof(info));
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var session = new UsbStreamingSession(process, title, terminate);
        try
        {
            if (!process.Start()) throw new IOException("无法启动 SysDVR 客户端。");
            session.ProcessId = process.Id;
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            session.CaptureExit();
            return session;
        }
        catch
        {
            try { if (session.ProcessId != 0 && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
            session.Cleanup(); throw;
        }
    }

    public nint TryFindVideoWindow()
    {
        if (!OperatingSystem.IsWindows() || Volatile.Read(ref disposed) != 0 || HasExited) return 0;
        if (lastWindow != 0 && Native.IsWindow(lastWindow))
        {
            Native.GetWindowThreadProcessId(lastWindow, out var owner);
            if (owner == (uint)ProcessId) return lastWindow;
        }
        nint found = 0;
        Native.EnumWindows((window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out var owner);
            if (owner != (uint)ProcessId || !Native.IsWindowVisible(window)) return true;
            var length = Native.GetWindowTextLength(window);
            if (length is <= 0 or > 1024) return true;
            var text = new StringBuilder(length + 1); Native.GetWindowText(window, text, text.Capacity);
            if (!UsbStreamingWindow.Matches((int)owner, ProcessId, text.ToString(), expectedWindowTitle, true)) return true;
            found = window; return false;
        }, 0);
        if (found != 0) lastWindow = found;
        return found;
    }

    public Task StopAsync()
    {
        lock (stopLock)
        {
            if (stopTask is { IsFaulted: true } or { IsCanceled: true }) stopTask = null;
            return stopTask ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (!HasExited)
        {
            var window = TryFindVideoWindow();
            if (window != 0) Native.PostMessage(window, 0x0010, 0, 0); // WM_CLOSE; only our validated window.
            else try { process.CloseMainWindow(); } catch (InvalidOperationException) { }
            if (await Task.WhenAny(exit.Task, Task.Delay(1500)).ConfigureAwait(false) != exit.Task && !HasExited)
            {
                try { terminate(process); }
                catch (InvalidOperationException) when (HasExited) { }
                catch (Win32Exception e) { throw new IOException("播放器未能退出，请检查连接诊断中的客户端状态。", e); }
                if (await Task.WhenAny(exit.Task, Task.Delay(2000)).ConfigureAwait(false) != exit.Task && !HasExited)
                    throw new IOException("播放器退出超时，尚未恢复 USB 管理连接。");
            }
        }
        CaptureExit();
        await Task.WhenAny(Task.WhenAll(stdoutClosed.Task, stderrClosed.Task), Task.Delay(500)).ConfigureAwait(false);
    }

    private bool CaptureExit()
    {
        if (Volatile.Read(ref exited) != 0) return true;
        if (Volatile.Read(ref disposed) != 0) return false;
        try
        {
            if (!process.HasExited) return false;
            exitCode = process.ExitCode; Volatile.Write(ref exitCodeKnown, 1);
            Volatile.Write(ref exited, 1); exit.TrySetResult(); return true;
        }
        catch (InvalidOperationException) { return Volatile.Read(ref exited) != 0; }
    }

    private void OnExited(object? sender, EventArgs args)
    {
        CaptureExit();
        if (Volatile.Read(ref disposed) == 0) Exited?.Invoke(this, EventArgs.Empty);
    }
    private void OnOutput(object sender, DataReceivedEventArgs args) { if (args.Data == null) stdoutClosed.TrySetResult(); else Append(args.Data); }
    private void OnError(object sender, DataReceivedEventArgs args) { if (args.Data == null) stderrClosed.TrySetResult(); else Append(args.Data); }
    private void Append(string text)
    {
        text = new string(text.Where(c => !char.IsControl(c) || c == '\t').Take(2048).ToArray());
        if (text.Length == 0) return;
        lock (outputLock)
        {
            output.Enqueue(text); outputCharacters += text.Length;
            while (output.Count > 128 || outputCharacters > 32768) outputCharacters -= output.Dequeue().Length;
        }
    }
    private void Cleanup()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        process.Exited -= OnExited; process.OutputDataReceived -= OnOutput; process.ErrorDataReceived -= OnError; process.Dispose();
    }
    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        if (!HasExited) throw new IOException("播放器仍在运行，保留会话以便重试停止。");
        Cleanup();
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (!HasExited) throw new IOException("播放器仍在运行，保留会话以便重试停止。");
        Cleanup();
    }

    private static class Native
    {
        internal delegate bool EnumWindowsCallback(nint window, nint parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLength(nint window);
        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint window, StringBuilder text, int maximum);
        [DllImport("user32.dll", EntryPoint = "PostMessageW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    }
}
