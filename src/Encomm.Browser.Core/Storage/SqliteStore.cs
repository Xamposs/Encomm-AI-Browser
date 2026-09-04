using Microsoft.Data.Sqlite;

namespace Encomm.Browser.Core;

/// <summary>
/// Thin SQLite helper with a schema-version table and migration hook.
/// All persistence goes through <see cref="OpenConnection"/> so we can
/// apply schema changes in one place.
/// </summary>
public sealed class SqliteStore : IDisposable
{
    private readonly string _connectionString;
    private bool _initialized;

    public string DatabaseFile { get; }

    public SqliteStore(string databaseFile)
    {
        DatabaseFile = databaseFile;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        if (!_initialized)
        {
            InitSchema(conn);
            _initialized = true;
        }
        return conn;
    }

    private static void InitSchema(SqliteConnection conn)
    {
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS schema_version(
    version INTEGER NOT NULL
);
INSERT OR IGNORE INTO schema_version(version) VALUES (1);
CREATE TABLE IF NOT EXISTS kv(
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);";
            cmd.ExecuteNonQuery();
        }
    }

    public int GetSchemaVersion()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version LIMIT 1;";
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : 1;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}