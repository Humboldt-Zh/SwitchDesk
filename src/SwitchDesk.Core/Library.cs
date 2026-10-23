using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SwitchDesk.Core;

public sealed class LibraryDatabase : IDisposable
{
    private readonly nint db;
    private readonly object sync = new();
    public LibraryDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Check(Native.sqlite3_open_v2(path, out db, 6, 0));
        Exec("PRAGMA journal_mode=WAL;");
        Exec("CREATE TABLE IF NOT EXISTS library(path TEXT PRIMARY KEY, name TEXT NOT NULL, title_id TEXT, kind TEXT NOT NULL, version TEXT NOT NULL, size INTEGER NOT NULL, sha256 TEXT, status TEXT NOT NULL);");
        Exec("CREATE TABLE IF NOT EXISTS fingerprints(path TEXT PRIMARY KEY, modified INTEGER NOT NULL, sidecar_modified INTEGER NOT NULL, size INTEGER NOT NULL);");
    }
    private void Check(int code) { if (code != 0) throw new IOException("SQLite: " + Marshal.PtrToStringUTF8(Native.sqlite3_errmsg(db))); }
    private void Exec(string sql) { Check(Native.sqlite3_exec(db, sql, 0, 0, out var error)); if (error != 0) Native.sqlite3_free(error); }
    public void Upsert(LibraryItem item)
    {
        lock (sync)
        {
            Check(Native.sqlite3_prepare_v2(db, "INSERT INTO library VALUES(?1,?2,?3,?4,?5,?6,?7,?8) ON CONFLICT(path) DO UPDATE SET name=excluded.name,title_id=excluded.title_id,kind=excluded.kind,version=excluded.version,size=excluded.size,sha256=excluded.sha256,status=excluded.status", -1, out var stmt, 0));
            try
            {
                string?[] values = [item.Path, item.Name, item.TitleId, item.Kind, item.Version, null, item.Sha256, item.Status];
                for (var i = 0; i < values.Length; i++) { if (i == 5) Check(Native.sqlite3_bind_int64(stmt, i + 1, item.Size)); else Check(Native.sqlite3_bind_text(stmt, i + 1, values[i], -1, new nint(-1))); }
                if (Native.sqlite3_step(stmt) != 101) Check(1);
            }
            finally { Native.sqlite3_finalize(stmt); }
        }
    }
    public IReadOnlyList<LibraryItem> List()
    {
        lock (sync)
        {
            Check(Native.sqlite3_prepare_v2(db, "SELECT path,name,title_id,kind,version,size,sha256,status FROM library ORDER BY name", -1, out var stmt, 0));
            var list = new List<LibraryItem>();
            try
            {
                int code;
                while ((code = Native.sqlite3_step(stmt)) == 100)
                {
                    string? Text(int i) => Marshal.PtrToStringUTF8(Native.sqlite3_column_text(stmt, i));
                    list.Add(new(Text(0)!, Text(1)!, Text(2), Text(3)!, Text(4)!, Native.sqlite3_column_int64(stmt, 5), Text(6), Text(7)!));
                }
                if (code != 101) Check(code);
            }
            finally { Native.sqlite3_finalize(stmt); }
            return list;
        }
    }
    public async Task ScanAsync(string directory, CancellationToken ct)
    {
        Directory.CreateDirectory(directory); var known = List().ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            ct.ThrowIfCancellationRequested();
            if (!new[] { ".nsp", ".xci", ".nsz", ".xcz" }.Contains(Path.GetExtension(path).ToLowerInvariant())) continue;
            var info = new FileInfo(path);
            if (known.ContainsKey(path) && MatchesFingerprint(info)) continue;
            LibraryItem item = new(path, info.Name, null, Path.GetExtension(path)[1..].ToUpperInvariant(), "待识别", info.Length, null, "待识别");
            var sidecar = path + ".switchdesk.json";
            if (File.Exists(sidecar))
            {
                try
                {
                    var manifest = JsonSerializer.Deserialize<GameManifest>(await File.ReadAllTextAsync(sidecar, ct), Wire.Json);
                    if (manifest?.FormatVersion == 1 && manifest.Size == info.Length && manifest.Component != null &&
                        !string.IsNullOrWhiteSpace(manifest.Name) && !string.IsNullOrWhiteSpace(manifest.TitleId) && manifest.Sha256?.Length == 64)
                        item = item with { Name = manifest.Name, TitleId = manifest.TitleId, Kind = manifest.Component.Kind, Version = manifest.Component.Version.ToString(), Sha256 = manifest.Sha256, Status = "有导出清单，待校验" };
                    else item = item with { Status = "清单无效" };
                }
                catch (Exception ex) when (ex is JsonException or IOException) { item = item with { Status = "清单无效" }; }
            }
            Upsert(item);
            RecordFingerprint(info);
        }
    }
    public async Task<LibraryItem> VerifyAsync(LibraryItem item, CancellationToken ct)
    {
        await using var stream = File.OpenRead(item.Path); var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        var result = item with { Sha256 = item.Sha256 ?? hash, Size = stream.Length, Status = item.Sha256 != null && !hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase) ? "校验不匹配" : "已校验" };
        Upsert(result); return result;
    }
    private static long SidecarStamp(string path) => File.Exists(path + ".switchdesk.json") ? File.GetLastWriteTimeUtc(path + ".switchdesk.json").Ticks : 0;
    private bool MatchesFingerprint(FileInfo info)
    {
        lock (sync)
        {
            Check(Native.sqlite3_prepare_v2(db, "SELECT modified,sidecar_modified,size FROM fingerprints WHERE path=?1", -1, out var stmt, 0));
            try
            {
                Check(Native.sqlite3_bind_text(stmt, 1, info.FullName, -1, new nint(-1)));
                return Native.sqlite3_step(stmt) == 100 && Native.sqlite3_column_int64(stmt, 0) == info.LastWriteTimeUtc.Ticks &&
                    Native.sqlite3_column_int64(stmt, 1) == SidecarStamp(info.FullName) && Native.sqlite3_column_int64(stmt, 2) == info.Length;
            }
            finally { Native.sqlite3_finalize(stmt); }
        }
    }
    private void RecordFingerprint(FileInfo info)
    {
        lock (sync)
        {
            Check(Native.sqlite3_prepare_v2(db, "INSERT OR REPLACE INTO fingerprints VALUES(?1,?2,?3,?4)", -1, out var stmt, 0));
            try
            {
                Check(Native.sqlite3_bind_text(stmt, 1, info.FullName, -1, new nint(-1)));
                Check(Native.sqlite3_bind_int64(stmt, 2, info.LastWriteTimeUtc.Ticks)); Check(Native.sqlite3_bind_int64(stmt, 3, SidecarStamp(info.FullName))); Check(Native.sqlite3_bind_int64(stmt, 4, info.Length));
                if (Native.sqlite3_step(stmt) != 101) Check(1);
            }
            finally { Native.sqlite3_finalize(stmt); }
        }
    }
    public void Dispose() => Native.sqlite3_close_v2(db);
    private static class Native
    {
        private const string Dll = "winsqlite3.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint db, int flags, nint vfs);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close_v2(nint db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint sqlite3_errmsg(nint db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_exec(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, nint cb, nint arg, out nint error);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void sqlite3_free(nint ptr);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out nint stmt, nint tail);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_text(nint stmt, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string? value, int bytes, nint destructor);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(nint stmt, int index, long value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(nint stmt);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint sqlite3_column_text(nint stmt, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_column_int64(nint stmt, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(nint stmt);
    }
}
