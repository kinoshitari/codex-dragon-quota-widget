using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DragonQuotaWidget.Tests;

public static class SyntheticDatabaseCreator
{
    private const int SQLITE_OK = 0;
    private const int SQLITE_OPEN_READWRITE = 0x00000002;
    private const int SQLITE_OPEN_CREATE = 0x00000004;

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr ppDb, int flags, IntPtr zVfs);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr db);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_exec", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr errmsg);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare16_v2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int sqlite3_prepare16_v2(IntPtr db, [MarshalAs(UnmanagedType.LPWStr)] string zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_int64", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_int64(IntPtr pStmt, int iCol, long value);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_blob", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_blob(IntPtr pStmt, int iCol, byte[] value, int nData, IntPtr destructor);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_text16", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int sqlite3_bind_text16(IntPtr pStmt, int iCol, [MarshalAs(UnmanagedType.LPWStr)] string value, int nLength, IntPtr destructor);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr pStmt);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr pStmt);

    public static void CreateDatabase(string filePath, string trajectoryId, IEnumerable<(long idx, byte[]? metadata)> steps)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var utf8Path = Encoding.UTF8.GetBytes(filePath + "\0");
        int rc = sqlite3_open_v2(utf8Path, out IntPtr db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, IntPtr.Zero);
        if (rc != SQLITE_OK)
        {
            throw new InvalidOperationException($"Failed to open/create SQLite DB: {rc}");
        }

        try
        {
            string schema = @"
                CREATE TABLE steps (
                  idx INTEGER PRIMARY KEY,
                  metadata BLOB,
                  step_payload BLOB,
                  step_type INTEGER,
                  status INTEGER,
                  has_subtrajectory NUMERIC,
                  error_details BLOB,
                  permissions BLOB,
                  task_details BLOB,
                  render_info BLOB,
                  step_format INTEGER
                );
                CREATE TABLE trajectory_meta (
                  trajectory_id TEXT PRIMARY KEY,
                  cascade_id TEXT,
                  trajectory_type INTEGER,
                  source INTEGER
                );
            ";
            byte[] schemaUtf8 = Encoding.UTF8.GetBytes(schema + "\0");
            rc = sqlite3_exec(db, schemaUtf8, IntPtr.Zero, IntPtr.Zero, out _);
            if (rc != SQLITE_OK)
            {
                throw new InvalidOperationException($"Failed to execute schema: {rc}");
            }

            IntPtr stmt = IntPtr.Zero;
            try
            {
                rc = sqlite3_prepare16_v2(db, "INSERT INTO trajectory_meta (trajectory_id) VALUES (?);", -1, out stmt, IntPtr.Zero);
                if (rc != SQLITE_OK) throw new InvalidOperationException("Failed to prepare trajectory_meta insert");
                sqlite3_bind_text16(stmt, 1, trajectoryId, -1, (IntPtr)(-1));
                sqlite3_step(stmt);
            }
            finally
            {
                if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
            }

            foreach (var (idx, metadata) in steps)
            {
                stmt = IntPtr.Zero;
                try
                {
                    rc = sqlite3_prepare16_v2(db, "INSERT INTO steps (idx, metadata) VALUES (?, ?);", -1, out stmt, IntPtr.Zero);
                    if (rc != SQLITE_OK) throw new InvalidOperationException("Failed to prepare steps insert");
                    sqlite3_bind_int64(stmt, 1, idx);
                    if (metadata is not null)
                    {
                        sqlite3_bind_blob(stmt, 2, metadata, metadata.Length, (IntPtr)(-1));
                    }
                    sqlite3_step(stmt);
                }
                finally
                {
                    if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                }
            }
        }
        finally
        {
            sqlite3_close_v2(db);
        }
    }
}
