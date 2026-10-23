using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SwitchDesk.Core;

public sealed record UsbCandidate(string Path, string Description);
public static class UsbDiscovery
{
    public static readonly Guid InterfaceGuid = new("51A376D0-670B-4C19-A2ED-5210DE5C0001");
    private static readonly Guid UsbGuid = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    public static string? ProblemMessage(uint code) => code switch
    {
        28 => "检测到 Switch USB · 缺少 WinUSB 驱动（Windows 错误 28）",
        10 => "Switch USB 设备无法启动（Windows 错误 10）",
        22 => "Switch USB 设备已被禁用（Windows 错误 22）",
        0 => null,
        _ => $"Switch USB 设备异常（Windows 错误 {code}）"
    };
    public static string? DiagnoseMissingInterface()
    {
        var set = Native.SetupDiGetUsbDevices(0, "USB", 0, 6);
        if (set == new nint(-1)) return null;
        try
        {
            for (uint i = 0; ; i++)
            {
                var device = new Native.DeviceData { Size = (uint)Marshal.SizeOf<Native.DeviceData>() };
                if (!Native.SetupDiEnumDeviceInfo(set, i, ref device)) break;
                var buffer = new byte[4096];
                if (!Native.SetupDiGetDeviceRegistryProperty(set, ref device, 1, out _, buffer, (uint)buffer.Length, out _)) continue;
                if (!Encoding.Unicode.GetString(buffer).Contains(@"USB\VID_057E&PID_3000", StringComparison.OrdinalIgnoreCase)) continue;
                if (Native.CM_Get_DevNode_Status(out _, out var problem, device.Instance, 0) == 0 && problem != 0) return ProblemMessage(problem);
            }
            return null;
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
    }
    public static IReadOnlyList<UsbCandidate> Enumerate()
    {
        var results = new List<UsbCandidate>();
        foreach (var guidValue in new[] { InterfaceGuid, UsbGuid })
        {
            var guid = guidValue; var set = Native.SetupDiGetClassDevs(ref guid, null, 0, 0x12);
            if (set == new nint(-1)) continue;
            try
            {
                for (uint index = 0; ; index++)
                {
                    var data = new Native.InterfaceData { Size = (uint)Marshal.SizeOf<Native.InterfaceData>() };
                    if (!Native.SetupDiEnumDeviceInterfaces(set, 0, ref guid, index, ref data)) break;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref data, 0, 0, out var required, 0);
                    if (required < 8 || required > 65536) continue;
                    var detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out _, 0)) continue;
                        var path = Marshal.PtrToStringUni(detail + 4)!;
                        if (guidValue == InterfaceGuid || path.Contains("vid_057e&pid_3000", StringComparison.OrdinalIgnoreCase))
                            results.Add(new(path, "Switch USB 管理接口"));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
        }
        return results.DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { internal uint Size; internal Guid Guid; internal uint Flags; internal nint Reserved; }
        [StructLayout(LayoutKind.Sequential)] internal struct DeviceData { internal uint Size; internal Guid Class; internal uint Instance; internal nint Reserved; }
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint SetupDiGetUsbDevices(nint guid, string enumerator, nint parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref DeviceData device);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceRegistryProperty(nint set, ref DeviceData device, uint property, out uint type, byte[] buffer, uint size, out uint required);
        [DllImport("cfgmgr32.dll")] internal static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint device, uint flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint SetupDiGetClassDevs(ref Guid guid, string? enumerator, nint parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInterfaces(nint set, nint info, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref InterfaceData data, nint detail, uint size, out uint required, nint deviceInfo);
        [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(nint set);
    }
}
public sealed class WinUsbStream : Stream
{
    private readonly SafeFileHandle file;
    private readonly SafeUsbHandle usb;
    private readonly byte readPipe, writePipe;
    private int disposed;
    public WinUsbStream(string devicePath)
    {
        file = Native.CreateFile(devicePath, 0xC0000000, 0, 0, 3, 0x40000000, 0);
        if (file.IsInvalid) { file.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开 USB 接口"); }
        if (!Native.WinUsb_Initialize(file, out usb)) { file.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "需要将配套程序的 USB 接口绑定到 WinUSB 驱动"); }
        try
        {
            var descriptorBytes = new byte[18];
            if (!Native.WinUsb_GetDescriptor(usb, 1, 0, 0, descriptorBytes, 18, out var descriptorSize) || descriptorSize != 18)
                throw new IOException("无法核对 USB 产品描述符");
            var languages = new byte[255]; ushort language = 0x0409;
            if (Native.WinUsb_GetDescriptor(usb, 3, 0, 0, languages, 255, out var languageSize) && languageSize >= 4)
                language = (ushort)(languages[2] | languages[3] << 8);
            var product = new byte[255];
            if (!Native.WinUsb_GetDescriptor(usb, 3, descriptorBytes[15], language, product, 255, out var productSize) || productSize < 2 ||
                Encoding.Unicode.GetString(product, 2, (int)productSize - 2) != "SwitchDesk PC")
                throw new IOException("等待 SwitchDesk 配套程序；当前 USB 接口属于其他主机程序");
            if (!Native.WinUsb_QueryInterfaceSettings(usb, 0, out var descriptor)) throw new Win32Exception(Marshal.GetLastWin32Error());
            for (byte i = 0; i < descriptor.Endpoints; i++)
            {
                if (!Native.WinUsb_QueryPipe(usb, 0, i, out var pipe) || pipe.Type != 2) continue;
                if ((pipe.Id & 0x80) != 0) readPipe = pipe.Id; else writePipe = pipe.Id;
            }
            if (readPipe == 0 || writePipe == 0) throw new IOException("USB 接口没有双向 Bulk 端点");
            uint timeout = 600000; // long save commits continue without a UI cancellation
            if (!Native.WinUsb_SetPipePolicy(usb, readPipe, 3, 4, ref timeout) || !Native.WinUsb_SetPipePolicy(usb, writePipe, 3, 4, ref timeout))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法配置 USB 传输超时");
        }
        catch { usb.Dispose(); file.Dispose(); throw; }
    }
    public override bool CanRead => Volatile.Read(ref disposed) == 0;
    public override bool CanWrite => Volatile.Read(ref disposed) == 0;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this); if (count == 0) return 0;
        var temporary = new byte[count];
        if (!Native.WinUsb_ReadPipe(usb, readPipe, temporary, (uint)count, out var read, 0)) throw new IOException("USB 读取中断", new Win32Exception(Marshal.GetLastWin32Error()));
        Buffer.BlockCopy(temporary, 0, buffer, offset, (int)read); return (int)read;
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var data = buffer.AsSpan(offset, count).ToArray();
        if (!Native.WinUsb_WritePipe(usb, writePipe, data, (uint)count, out var written, 0) || written != count)
            throw new IOException("USB 写入中断", new Win32Exception(Marshal.GetLastWin32Error()));
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        using var registration = ct.Register(() => Abort(readPipe));
        try { return await Task.Run(() => { ct.ThrowIfCancellationRequested(); var data = new byte[buffer.Length]; var count = Read(data, 0, data.Length); data.AsMemory(0, count).CopyTo(buffer); ct.ThrowIfCancellationRequested(); return count; }, ct); }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is IOException or ObjectDisposedException) { throw new OperationCanceledException("USB 读取已取消", ex, ct); }
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        using var registration = ct.Register(() => Abort(writePipe));
        try { await Task.Run(() => { ct.ThrowIfCancellationRequested(); var data = buffer.ToArray(); Write(data, 0, data.Length); ct.ThrowIfCancellationRequested(); }, ct); }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is IOException or ObjectDisposedException) { throw new OperationCanceledException("USB 写入已取消", ex, ct); }
    }
    private void Abort(byte pipe) { try { if (!usb.IsClosed) Native.WinUsb_AbortPipe(usb, pipe); } catch (ObjectDisposedException) { } }
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
        {
            Abort(readPipe); Abort(writePipe);
            usb.Dispose(); file.Dispose();
        }
        base.Dispose(disposing);
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    private sealed class SafeUsbHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeUsbHandle() : base(true) { }
        protected override bool ReleaseHandle() => Native.WinUsb_Free(handle);
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct InterfaceDescriptor { internal byte Length, Type, Number, Alternate, Endpoints, Class, SubClass, Protocol, String; }
        [StructLayout(LayoutKind.Sequential)] internal struct PipeInformation { internal int Type; internal byte Id; internal ushort MaxPacket; internal byte Interval; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_Initialize(SafeFileHandle device, out SafeUsbHandle usb);
        [DllImport("winusb.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_Free(nint usb);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_QueryInterfaceSettings(SafeUsbHandle usb, byte alternate, out InterfaceDescriptor descriptor);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_QueryPipe(SafeUsbHandle usb, byte alternate, byte index, out PipeInformation pipe);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_GetDescriptor(SafeUsbHandle usb, byte type, byte index, ushort language, [Out] byte[] buffer, uint length, out uint transferred);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_ReadPipe(SafeUsbHandle usb, byte pipe, [Out] byte[] buffer, uint length, out uint transferred, nint overlapped);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_WritePipe(SafeUsbHandle usb, byte pipe, byte[] buffer, uint length, out uint transferred, nint overlapped);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_AbortPipe(SafeUsbHandle usb, byte pipe);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_SetPipePolicy(SafeUsbHandle usb, byte pipe, uint policy, uint size, ref uint value);
    }
}
