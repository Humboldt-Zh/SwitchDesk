using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace SwitchDesk.App;

// A delayed IStream backed by a verified snapshot. Explorer may extract it on a worker
// thread after DoDragDrop has returned; the COM object owns the factory and stream.
public static class VirtualFileDrag
{
    [DllImport("ole32.dll")] private static extern int DoDragDrop(IDataObject data, IDropSource source, uint effects, out uint result);
    public static void Start(Func<Task<string>> snapshot, string name)
    { var data = new VirtualFileDataObject(snapshot, name); var hr = DoDragDrop(data, new DropSource(), 1, out _); if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    [ComVisible(true), Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escape, uint keys);
        [PreserveSig] int GiveFeedback(uint effect);
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DropSource : IDropSource
    {
        public int QueryContinueDrag(bool escape, uint keys) => escape ? 0x40101 : (keys & 1) == 0 ? 0x40100 : 0;
        public int GiveFeedback(uint effect) => 0x40102;
    }
}
[ComVisible(true), Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDataObjectAsyncCapability
{
    void SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool enabled);
    void GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    void StartOperation(IBindCtx? context);
    void InOperation([MarshalAs(UnmanagedType.Bool)] out bool active);
    void EndOperation(int result, IBindCtx? context, uint effects);
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class VirtualFileDataObject : IDataObject, IDataObjectAsyncCapability
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    private static readonly short Descriptor = unchecked((short)RegisterClipboardFormat("FileGroupDescriptorW"));
    private static readonly short Contents = unchecked((short)RegisterClipboardFormat("FileContents"));
    private readonly Lazy<Task<string>> snapshot;
    private readonly string fileName;
    private bool asyncMode = true, active;
    public VirtualFileDataObject(Func<Task<string>> factory, string name)
    {
        snapshot = new(factory, true); var extension = Path.GetExtension(name);
        fileName = name.Length > 250 ? name[..Math.Max(1, 250 - extension.Length)] + extension : name;
    }
    private FORMATETC[] Formats => [new() { cfFormat = Descriptor, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL },
        new() { cfFormat = Contents, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = 0, tymed = TYMED.TYMED_ISTREAM }];
    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        var hr = QueryGetData(ref format); if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        if (format.cfFormat == Descriptor)
        {
            var bytes = new byte[596]; BitConverter.GetBytes(1).CopyTo(bytes, 0);
            BitConverter.GetBytes(4u | 0x4000u | 0x80000000u).CopyTo(bytes, 4); // attributes, progress UI, Unicode
            BitConverter.GetBytes(0x80u).CopyTo(bytes, 40); Encoding.Unicode.GetBytes(fileName + '\0').CopyTo(bytes, 76);
            // HGLOBAL must be movable. Explorer owns it through ReleaseStgMedium.
            var global = Native.GlobalAlloc(0x42, (nuint)bytes.Length); if (global == 0) throw new OutOfMemoryException();
            var address = Native.GlobalLock(global);
            if (address == 0) { Native.GlobalFree(global); throw new OutOfMemoryException("无法锁定拖放描述内存"); }
            try { Marshal.Copy(bytes, 0, address, bytes.Length); } finally { Native.GlobalUnlock(global); }
            medium = new() { tymed = TYMED.TYMED_HGLOBAL, unionmember = global }; return;
        }
        medium = new() { tymed = TYMED.TYMED_ISTREAM, unionmember = Marshal.GetComInterfaceForObject(new SnapshotStream(snapshot), typeof(IStream)) };
    }
    public int QueryGetData(ref FORMATETC format)
    {
        if (format.dwAspect != DVASPECT.DVASPECT_CONTENT) return unchecked((int)0x8004006B);
        if (format.cfFormat == Descriptor && (format.tymed & TYMED.TYMED_HGLOBAL) != 0) return 0;
        if (format.cfFormat == Contents && format.lindex == 0 && (format.tymed & TYMED.TYMED_ISTREAM) != 0) return 0;
        return unchecked((int)0x80040064);
    }
    public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => direction == DATADIR.DATADIR_GET ? new FormatEnumerator(Formats) : throw new COMException("只读数据", unchecked((int)0x80004001));
    public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; output.ptd = 0; return 0x40130; }
    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("不支持此数据格式", unchecked((int)0x80040064));
    public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => throw new COMException("只读", unchecked((int)0x80004001));
    public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
    public void DUnadvise(int connection) { }
    public int EnumDAdvise(out IEnumSTATDATA enumerator) { enumerator = null!; return unchecked((int)0x80040003); }
    public void SetAsyncMode(bool enabled) => asyncMode = enabled;
    public void GetAsyncMode(out bool enabled) => enabled = asyncMode;
    public void StartOperation(IBindCtx? context) => active = true;
    public void InOperation(out bool value) => value = active;
    public void EndOperation(int result, IBindCtx? context, uint effects) => active = false;
    private static class Native
    {
        [DllImport("kernel32.dll")] internal static extern nint GlobalAlloc(uint flags, nuint length);
        [DllImport("kernel32.dll")] internal static extern nint GlobalLock(nint memory);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GlobalUnlock(nint memory);
        [DllImport("kernel32.dll")] internal static extern nint GlobalFree(nint memory);
    }
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class FormatEnumerator(FORMATETC[] formats) : IEnumFORMATETC
{
    private int index;
    public int Next(int count, FORMATETC[] destination, int[]? fetched)
    { var copied = 0; while (copied < count && index < formats.Length) destination[copied++] = formats[index++]; if (fetched?.Length > 0) fetched[0] = copied; return copied == count ? 0 : 1; }
    public int Skip(int count) { var previous = index; index = Math.Min(index + count, formats.Length); return index - previous == count ? 0 : 1; }
    public int Reset() { index = 0; return 0; }
    public void Clone(out IEnumFORMATETC clone) => clone = new FormatEnumerator(formats) { index = index };
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class SnapshotStream(Lazy<Task<string>> source) : IStream
{
    private FileStream? stream;
    private FileStream File => stream ??= System.IO.File.OpenRead(source.Value.GetAwaiter().GetResult());
    ~SnapshotStream() => stream?.Dispose();
    public void Read(byte[] buffer, int count, nint read) { var n = File.Read(buffer, 0, count); if (read != 0) Marshal.WriteInt32(read, n); }
    public void Seek(long offset, int origin, nint position) { var value = File.Seek(offset, (SeekOrigin)origin); if (position != 0) Marshal.WriteInt64(position, value); }
    public void Stat(out STATSTG stat, int flags) => stat = new() { type = 2, cbSize = File.Length, grfMode = 0 };
    public void CopyTo(IStream target, long count, nint read, nint written)
    {
        long totalRead = 0, totalWritten = 0; var buffer = new byte[1024 * 1024]; var result = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            while (totalRead < count)
            {
                var n = File.Read(buffer, 0, (int)Math.Min(buffer.Length, count - totalRead)); if (n == 0) break;
                totalRead += n; Marshal.WriteInt32(result, 0); target.Write(buffer, n, result); var actual = Marshal.ReadInt32(result);
                if (actual < 0 || actual > n) throw new COMException("拖放目标返回无效的写入长度", unchecked((int)0x80030070));
                totalWritten += actual; if (actual != n) throw new COMException("拖放目标没有完整写入，请检查电脑空间", unchecked((int)0x80030070));
            }
        }
        finally { Marshal.FreeHGlobal(result); if (read != 0) Marshal.WriteInt64(read, totalRead); if (written != 0) Marshal.WriteInt64(written, totalWritten); }
    }
    public void Clone(out IStream clone) { var copy = new SnapshotStream(source); copy.File.Position = File.Position; clone = copy; }
    public void Commit(int flags) { }
    public void Write(byte[] data, int count, nint written) => throw new COMException("只读", unchecked((int)0x80030005));
    public void SetSize(long size) => throw new COMException("只读", unchecked((int)0x80030005));
    public void Revert() => throw new COMException("不支持", unchecked((int)0x80004001));
    public void LockRegion(long offset, long count, int type) => throw new COMException("不支持", unchecked((int)0x80004001));
    public void UnlockRegion(long offset, long count, int type) => throw new COMException("不支持", unchecked((int)0x80004001));
}
