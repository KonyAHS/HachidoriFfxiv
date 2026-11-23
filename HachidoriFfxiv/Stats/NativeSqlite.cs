using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HachidoriFfxiv.Stats;

/// <summary>
/// Minimal, zero-dependency P/Invoke wrapper over Dalamud's bundled <c>e_sqlite3.dll</c>
/// for reading and writing Tidepool / Telly / GameSentenceMiner / SubMiner SQLite logs.
/// Degrades cleanly if native SQLite is unavailable on a given platform.
/// </summary>
public sealed class NativeSqlite : IDisposable
{
    private const string LibraryName = "e_sqlite3";
    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_DONE = 101;
    private const int SQLITE_OPEN_READONLY = 0x00000001;
    private const int SQLITE_OPEN_READWRITE = 0x00000002;
    private const int SQLITE_OPEN_CREATE = 0x00000004;
    private const int SQLITE_INTEGER = 1;
    private const int SQLITE_FLOAT = 2;
    private const int SQLITE_TEXT = 3;
    private const int SQLITE_NULL = 5;
    private static readonly IntPtr SQLITE_TRANSIENT = new(-1);

    private IntPtr db;

    private NativeSqlite(IntPtr db) => this.db = db;

    public static bool IsAvailable
    {
        get
        {
            try
            {
                _ = sqlite3_libversion_number();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static NativeSqlite OpenReadWrite(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        return Open(path, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE);
    }

    public static NativeSqlite OpenReadOnly(string path) =>
        Open(path, SQLITE_OPEN_READONLY);

    private static NativeSqlite Open(string path, int flags)
    {
        var rc = sqlite3_open_v2(path, out var handle, flags, null);
        if (rc != SQLITE_OK)
        {
            var msg = handle != IntPtr.Zero ? ReadErrMsg(handle) : $"SQLite error {rc}";
            if (handle != IntPtr.Zero)
                sqlite3_close_v2(handle);
            throw new IOException($"Could not open SQLite database '{path}': {msg}");
        }

        _ = sqlite3_busy_timeout(handle, 3000);
        return new NativeSqlite(handle);
    }

    public void Execute(string sql, params object?[] parameters)
    {
        EnsureOpen();
        if (parameters.Length == 0 && sql.IndexOf(';') >= 0)
        {
            var rc = sqlite3_exec(db, sql, IntPtr.Zero, IntPtr.Zero, out var errPtr);
            if (rc != SQLITE_OK)
            {
                var err = errPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(errPtr) : ReadErrMsg(db);
                if (errPtr != IntPtr.Zero)
                    sqlite3_free(errPtr);
                throw new IOException($"SQLite exec failed ({rc}): {err}");
            }
            return;
        }

        using var stmt = Prepare(sql, parameters);
        var step = sqlite3_step(stmt.Handle);
        if (step is not (SQLITE_DONE or SQLITE_ROW))
            throw new IOException($"SQLite step failed ({step}): {ReadErrMsg(db)}");
    }

    public long LastInsertRowId()
    {
        EnsureOpen();
        return sqlite3_last_insert_rowid(db);
    }

    public bool TableExists(string tableName)
    {
        var rows = Query("SELECT 1 FROM sqlite_master WHERE (type='table' OR type='view') AND name=?1 LIMIT 1", tableName);
        return rows.Count > 0;
    }

    public HashSet<string> GetColumnNames(string tableName)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Validate identifier characters to avoid injection on PRAGMA.
        foreach (var ch in tableName)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_')
                return cols;
        }
        var rows = Query($"PRAGMA table_info({tableName})");
        foreach (var row in rows)
        {
            if (row.Length > 1 && row[1] is string name)
                cols.Add(name);
        }
        return cols;
    }

    public List<object?[]> Query(string sql, params object?[] parameters)
    {
        EnsureOpen();
        using var stmt = Prepare(sql, parameters);
        var colCount = sqlite3_column_count(stmt.Handle);
        var result = new List<object?[]>();

        while (true)
        {
            var rc = sqlite3_step(stmt.Handle);
            if (rc == SQLITE_DONE)
                break;
            if (rc != SQLITE_ROW)
                throw new IOException($"SQLite query failed ({rc}): {ReadErrMsg(db)}");

            var row = new object?[colCount];
            for (var i = 0; i < colCount; i++)
            {
                row[i] = sqlite3_column_type(stmt.Handle, i) switch
                {
                    SQLITE_INTEGER => sqlite3_column_int64(stmt.Handle, i),
                    SQLITE_FLOAT => sqlite3_column_double(stmt.Handle, i),
                    SQLITE_TEXT => Marshal.PtrToStringUTF8(sqlite3_column_text(stmt.Handle, i)) ?? string.Empty,
                    SQLITE_NULL => null,
                    _ => Marshal.PtrToStringUTF8(sqlite3_column_text(stmt.Handle, i)),
                };
            }
            result.Add(row);
        }

        return result;
    }

    private Statement Prepare(string sql, object?[] parameters)
    {
        var sqlBytes = Encoding.UTF8.GetBytes(sql);
        var rc = sqlite3_prepare_v2(db, sqlBytes, sqlBytes.Length, out var stmt, IntPtr.Zero);
        if (rc != SQLITE_OK)
            throw new IOException($"SQLite prepare failed ({rc}): {ReadErrMsg(db)}");

        try
        {
            for (var i = 0; i < parameters.Length; i++)
            {
                var idx = i + 1;
                var val = parameters[i];
                int bindRc = val switch
                {
                    null => sqlite3_bind_null(stmt, idx),
                    int n => sqlite3_bind_int64(stmt, idx, n),
                    long l => sqlite3_bind_int64(stmt, idx, l),
                    bool b => sqlite3_bind_int64(stmt, idx, b ? 1 : 0),
                    double d => sqlite3_bind_double(stmt, idx, d),
                    float f => sqlite3_bind_double(stmt, idx, f),
                    string s => BindText(stmt, idx, s),
                    _ => BindText(stmt, idx, val.ToString() ?? string.Empty),
                };
                if (bindRc != SQLITE_OK)
                    throw new IOException($"SQLite bind failed ({bindRc}): {ReadErrMsg(db)}");
            }

            return new Statement(stmt);
        }
        catch
        {
            sqlite3_finalize(stmt);
            throw;
        }
    }

    private static int BindText(IntPtr stmt, int index, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return sqlite3_bind_text(stmt, index, bytes, bytes.Length, SQLITE_TRANSIENT);
    }

    private static string ReadErrMsg(IntPtr handle) =>
        Marshal.PtrToStringUTF8(sqlite3_errmsg(handle)) ?? "unknown SQLite error";

    private void EnsureOpen()
    {
        if (db == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(NativeSqlite));
    }

    public void Dispose()
    {
        if (db != IntPtr.Zero)
        {
            sqlite3_close_v2(db);
            db = IntPtr.Zero;
        }
    }

    private sealed class Statement(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; private set; } = handle;
        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                sqlite3_finalize(Handle);
                Handle = IntPtr.Zero;
            }
        }
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_libversion_number();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
        out IntPtr ppDb,
        int flags,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? zVfs);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr db);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(
        IntPtr db,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        IntPtr callback,
        IntPtr arg,
        out IntPtr errmsg);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_free(IntPtr ptr);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr db);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_last_insert_rowid(IntPtr db);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr pStmt);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr pStmt);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_count(IntPtr pStmt);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_type(IntPtr pStmt, int iCol);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_column_int64(IntPtr pStmt, int iCol);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern double sqlite3_column_double(IntPtr pStmt, int iCol);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr pStmt, int iCol);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_null(IntPtr pStmt, int index);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_int64(IntPtr pStmt, int index, long value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_double(IntPtr pStmt, int index, double value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text(IntPtr pStmt, int index, byte[] value, int nBytes, IntPtr destructor);
}
