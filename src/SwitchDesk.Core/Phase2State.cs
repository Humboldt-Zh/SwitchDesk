using System.Runtime.InteropServices;
using System.Text.Json;

namespace SwitchDesk.Core;

/// <summary>Private, independent phase-two database. Never migrates the existing library or backup format.</summary>
public sealed class Phase2State : IDisposable
{
    private readonly nint db;
    private readonly object sync = new();
    private bool disposed;
    public Phase2State(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Check(LibraryDatabase.Native.sqlite3_open_v2(path, out db, 6, 0));
        try
        {
            Exec("PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
            Exec("CREATE TABLE IF NOT EXISTS state_meta(version INTEGER NOT NULL); INSERT INTO state_meta SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM state_meta);");
            if (ReadVersion() != 1) throw new InvalidDataException("二期状态库版本不兼容，请使用创建它的 SwitchDesk 版本");
            Exec("CREATE TABLE IF NOT EXISTS records(scope TEXT NOT NULL, id TEXT NOT NULL, value TEXT NOT NULL, revision INTEGER NOT NULL, PRIMARY KEY(scope,id));");
        }
        catch { LibraryDatabase.Native.sqlite3_close_v2(db); throw; }
    }
    private void Check(int code)
    { if (code != 0) throw new IOException("SQLite: " + Marshal.PtrToStringUTF8(LibraryDatabase.Native.sqlite3_errmsg(db))); }
    private void Exec(string sql)
    {
        var code = LibraryDatabase.Native.sqlite3_exec(db, sql, 0, 0, out var error);
        try { Check(code); } finally { if (error != 0) LibraryDatabase.Native.sqlite3_free(error); }
    }
    private int ReadVersion()
    {
        Check(LibraryDatabase.Native.sqlite3_prepare_v2(db, "SELECT version FROM state_meta", -1, out var stmt, 0));
        try { if (LibraryDatabase.Native.sqlite3_step(stmt) != 100) throw new InvalidDataException("二期状态库缺少版本"); return checked((int)LibraryDatabase.Native.sqlite3_column_int64(stmt, 0)); }
        finally { LibraryDatabase.Native.sqlite3_finalize(stmt); }
    }
    private static string Text(nint stmt, int column) => Marshal.PtrToStringUTF8(LibraryDatabase.Native.sqlite3_column_text(stmt, column)) ?? throw new InvalidDataException("二期状态记录为空");
    private void Bind(nint stmt, int index, string value) => Check(LibraryDatabase.Native.sqlite3_bind_text(stmt, index, value, -1, new nint(-1)));
    public (T Value, long Revision)? Get<T>(string scope, string id)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Check(LibraryDatabase.Native.sqlite3_prepare_v2(db, "SELECT value,revision FROM records WHERE scope=?1 AND id=?2", -1, out var stmt, 0));
            try
            {
                Bind(stmt, 1, scope); Bind(stmt, 2, id); var rc = LibraryDatabase.Native.sqlite3_step(stmt);
                if (rc == 101) return null; if (rc != 100) Check(rc);
                return (JsonSerializer.Deserialize<T>(Text(stmt, 0), Wire.Json) ?? throw new InvalidDataException("二期状态记录损坏"), LibraryDatabase.Native.sqlite3_column_int64(stmt, 1));
            }
            finally { LibraryDatabase.Native.sqlite3_finalize(stmt); }
        }
    }
    public T[] List<T>(string scope)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Check(LibraryDatabase.Native.sqlite3_prepare_v2(db, "SELECT value FROM records WHERE scope=?1 ORDER BY id", -1, out var stmt, 0));
            try
            {
                Bind(stmt, 1, scope); var list = new List<T>(); int rc;
                while ((rc = LibraryDatabase.Native.sqlite3_step(stmt)) == 100) list.Add(JsonSerializer.Deserialize<T>(Text(stmt, 0), Wire.Json) ?? throw new InvalidDataException("二期状态记录损坏"));
                if (rc != 101) Check(rc); return list.ToArray();
            }
            finally { LibraryDatabase.Native.sqlite3_finalize(stmt); }
        }
    }
    /// <summary>CAS prevents stale dialogs, duplicate clicks and reconnects from overwriting newer operation state.</summary>
    public long Put<T>(string scope, string id, T value, long? expectedRevision = null)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(id)) throw new ArgumentException("状态标识不能为空");
        var json = JsonSerializer.Serialize(value, Wire.Json);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this); Exec("BEGIN IMMEDIATE;");
            try
            {
                var old = Get<JsonElement>(scope, id); var revision = old?.Revision ?? 0;
                if (expectedRevision is {} expected && expected != revision) throw new InvalidOperationException("记录已变化，请重新比较后操作");
                Check(LibraryDatabase.Native.sqlite3_prepare_v2(db, "INSERT INTO records VALUES(?1,?2,?3,?4) ON CONFLICT(scope,id) DO UPDATE SET value=excluded.value,revision=excluded.revision", -1, out var stmt, 0));
                try
                {
                    Bind(stmt, 1, scope); Bind(stmt, 2, id); Bind(stmt, 3, json); Check(LibraryDatabase.Native.sqlite3_bind_int64(stmt, 4, checked(revision + 1)));
                    var rc = LibraryDatabase.Native.sqlite3_step(stmt); if (rc != 101) Check(rc);
                }
                finally { LibraryDatabase.Native.sqlite3_finalize(stmt); }
                Exec("COMMIT;"); return revision + 1;
            }
            catch { Exec("ROLLBACK;"); throw; }
        }
    }
    public void Remove(string scope, string id, long expectedRevision)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this); Exec("BEGIN IMMEDIATE;");
            try
            {
                if (Get<JsonElement>(scope, id)?.Revision != expectedRevision) throw new InvalidOperationException("记录已变化，请刷新后重试");
                Check(LibraryDatabase.Native.sqlite3_prepare_v2(db, "DELETE FROM records WHERE scope=?1 AND id=?2", -1, out var stmt, 0));
                try { Bind(stmt, 1, scope); Bind(stmt, 2, id); var rc = LibraryDatabase.Native.sqlite3_step(stmt); if (rc != 101) Check(rc); }
                finally { LibraryDatabase.Native.sqlite3_finalize(stmt); }
                Exec("COMMIT;");
            }
            catch { Exec("ROLLBACK;"); throw; }
        }
    }
    public void Dispose() { lock (sync) { if (disposed) return; disposed = true; LibraryDatabase.Native.sqlite3_close_v2(db); } }
}
