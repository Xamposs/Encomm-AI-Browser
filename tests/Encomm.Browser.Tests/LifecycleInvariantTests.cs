using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Encomm.Browser.App.Services;
using Encomm.Browser.Core;
using Encomm.Browser.Core.Storage;
using Encomm.Browser.Developer;
using Encomm.Browser.Engine.Abstractions;
using Encomm.Browser.Shield;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encomm.Browser.Tests;

/// <summary>
/// Phase 2B addendum item 11: lifecycle state invariants, verified
/// against the real BrowserRuntime + TabService with a fake engine.
///
///   A: Ghost  => runtime holds NO renderer.
///   B: Warm   => runtime view exists AND actual view state is Warm.
///   C: Live   => runtime view exists AND renderer is usable.
///   D: active user-visible tab is never left Ghost after a restore.
///   E: closed tab => no renderer and no persisted record.
/// </summary>
public class LifecycleInvariantTests
{
    [Fact]
    public async Task A_Ghost_implies_no_renderer()
    {
        using var ctx = SharedContext.New();
        var tab = ctx.AddGhostTab("https://example.com/");
        ctx.FailNext(tab.Id);

        var ok = await ctx.Runtime.RestoreGhostTabAsync(tab);

        Assert.False(ok);
        Assert.Equal(TabRendererStateKind.Ghost, ctx.StateOf(tab.Id));
        Assert.False(ctx.Runtime.HasView(tab.Id));
        // The divergence inspector agrees: Ghost + view would be an error,
        // and here there is none.
        var report = Inspect(ctx, tab.Id);
        Assert.DoesNotContain(report.Errors, e => e.Contains("still exists"));
    }

    [Fact]
    public async Task B_Warm_implies_view_exists_and_is_Warm()
    {
        using var ctx = SharedContext.New();
        var tab = ctx.AddLiveTab("https://example.com/");

        var ok = await ctx.Runtime.SuspendAsync(tab.Id);
        ctx.Tabs.SetRendererState(tab, TabRendererStateKind.Warm);

        Assert.True(ok);
        Assert.True(ctx.Runtime.HasView(tab.Id));
        Assert.Equal(ViewLifecycleState.Warm, ctx.Runtime.Views[tab.Id].State);
        Assert.Equal(TabRendererStateKind.Warm, ctx.StateOf(tab.Id));
    }

    [Fact]
    public async Task C_Live_implies_view_exists_and_is_usable()
    {
        using var ctx = SharedContext.New();
        var tab = ctx.AddGhostTab("https://example.com/");

        var ok = await ctx.Runtime.RestoreGhostTabAsync(tab);

        Assert.True(ok);
        Assert.Equal(TabRendererStateKind.Live, ctx.StateOf(tab.Id));
        Assert.True(ctx.Runtime.HasView(tab.Id));
        Assert.Equal(ViewLifecycleState.Live, ctx.Runtime.Views[tab.Id].State);
    }

    [Fact]
    public async Task D_active_tab_is_not_Ghost_after_restore()
    {
        using var ctx = SharedContext.New();
        var tab = ctx.AddGhostTab("https://example.com/");
        ctx.Tabs.SetActive(tab);

        var ok = await ctx.Runtime.RestoreGhostTabAsync(tab);

        Assert.True(ok);
        var current = ctx.Tabs.Tabs.First(t => t.Id == tab.Id);
        Assert.Equal(TabRendererStateKind.Live, current.RendererState);
        Assert.Equal(TabRendererStateKind.Live, ctx.Tabs.ActiveTab?.RendererState);
    }

    [Fact]
    public async Task E_closed_tab_has_no_renderer_and_no_persisted_record()
    {
        using var ctx = SharedContext.New();
        var tab = ctx.AddLiveTab("https://example.com/");

        await ctx.Runtime.GhostAsync(tab.Id);
        ctx.Tabs.Close(tab);

        Assert.False(ctx.Runtime.HasView(tab.Id));
        Assert.DoesNotContain(ctx.Tabs.Tabs, t => t.Id == tab.Id);
        Assert.Empty(ctx.Persistence.LoadTabs(ctx.WorkspaceId));
    }

    private static StateDivergenceReport Inspect(SharedContext ctx, Guid id)
    {
        var tabs = ctx.Tabs.Tabs.ToList();
        var views = ctx.Runtime.Views.ToDictionary(k => k.Key, v => v.Value);
        return new StateDivergenceInspector(tabs, views).Inspect();
    }
}
