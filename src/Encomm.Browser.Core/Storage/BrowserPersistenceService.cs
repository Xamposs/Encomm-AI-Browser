using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Encomm.Browser.Core;
using Microsoft.Data.Sqlite;

namespace Encomm.Browser.Core.Storage;

[SupportedOSPlatform("windows")]
public sealed class BrowserPersistenceService
{
    private const int CurrentSchemaVersion = 2;

    private readonly SqliteStore _store;
    private readonly ILogger<BrowserPersistenceService> _log;

    public BrowserPersistenceService(SqliteStore store, ILogger<BrowserPersistenceService> log)
    {
        _store = store;
        _log = log;
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var conn = _store.OpenConnection();
        using (var v = conn.CreateCommand())
        {
            v.CommandText = "PRAGMA user_version;";
            var raw = v.ExecuteScalar();
            int current = raw is long l ? (int)l : 0;
            if (current < 1) MigrateTo1(conn);
            if (current < 2) MigrateTo2(conn);
        }
    }

    private static void MigrateTo1(SqliteConnection conn)
    {
        // Initial schema: workspaces, tabs, recently_closed, app_state.
        // Idempotent: each statement uses IF NOT EXISTS.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS workspaces(
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    order_index INTEGER NOT NULL DEFAULT 0,
    built_in INTEGER NOT NULL DEFAULT 0,
    created_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS tabs(
    id TEXT PRIMARY KEY,
    workspace_id TEXT NOT NULL,
    url TEXT NOT NULL,
    title TEXT NOT NULL,
    favicon_url TEXT,
    renderer_state INTEGER NOT NULL DEFAULT 0,
    logical_state INTEGER NOT NULL DEFAULT 0,
    pinned INTEGER NOT NULL DEFAULT 0,
    muted INTEGER NOT NULL DEFAULT 0,
    keep_awake INTEGER NOT NULL DEFAULT 0,
    created_utc TEXT NOT NULL,
    last_interaction_utc TEXT NOT NULL,
    order_index INTEGER NOT NULL DEFAULT 0,
    preview_path TEXT
);
CREATE INDEX IF NOT EXISTS ix_tabs_workspace ON tabs(workspace_id);
CREATE TABLE IF NOT EXISTS recently_closed(
    original_id TEXT PRIMARY KEY,
    url TEXT NOT NULL,
    title TEXT NOT NULL,
    workspace_id TEXT NOT NULL,
    closed_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS app_state(
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
PRAGMA user_version = 1;
";
        cmd.ExecuteNonQuery();
    }

    private static void MigrateTo2(SqliteConnection conn)
    {
        // Schema v2: add scroll_x and scroll_y to tabs.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
ALTER TABLE tabs ADD COLUMN scroll_x REAL NOT NULL DEFAULT 0;
ALTER TABLE tabs ADD COLUMN scroll_y REAL NOT NULL DEFAULT 0;
PRAGMA user_version = 2;
";
        try { cmd.ExecuteNonQuery(); }
        catch { /* columns already exist */ }
    }

    public void SaveWorkspace(WorkspaceRecord w)
    {
        using var conn = _store.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO workspaces(id,name,order_index,built_in,created_utc) " +
                          "VALUES($i,$n,$o,$b,$c) " +
                          "ON CONFLICT(id) DO UPDATE SET name=$n, order_index=$o;";
        cmd.Parameters.AddWithValue("$i", w.Id.ToString());
        cmd.Parameters.AddWithValue("$n", w.Name);
        cmd.Parameters.AddWithValue("$o", w.OrderIndex);
        cmd.Parameters.AddWithValue("$b", w.BuiltIn ? 1 : 0);
        cmd.Parameters.AddWithValue("$c", w.CreatedUtc.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public List<WorkspaceRecord> LoadWorkspaces()
    {
        var list = new List<WorkspaceRecord>();
        using var conn = _store.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,name,order_index,built_in,created_utc FROM workspaces ORDER BY order_index;";
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            list.Add(new WorkspaceRecord(
                Guid.Parse(rdr.GetString(0)),
                rdr.GetString(1),
                rdr.GetInt32(2),
                rdr.GetInt32(3) == 1,
                DateTimeOffset.Parse(rdr.GetString(4))));
        }
        return list;
    }

    public void DeleteWorkspace(Guid id)
    {
        using var conn = _store.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM workspaces WHERE id=$i; DELETE FROM tabs WHERE workspace_id=$i;";
        cmd.Parameters.AddWithValue("$i", id.ToString());
        cmd.ExecuteNonQuery();
    }

    public void SaveTab(TabRecord t)
    {
        using var conn = _store.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO tabs(id,workspace_id,url,title,favicon_url,renderer_state,logical_state,pinned,muted,keep_awake,created_utc,last_interaction_utc,order_index,preview_path,scroll_x,scroll_y)
VALUES($i,$w,$u,$t,$f,$rs,$ls,$p,$m,$k,$c,$l,$o,$pp,$sx,$sy)
ON CONFLICT(id) DO UPDATE SET
  url=$u, title=$t, favicon_url=$f, renderer_state=$rs, logical_state=$ls,
  pinned=$p, muted=$m, keep_awake=$k, last_interaction_utc=$l, order_index=$o,
  preview_path=$pp, scroll_x=$sx, scroll_y=$sy;";
        cmd.Parameters.AddWithValue("$i", t.Id.ToString());
        cmd.Parameters.AddWithValue("$w", t.WorkspaceId.ToString());
        cmd.Parameters.AddWithValue("$u", t.Url ?? "");
        cmd.Parameters.AddWithValue("$t", t.Title ?? "");
        cmd.Parameters.AddWithValue("$f", (object?)t.FaviconUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rs", (int)t.RendererState);
        cmd.Parameters.AddWithValue("$ls", (int)t.LogicalState);
        cmd.Parameters.AddWithValue("$p", t.Pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$m", t.Muted ? 1 : 0);
        cmd.Parameters.AddWithValue("$k", t.KeepAwake ? 1 : 0);
        cmd.Parameters.AddWithValue("$c", t.CreatedUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$l", t.LastInteractionUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$o", t.OrderIndex);
        cmd.Parameters.AddWithValue("$pp", (object?)t.PreviewPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sx", t.ScrollX);
        cmd.Parameters.AddWithValue("$sy", t.ScrollY);
        cmd.ExecuteNonQuery();
    }

    public List<TabRecord> LoadTabs(Guid workspaceId)
    {
        var list = new List<TabRecord>();
        using var conn = _store.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,workspace_id,url,title,favicon_url,renderer_state,logical_state,pinned,muted,keep_awake,created_utc,last_interaction_utc,order_index,preview_path,scroll_x,scroll_y FROM tabs WHERE workspace_id=$w ORDER BY order_index;";
        cmd.Parameters.AddWithValue("$w", workspaceId.ToString());
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            list.Add(new TabRecord(
                Guid.Parse(rdr.GetString(0)),
                Guid.Parse(rdr.GetString(1)),
                rdr.GetString(2),
                rdr.GetString(3),
                rdr.IsDBNull(4) ? null : rdr.GetString(4),
                (TabRendererStateKind)rdr.GetInt32(5),
                (TabLogicalStateKind)rdr.GetInt32(6),
                rdr.GetInt32(7) == 1,
                rdr.GetInt32(8) == 1,
                rdr.GetInt32(9) == 1,
                DateTimeOffset.Parse(rdr.GetString(10)),
                DateTimeOffset.Parse(rdr.GetString(11)),
                rdr.GetInt32(12),
                rdr.IsDBNull(13) ? null : rdr.GetString(13),
                rdr.GetDouble(14),
                rdr.GetDouble(15)));
        }
        return list;
    }

    public void DeleteTab(Guid id)
    {
        using var conn = _store.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tabs WHERE id=$i;";
        cmd.Parameters.AddWithValue("$i", id.ToString());
        cmd.ExecuteNonQuery();
    }

    public void SaveRecentlyClosed(RecentlyClosedRecord r)
    {
        using var conn = _store.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO recently_closed(original_id,url,title,workspace_id,closed_utc)
VALUES($i,$u,$t,$w,$c)
ON CONFLICT(original_id) DO UPDATE SET closed_utc=$c;";
        cmd.Parameters.AddWithValue("$i", r.OriginalTabId.ToString());
        cmd.Parameters.AddWithValue("$u", r.Url);
        cmd.Parameters.AddWithValue("$t", r.Title);
        cmd.Parameters.AddWithValue("$w", r.WorkspaceId.ToString());
        cmd.Parameters.AddWithValue("$c", r.ClosedUtc.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public List<RecentlyClosedRecord> LoadRecentlyClosed(int max = 25)
    {
        var list = new List<RecentlyClosedRecord>();
        using var conn = _store.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT original_id,url,title,workspace_id,closed_utc FROM recently_closed ORDER BY closed_utc DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", max);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            list.Add(new RecentlyClosedRecord(
                Guid.Parse(rdr.GetString(0)),
                rdr.GetString(1),
                rdr.GetString(2),
                Guid.Parse(rdr.GetString(3)),
                DateTimeOffset.Parse(rdr.GetString(4))));
        }
        return list;
    }

    public void DeleteRecentlyClosed(Guid id)
    {
        using var conn = _store.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM recently_closed WHERE original_id=$i;";
        cmd.Parameters.AddWithValue("$i", id.ToString());
        cmd.ExecuteNonQuery();
    }

    public void SetAppState(string key, string value)
    {
        using var conn = _store.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO app_state(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public string? GetAppState(string key)
    {
        using var conn = _store.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_state WHERE key=$k;";
        cmd.Parameters.AddWithValue("$k", key);
        var r = cmd.ExecuteScalar();
        return r as string;
    }
}

public enum TabRendererStateKind { Live = 0, Warm = 1, Ghost = 2 }
public enum TabLogicalStateKind { Active = 0, Background = 1 }

public sealed record WorkspaceRecord(Guid Id, string Name, int OrderIndex, bool BuiltIn, DateTimeOffset CreatedUtc);

public sealed record TabRecord(
    Guid Id,
    Guid WorkspaceId,
    string Url,
    string Title,
    string? FaviconUrl,
    TabRendererStateKind RendererState,
    TabLogicalStateKind LogicalState,
    bool Pinned,
    bool Muted,
    bool KeepAwake,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastInteractionUtc,
    int OrderIndex,
    string? PreviewPath,
    double ScrollX = 0,
    double ScrollY = 0);

public sealed record RecentlyClosedRecord(Guid OriginalTabId, string Url, string Title, Guid WorkspaceId, DateTimeOffset ClosedUtc);
