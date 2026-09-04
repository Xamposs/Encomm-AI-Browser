using Xunit;
using Encomm.Browser.Core;
using Encomm.Browser.Core.Storage;
using System.IO;
using System.Linq;

namespace Encomm.Browser.Tests;

public class PersistenceTests
{
    [Fact]
    public void Tabs_round_trip()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "encomm-test-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var paths = new BrowserPaths(tmp);
            using var store = new SqliteStore(paths.DatabaseFile);
            var svc = new BrowserPersistenceService(store, Microsoft.Extensions.Logging.Abstractions.NullLogger<BrowserPersistenceService>.Instance);
            var ws = new WorkspaceRecord(System.Guid.NewGuid(), "W", 0, true, System.DateTimeOffset.UtcNow);
            svc.SaveWorkspace(ws);
            var tab = new TabRecord(System.Guid.NewGuid(), ws.Id, "https://example.com", "Example", null,
                TabRendererStateKind.Ghost, TabLogicalStateKind.Background, false, false, false,
                System.DateTimeOffset.UtcNow, System.DateTimeOffset.UtcNow, 0, null);
            svc.SaveTab(tab);
            var loaded = svc.LoadTabs(ws.Id);
            Assert.Single(loaded);
            Assert.Equal("https://example.com", loaded[0].Url);
            Assert.Equal(TabRendererStateKind.Ghost, loaded[0].RendererState);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    [Fact]
    public void Recently_closed_round_trip()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "encomm-test-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var paths = new BrowserPaths(tmp);
            using var store = new SqliteStore(paths.DatabaseFile);
            var svc = new BrowserPersistenceService(store, Microsoft.Extensions.Logging.Abstractions.NullLogger<BrowserPersistenceService>.Instance);
            var ws = new WorkspaceRecord(System.Guid.NewGuid(), "W", 0, true, System.DateTimeOffset.UtcNow);
            svc.SaveWorkspace(ws);
            var rec = new RecentlyClosedRecord(System.Guid.NewGuid(), "https://x.test", "X", ws.Id, System.DateTimeOffset.UtcNow);
            svc.SaveRecentlyClosed(rec);
            var loaded = svc.LoadRecentlyClosed(5);
            Assert.Single(loaded);
            Assert.Equal("X", loaded[0].Title);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }
}