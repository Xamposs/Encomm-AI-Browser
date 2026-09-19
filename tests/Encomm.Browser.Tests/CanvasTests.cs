using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Encomm.Browser.AI;
using Encomm.Browser.App.Services;
using Encomm.Browser.Core;
using Encomm.Browser.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encomm.Browser.Tests;

/// <summary>
/// Phase 3C — Intent / Canvas first usable version.
///
/// These tests protect the two promises that make a browser-generated
/// workspace trustworthy: it degrades honestly when a model returns less
/// than asked, and every claim stays traceable to a real tab.
/// </summary>
public class CanvasTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "encomm-canvas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SourceLabelIndex Index(params (string Label, string Url)[] sources) =>
        SourceLabelIndex.From(sources
            .Select(s => new ContextSource(s.Label, Guid.NewGuid(), "Tab " + s.Label, s.Url,
                AIContextKind.PageContent, 100, false))
            .ToList());

    // ---- Parser -------------------------------------------------------

    [Fact]
    public void Parser_builds_a_comparison_table_with_per_cell_citations()
    {
        var index = Index(("S1", "https://a.example/"), ("S2", "https://b.example/"));
        var sources = index.ResolveSources(new[] { "S1", "S2" });
        var json = """
        {"title":"Laptop comparison","kind":"comparison","summary":"Two options.",
         "columns":[{"key":"price","label":"Price"},{"key":"memory","label":"Memory"}],
         "rows":[
           {"label":"Model A","cells":[
              {"column":"price","text":"999","sourceLabels":["S1"]},
              {"column":"memory","text":"16GB","sourceLabels":["S1"]}]},
           {"label":"Model B","cells":[
              {"column":"price","text":"1249","sourceLabels":["S2"]},
              {"column":"memory","text":"32GB","sourceLabels":["S2"]}]}
         ],
         "uncertainties":["battery life not listed"]}
        """;

        var canvas = CanvasParser.Parse(json, Guid.NewGuid(), "compare laptops", "Canvas",
            index, sources);

        Assert.Equal(AIResultStatus.Ok, canvas.Status);
        Assert.Equal(CanvasKind.Comparison, canvas.Kind);
        Assert.True(canvas.HasTable);
        Assert.Equal(2, canvas.Columns.Count);
        Assert.Equal(2, canvas.Rows.Count);
        Assert.Equal("Price", canvas.Columns[0].Label);

        var cell = canvas.Rows[0].Cells[0];
        Assert.Equal("price", cell.ColumnKey);
        Assert.Equal("999", cell.Text);
        Assert.Equal(sources[0].TabId, Assert.Single(cell.SourceTabIds));
        Assert.Equal("battery life not listed", Assert.Single(canvas.Uncertainties));
    }

    [Fact]
    public void Parser_falls_back_to_an_evidence_list_when_rows_have_no_attributes()
    {
        var index = Index(("S1", "https://a.example/"));
        var json = """
        {"summary":"Findings","rows":[
          {"label":"Finding one","detail":"Something important","sourceLabels":["S1"]},
          {"label":"Finding two","detail":"Something else"}
        ]}
        """;

        var canvas = CanvasParser.Parse(json, Guid.NewGuid(), "collect evidence", "Canvas",
            index, index.ResolveSources(new[] { "S1" }));

        Assert.Equal(CanvasKind.Evidence, canvas.Kind);
        Assert.False(canvas.HasTable);
        Assert.Equal(2, canvas.Rows.Count);
        Assert.Equal("Something important", canvas.Rows[0].FirstText);
        Assert.Single(canvas.Rows[0].SourceTabIds);
    }

    [Fact]
    public void Parser_synthesises_columns_from_facts_that_differ()
    {
        var index = Index(("S1", "https://a.example/"), ("S2", "https://b.example/"));
        var json = """
        {"summary":"Options","rows":[
          {"label":"A","facts":{"price":"999","ram":"16GB"},"sourceLabels":["S1"]},
          {"label":"B","facts":{"price":"1249","ram":"32GB"},"sourceLabels":["S2"]}
        ]}
        """;

        var canvas = CanvasParser.Parse(json, Guid.NewGuid(), "compare", "Canvas", index,
            index.ResolveSources(new[] { "S1", "S2" }));

        Assert.Equal(CanvasKind.Comparison, canvas.Kind);
        Assert.True(canvas.HasTable);
        Assert.Equal(2, canvas.Columns.Count);
        Assert.Contains(canvas.Columns, c => c.Label.Equals("Price", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parser_refuses_to_fake_a_table_when_attributes_do_not_differ()
    {
        var index = Index(("S1", "https://a.example/"), ("S2", "https://b.example/"));
        var json = """
        {"summary":"Options","rows":[
          {"label":"A","facts":{"status":"available"},"sourceLabels":["S1"]},
          {"label":"B","facts":{"status":"available"},"sourceLabels":["S2"]}
        ]}
        """;

        var canvas = CanvasParser.Parse(json, Guid.NewGuid(), "compare", "Canvas", index,
            index.ResolveSources(new[] { "S1", "S2" }));

        Assert.False(canvas.HasTable);
        Assert.Equal(CanvasKind.Evidence, canvas.Kind);
    }

    [Fact]
    public void Parser_respects_an_explicit_kind()
    {
        var canvas = CanvasParser.Parse("""{"kind":"evidence","rows":[{"label":"Only fact"}]}""",
            Guid.NewGuid(), "intent", "Canvas");

        Assert.Equal(CanvasKind.Evidence, canvas.Kind);
        Assert.Single(canvas.Rows);
    }

    [Fact]
    public void Parser_drops_invented_citations_and_accepts_bracketed_labels()
    {
        var index = Index(("S1", "https://a.example/"));
        var json = """
        {"rows":[{"label":"A","cells":[{"column":"x","text":"v","sourceLabels":["S1","S7","[S1]"]}]}]}
        """;

        var canvas = CanvasParser.Parse(json, Guid.NewGuid(), "intent", "Canvas", index,
            index.ResolveSources(new[] { "S1" }));

        var cell = Assert.Single(Assert.Single(canvas.Rows).Cells);
        Assert.Equal("S1", Assert.Single(cell.SourceLabels));
        Assert.Single(cell.SourceTabIds);
    }

    [Fact]
    public void Parser_keeps_unstructured_output_as_a_summary_canvas()
    {
        var canvas = CanvasParser.Parse("Here is what I found: pricing varies.", Guid.NewGuid(),
            "intent", "Canvas");

        Assert.Equal(CanvasKind.Summary, canvas.Kind);
        Assert.Contains("pricing varies", canvas.Summary);
        Assert.False(canvas.HasTable);
        Assert.Empty(canvas.Rows);
    }

    [Fact]
    public void Canvas_plain_text_keeps_citations_and_sources()
    {
        var index = Index(("S1", "https://a.example/"));
        var sources = index.ResolveSources(new[] { "S1" });
        var json = """
        {"kind":"comparison","columns":[{"key":"price","label":"Price"}],
         "rows":[{"label":"A","cells":[{"column":"price","text":"999","sourceLabels":["S1"]}]}]}
        """;

        var canvas = CanvasParser.Parse(json, Guid.NewGuid(), "compare", "Canvas", index, sources);
        var text = canvas.ToPlainText();

        Assert.Contains("Price", text);
        Assert.Contains("999 (S1)", text);
        Assert.Contains("https://a.example/", text);
    }

    [Fact]
    public void Canvas_payload_round_trips()
    {
        var index = Index(("S1", "https://a.example/"));
        var sources = index.ResolveSources(new[] { "S1" });
        var original = CanvasParser.Parse("""
        {"title":"T","kind":"comparison","summary":"s",
         "columns":[{"key":"price","label":"Price"}],
         "rows":[{"label":"A","cells":[{"column":"price","text":"999","sourceLabels":["S1"]}]}],
         "uncertainties":["u"]}
        """, Guid.NewGuid(), "intent", "Canvas", index, sources, truncatedContext: true);

        var restored = CanvasPayload.Deserialize(CanvasPayload.Serialize(original));

        Assert.NotNull(restored);
        Assert.Equal(original.Id, restored!.Id);
        Assert.Equal(CanvasKind.Comparison, restored.Kind);
        Assert.Equal(original.Summary, restored.Summary);
        Assert.True(restored.TruncatedContext);
        Assert.Single(restored.Rows[0].Cells);
        Assert.Equal(sources[0].TabId, restored.Rows[0].Cells[0].SourceTabIds[0]);
    }

    [Fact]
    public void Canvas_payload_never_throws_on_bad_input()
    {
        Assert.Null(CanvasPayload.Deserialize(null));
        Assert.Null(CanvasPayload.Deserialize(""));
        Assert.Null(CanvasPayload.Deserialize("not json at all"));
        Assert.Null(CanvasPayload.Deserialize("{\"unexpected\":true"));
    }

    // ---- Persistence --------------------------------------------------

    private static AICanvas SampleCanvas(Guid workspaceId, string intent) =>
        CanvasParser.Parse($"{{\"summary\":\"{intent}\"}}", workspaceId, intent, "Canvas");

    private static CanvasRecord ToRecord(AICanvas canvas) => new(
        canvas.Id, canvas.WorkspaceId, canvas.Title, canvas.Intent,
        (int)canvas.Kind, (int)canvas.Status, CanvasPayload.Serialize(canvas),
        canvas.CreatedUtc, DateTimeOffset.UtcNow);

    [Fact]
    public void Canvas_records_round_trip_per_workspace_newest_first()
    {
        var dir = NewTempDir();
        try
        {
            var paths = new BrowserPaths(dir);
            using var store = new SqliteStore(paths.DatabaseFile);
            var svc = new BrowserPersistenceService(store, NullLogger<BrowserPersistenceService>.Instance);
            var workspace = Guid.NewGuid();
            var other = Guid.NewGuid();

            var older = SampleCanvas(workspace, "older") with { CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
            var newer = SampleCanvas(workspace, "newer");
            svc.SaveCanvas(ToRecord(older));
            svc.SaveCanvas(ToRecord(newer));
            svc.SaveCanvas(ToRecord(SampleCanvas(other, "elsewhere")));

            Assert.Single(svc.LoadCanvases(other));
            Assert.Equal(2, svc.LoadCanvases(workspace).Count);
            Assert.Equal("newer", CanvasPayload.Deserialize(svc.LoadLatestCanvas(workspace)!.PayloadJson)!.Intent);
            Assert.Null(svc.LoadLatestCanvas(Guid.NewGuid()));

            svc.DeleteCanvas(older.Id);
            Assert.Single(svc.LoadCanvases(workspace));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Deleting_a_workspace_removes_its_canvases()
    {
        var dir = NewTempDir();
        try
        {
            var paths = new BrowserPaths(dir);
            using var store = new SqliteStore(paths.DatabaseFile);
            var svc = new BrowserPersistenceService(store, NullLogger<BrowserPersistenceService>.Instance);
            var workspace = Guid.NewGuid();

            svc.SaveWorkspace(new WorkspaceRecord(workspace, "W", 0, true, DateTimeOffset.UtcNow));
            svc.SaveCanvas(ToRecord(SampleCanvas(workspace, "intent")));
            Assert.Single(svc.LoadCanvases(workspace));

            svc.DeleteWorkspace(workspace);

            Assert.Empty(svc.LoadCanvases(workspace));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Canvas_schema_upgrades_an_existing_v2_database_without_data_loss()
    {
        var dir = NewTempDir();
        try
        {
            var paths = new BrowserPaths(dir);
            var workspace = Guid.NewGuid();

            // Exactly the shape an install from before Phase 3C has: the
            // v1+v2 tables, user_version = 2, and no canvases table.
            using (var store = new SqliteStore(paths.DatabaseFile))
            {
                using var conn = store.OpenConnection();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS workspaces(id TEXT PRIMARY KEY,name TEXT NOT NULL,order_index INTEGER NOT NULL DEFAULT 0,built_in INTEGER NOT NULL DEFAULT 0,created_utc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS tabs(id TEXT PRIMARY KEY,workspace_id TEXT NOT NULL,url TEXT NOT NULL,title TEXT NOT NULL,favicon_url TEXT,renderer_state INTEGER NOT NULL DEFAULT 0,logical_state INTEGER NOT NULL DEFAULT 0,pinned INTEGER NOT NULL DEFAULT 0,muted INTEGER NOT NULL DEFAULT 0,keep_awake INTEGER NOT NULL DEFAULT 0,created_utc TEXT NOT NULL,last_interaction_utc TEXT NOT NULL,order_index INTEGER NOT NULL DEFAULT 0,preview_path TEXT,scroll_x REAL NOT NULL DEFAULT 0,scroll_y REAL NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS app_state(key TEXT PRIMARY KEY,value TEXT NOT NULL);
PRAGMA user_version = 2;";
                    cmd.ExecuteNonQuery();
                }

                using var insert = conn.CreateCommand();
                insert.CommandText =
                    "INSERT INTO workspaces(id,name,order_index,built_in,created_utc) VALUES($i,'Legacy',0,1,$c);" +
                    "INSERT INTO tabs(id,workspace_id,url,title,created_utc,last_interaction_utc) " +
                    "VALUES($t,$i,'https://legacy.example/','Legacy tab',$c,$c);";
                insert.Parameters.AddWithValue("$i", workspace.ToString());
                insert.Parameters.AddWithValue("$t", Guid.NewGuid().ToString());
                insert.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("o"));
                insert.ExecuteNonQuery();
            }

            // Opening the service must migrate to v3 and leave the legacy
            // workspace and tab rows fully intact.
            using (var store = new SqliteStore(paths.DatabaseFile))
            {
                var svc = new BrowserPersistenceService(store, NullLogger<BrowserPersistenceService>.Instance);

                Assert.Single(svc.LoadWorkspaces());
                var tabs = svc.LoadTabs(workspace);
                Assert.Single(tabs);
                Assert.Equal("https://legacy.example/", tabs[0].Url);
                Assert.Null(svc.LoadLatestCanvas(workspace));

                svc.SaveCanvas(ToRecord(SampleCanvas(workspace, "after migration")));
                var loaded = CanvasPayload.Deserialize(svc.LoadLatestCanvas(workspace)!.PayloadJson);
                Assert.Equal("after migration", loaded!.Intent);
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---- CanvasService lifecycle (fake engine, no WebView2) -----------

    private sealed class CanvasFixture : IDisposable
    {
        private readonly string _dir;
        public SharedContext Context { get; }
        public CanvasService Canvases { get; }

        public CanvasFixture(IModelRouter router)
        {
            _dir = NewTempDir();
            Context = SharedContext.New();
            var ai = new AIService(router, new FakeSecretStore(), Context.Runtime,
                NullLogger<AIService>.Instance);
            Canvases = new CanvasService(Context.Persistence, ai, NullLogger<CanvasService>.Instance);
        }

        public void Dispose()
        {
            Context.Dispose();
            try { Directory.Delete(_dir, true); } catch { }
        }
    }

    [Fact]
    public async Task CanvasService_generates_and_persists_a_usable_canvas()
    {
        using var fixture = new CanvasFixture(new RecordingRouter("""
            {"kind":"comparison","summary":"Options","columns":[{"key":"price","label":"Price"}],
             "rows":[{"label":"A","cells":[{"column":"price","text":"999","sourceLabels":["S1"]}]}]}
            """));
        var live = fixture.Context.AddLiveTab("https://a.example/");

        var canvas = await fixture.Canvases.GenerateAsync(
            fixture.Context.WorkspaceId, "compare", live, new[] { live });

        Assert.Equal(AIResultStatus.Ok, canvas.Status);
        Assert.True(canvas.HasTable);

        // The canvas is a workspace artifact: it survives a reload.
        var restored = fixture.Canvases.LoadLatest(fixture.Context.WorkspaceId);
        Assert.NotNull(restored);
        Assert.Equal(canvas.Id, restored!.Id);
        Assert.Equal(canvas.Summary, restored.Summary);
    }

    [Fact]
    public async Task CanvasService_never_stores_unusable_canvases()
    {
        using var fixture = new CanvasFixture(new FakeRouter());
        var live = fixture.Context.AddLiveTab("https://example.com/");

        var canvas = await fixture.Canvases.GenerateAsync(
            fixture.Context.WorkspaceId, "compare", live, new[] { live });

        // Unconfigured: a transient UX state, not workspace content. Storing
        // it would pollute the workspace with an empty canvas.
        Assert.Equal(AIResultStatus.NotConfigured, canvas.Status);
        Assert.Null(fixture.Canvases.LoadLatest(fixture.Context.WorkspaceId));
    }

    [Fact]
    public async Task CanvasService_reports_no_sources_for_an_empty_workspace()
    {
        using var fixture = new CanvasFixture(new RecordingRouter("{}"));
        var native = fixture.Context.AddLiveTab("encomm://newtab");

        var canvas = await fixture.Canvases.GenerateAsync(
            fixture.Context.WorkspaceId, "compare", native, new[] { native });

        Assert.Equal(AIResultStatus.NoSources, canvas.Status);
        Assert.Null(fixture.Canvases.LoadLatest(fixture.Context.WorkspaceId));
    }

    [Fact]
    public async Task Canvas_generation_never_wakes_cold_tabs()
    {
        using var fixture = new CanvasFixture(new RecordingRouter("{\"summary\":\"ok\"}"));
        var ghost = fixture.Context.AddGhostTab("https://cold.example/");
        var live = fixture.Context.AddLiveTab("https://active.example/");

        await fixture.Canvases.GenerateAsync(
            fixture.Context.WorkspaceId, "compare", live, new[] { ghost, live });

        Assert.DoesNotContain("context", fixture.Context.ViewFor(ghost.Id).CallLog);
        Assert.False(fixture.Context.Runtime.HasView(ghost.Id));
    }

    [Fact]
    public async Task Canvas_generation_never_throws_when_the_provider_fails()
    {
        using var fixture = new CanvasFixture(new FailingRouter());
        var live = fixture.Context.AddLiveTab("https://example.com/");

        var canvas = await fixture.Canvases.GenerateAsync(
            fixture.Context.WorkspaceId, "compare", live, new[] { live });

        Assert.Equal(AIResultStatus.Failed, canvas.Status);
        Assert.Contains("could not build", canvas.Message);
    }
}