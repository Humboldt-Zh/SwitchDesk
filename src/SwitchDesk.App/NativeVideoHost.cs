using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SwitchDesk.App;

/// <summary>
/// Hosts a caller-owned native video window. The caller discovers the window and
/// owns its process lifetime; this class never sends WM_CLOSE or terminates it.
/// Attach and Detach must run on this element's dispatcher after it is loaded.
/// </summary>
public sealed class NativeVideoHost : HwndHost
{
    private const int GwlStyle = -16, GwlExStyle = -20, GwlOwner = -8;
    private const uint WsChild = 0x40000000, WsVisible = 0x10000000;
    private const uint WsClipChildren = 0x02000000, WsClipSiblings = 0x04000000;
    private const uint TopLevelStyle = 0x80000000 | 0x00C00000 | 0x00040000 | 0x00080000 | 0x00020000 | 0x00010000;
    private const uint TopLevelExStyle = 0x00040000 | 0x00000100 | 0x00000200 | 0x00000001 | 0x00000008;
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;
    private const int SwHide = 0, SwShowNoActivate = 4;
    private nint host, child;
    private int childProcessId;
    private WindowSnapshot original;

    public nint HostHandle => host;
    public nint ChildHandle => HasChild ? child : 0;
    public int ChildProcessId => HasChild ? childProcessId : 0;
    public bool HasChild => host != 0 && Native.IsWindow(host) && child != 0 && IsWindowOwnedByProcess(child, childProcessId) && Native.GetParent(child) == host;
    public string? LastError { get; private set; }

    /// <summary>A point-in-time ownership check; callers must still handle window exit races.</summary>
    public static bool IsWindowOwnedByProcess(nint window, int processId)
        => processId > 0 && window != 0 && Native.IsWindow(window) &&
           Native.GetWindowThreadProcessId(window, out var actual) != 0 && actual == (uint)processId;

    public void Attach(nint window, int ownerPid)
    {
        Dispatcher.VerifyAccess();
        if (host == 0 || !Native.IsWindow(host)) throw new InvalidOperationException("视频宿主尚未就绪，请在 Loaded 后嵌入播放器。");
        if (!IsWindowOwnedByProcess(window, ownerPid)) throw new ArgumentException("播放器窗口不存在，或不属于指定进程。", nameof(window));
        if (window == host || Native.IsChild(window, host)) throw new ArgumentException("不能将宿主或它的父窗口嵌入自身。", nameof(window));
        if (HasChild)
        {
            if (window != child || ownerPid != childProcessId) throw new InvalidOperationException("视频宿主已经附加播放器，请先分离现有窗口。");
            ResizeChild(); return;
        }

        ClearChild();
        var style = ReadWindowLong(window, GwlStyle);
        var exStyle = ReadWindowLong(window, GwlExStyle);
        if (!Native.GetWindowRect(window, out var bounds)) throw Win32Failure("读取播放器窗口位置");
        var wasChild = (ToUInt32(style) & WsChild) != 0;
        var snapshot = new WindowSnapshot(style, exStyle, wasChild ? Native.GetParent(window) : 0,
            wasChild ? 0 : Native.GetWindow(window, 4), bounds, Native.IsWindowVisible(window));
        var changed = false;
        var reparented = false;
        try
        {
            Native.ShowWindow(window, SwHide);
            WriteWindowLong(window, GwlStyle, FromUInt32((ToUInt32(style) & ~TopLevelStyle) | WsChild | WsClipSiblings));
            changed = true;
            WriteWindowLong(window, GwlExStyle, FromUInt32(ToUInt32(exStyle) & ~TopLevelExStyle));
            SetParentChecked(window, host);
            reparented = true;
            if (!IsWindowOwnedByProcess(window, ownerPid) || Native.GetParent(window) != host)
                throw new InvalidOperationException("播放器窗口在嵌入期间退出或改变了所属窗口。");

            child = window; childProcessId = ownerPid; original = snapshot;
            ResizeChildCore(SwpFrameChanged);
            LastError = null;
        }
        catch (Exception error)
        {
            // Never undo another process's subsequent reparenting decision.
            if (IsWindowOwnedByProcess(window, ownerPid) &&
                (!reparented || Native.GetParent(window) == host))
            {
                try
                {
                    if (reparented) RestoreParent(window, snapshot);
                    if (changed) RestoreAppearance(window, snapshot, snapshot.Visible);
                    else if (snapshot.Visible) Native.ShowWindow(window, SwShowNoActivate);
                }
                catch (Exception rollback) when (rollback is Win32Exception or InvalidOperationException)
                {
                    LastError = error.Message + "；恢复窗口时失败：" + rollback.Message;
                    // Keep a still-attached window tracked so disposal cannot accidentally destroy it.
                    if (IsWindowOwnedByProcess(window, ownerPid) && Native.GetParent(window) == host)
                    { child = window; childProcessId = ownerPid; original = snapshot; }
                    throw new InvalidOperationException(LastError, new AggregateException(error, rollback));
                }
            }
            if (child == window) ClearChild();
            LastError = error.Message;
            throw;
        }
    }

    /// <summary>
    /// Restores the window's original parent/styles/size. It stays hidden by
    /// default so the caller can shut down its player process without a popup.
    /// No foreign window or process is closed, even when its original parent disappeared.
    /// </summary>
    public void Detach(bool restoreVisibility = false)
    {
        Dispatcher.VerifyAccess();
        if (!HasChild) { ClearChild(); return; }
        var window = child; var snapshot = original;
        try
        {
            Native.ShowWindow(window, SwHide);
            RestoreParent(window, snapshot);
            // A successfully detached window must not remain marked as our child
            // even if restoring its old style subsequently fails.
            ClearChild();
            RestoreAppearance(window, snapshot, restoreVisibility && snapshot.Visible);
            LastError = null;
        }
        catch (Win32Exception error)
        {
            LastError = error.Message;
            throw;
        }
    }

    public void ResizeChild()
    {
        Dispatcher.VerifyAccess();
        if (!HasChild) { ClearChild(); return; }
        ResizeChildCore(0);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        host = Native.CreateWindowEx(0, "static", "", WsChild | WsVisible | WsClipChildren | WsClipSiblings | 4,
            0, 0, 1, 1, hwndParent.Handle, 0, 0, 0); // SS_BLACKRECT supplies a safe empty video surface.
        if (host == 0) throw Win32Failure("创建视频宿主窗口");
        return new(this, host);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        try { Detach(); }
        finally
        {
            // DestroyWindow also destroys descendants. If Windows refused to
            // detach a live player, fail closed and let its caller stop it first.
            // This rare failure must never become an implicit WM_CLOSE for it.
            if (!HasChild)
            {
                if (Native.IsWindow(hwnd.Handle) && !Native.DestroyWindow(hwnd.Handle))
                    LastError = Win32Failure("销毁视频宿主窗口").Message;
                host = 0;
            }
        }
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        TryResizeChild();
    }

    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // Client rectangles are physical pixels in the host's current DPI context;
        // no guessed WPF DIP scale is applied to a cross-process video window.
        if (msg is 0x0005 or 0x02E0 or 0x02E3) TryResizeChild(); // SIZE / DPI_CHANGED / DPI_AFTERPARENT.
        else if (msg == 0x0007 && HasChild) Native.SetFocus(child);
        else if (msg == 0x0018 && HasChild) Native.ShowWindow(child, wParam == 0 ? SwHide : SwShowNoActivate);
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private void TryResizeChild()
    {
        if (!HasChild) return;
        try { ResizeChildCore(0); }
        catch (Win32Exception error) { LastError = error.Message; }
    }

    private void ResizeChildCore(uint extraFlags)
    {
        if (!Native.GetClientRect(host, out var rect)) throw Win32Failure("读取视频宿主尺寸");
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) { Native.ShowWindow(child, SwHide); return; }
        if (!Native.SetWindowPos(child, 0, 0, 0, width, height, SwpNoZOrder | SwpNoActivate | extraFlags))
            throw Win32Failure("调整播放器窗口尺寸");
        Native.ShowWindow(child, SwShowNoActivate);
    }

    private static void RestoreParent(nint window, WindowSnapshot snapshot)
    {
        var parent = snapshot.Parent != 0 && Native.IsWindow(snapshot.Parent) ? snapshot.Parent : 0;
        SetParentChecked(window, parent);
    }

    private static void RestoreAppearance(nint window, WindowSnapshot snapshot, bool visible)
    {
        WriteWindowLong(window, GwlStyle, snapshot.Style);
        WriteWindowLong(window, GwlExStyle, snapshot.ExStyle);
        if ((ToUInt32(snapshot.Style) & WsChild) == 0)
        {
            var owner = snapshot.Owner != 0 && Native.IsWindow(snapshot.Owner) ? snapshot.Owner : 0;
            if (Native.GetWindow(window, 4) != owner) WriteWindowLong(window, GwlOwner, owner);
        }
        var rect = snapshot.Bounds;
        var parent = snapshot.Parent != 0 && Native.IsWindow(snapshot.Parent) ? snapshot.Parent : 0;
        if (parent != 0 && Native.MapWindowPoints(0, parent, ref rect, 2) == 0 && Marshal.GetLastWin32Error() != 0)
            throw Win32Failure("转换播放器窗口位置");
        if (!Native.SetWindowPos(window, 0, rect.Left, rect.Top, Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top),
            SwpNoZOrder | SwpNoActivate | SwpFrameChanged)) throw Win32Failure("恢复播放器窗口位置");
        Native.ShowWindow(window, visible ? SwShowNoActivate : SwHide);
    }

    private void ClearChild() { child = 0; childProcessId = 0; original = default; }
    private static uint ToUInt32(nint value) => unchecked((uint)value.ToInt64());
    private static nint FromUInt32(uint value) => unchecked((nint)(int)value);
    private static Win32Exception Win32Failure(string operation)
    {
        var code = Marshal.GetLastWin32Error();
        return new(code, $"{operation}失败（Win32 {code}）。Windows 可能拒绝不同权限或 DPI 上下文的窗口嵌入。");
    }

    private static nint ReadWindowLong(nint window, int index)
    {
        var value = IntPtr.Size == 8 ? Native.GetWindowLongPtr64(window, index) : (nint)Native.GetWindowLong32(window, index);
        if (value == 0 && Marshal.GetLastWin32Error() != 0) throw Win32Failure("读取播放器窗口样式");
        return value;
    }

    private static void WriteWindowLong(nint window, int index, nint value)
    {
        var previous = IntPtr.Size == 8 ? Native.SetWindowLongPtr64(window, index, value) : (nint)Native.SetWindowLong32(window, index, unchecked((int)value));
        if (previous == 0 && Marshal.GetLastWin32Error() != 0) throw Win32Failure("设置播放器窗口样式");
    }

    private static void SetParentChecked(nint window, nint parent)
    {
        var previous = Native.SetParent(window, parent);
        if (previous == 0 && Marshal.GetLastWin32Error() != 0) throw Win32Failure("嵌入或分离播放器窗口");
    }

    private readonly record struct WindowSnapshot(nint Style, nint ExStyle, nint Parent, nint Owner, NativeRect Bounds, bool Visible);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }
    private static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsChild(nint parent, nint window);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll")] internal static extern nint GetParent(nint window);
        [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
        [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetParent(nint window, nint parent);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] internal static extern nint GetWindowLongPtr64(nint window, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] internal static extern int GetWindowLong32(nint window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static extern nint SetWindowLongPtr64(nint window, int index, nint value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] internal static extern int SetWindowLong32(nint window, int index, int value);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint window, out NativeRect rect);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(nint window, out NativeRect rect);
        [DllImport("user32.dll", SetLastError = true)] internal static extern int MapWindowPoints(nint from, nint to, ref NativeRect points, uint count);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll")] internal static extern nint SetFocus(nint window);
    }
}
