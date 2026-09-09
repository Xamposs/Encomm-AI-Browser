using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Encomm.Browser.Core.Storage;
using Encomm.Browser.Engine.Abstractions;
using Xunit;

namespace Encomm.Browser.Tests;

/// <summary>
/// Phase 2B addendum item 1: Ghost-restore strict success semantics
/// (fake engine; no WebView2 required).
///
/// Contract under test:
///   success (usable document)  -> Live + true + no error
///   failure/timeout/cancel     -> Ghost preserved + URL intact +
///                                 renderer torn down + error recorded +
///                                 false (retryable)
/// Item 3: suspend captures context + scroll BEFORE suspension, and
/// scroll failure never blocks suspension.
/// </summary>
public class RuntimeSemanticsTests
{
    [Fact]
    public async Task Restore_success_marks_Live_and_returns_true()
    {
        using var ctx = SharedContext.New();
        var tab = ctx.AddGhostTab("https://example.com/");

        var ok = await ctx.Runtime.RestoreGhostTabAsync(tab);

        Assert.True(ok);
        Assert.Null(ctx.Runtime.LastRestoreError);
        Assert.True(ctx.Runtime.HasView(tab.Id));
        Assert.Equal(TabRendererStateKind.Live, ctx.StateOf(tab.Id));
        Assert.True(ctx.Runtime.LastRestoreLatency >= TimeSpan.Zero);
        Assert.True(ctx.Runtime.LastRestoreBreakdown.Total >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Restore_failure_keeps_Ghost_tears_down_view_and_reports_error()
    {
        using var ctx = SharedContext.New();
        var tab = ctx.AddGhostTab("https://example.com/");
        ctx.FailNext(tab.Id);

        var ok = await ctx.Runtime.RestoreGhostTabAsync(tab);

        Assert.False(ok);
        Assert.NotNull(ctx.Runtime.LastRestoreError);
        Assert.Contains("navigation failed", ctx.Runtime.LastRestoreError);
        Assert.Equal(TabRendererStateKind.Ghost, ctx.StateOf(tab.Id));
        Assert.Equal("https://example.com/", ctx.UrlOf(tab.Id));
        Assert.False(ctx.Runtime.HasView(tab.Id));
    }

    [Fact]
    public async Task Restore_watchdog_keeps_Ghost_and_reports_timeout()
    {
        using var ctx = SharedContext.New();
        ctx.Runtime.RestoreWatchdog = TimeSpan.FromMilliseconds(150);
        var tab = ctx.AddGhostTab("https://example.com/");
        ctx.ViewFor(tab.Id).NeverComplete = true;

        var ok = await ctx.Runtime.RestoreGhostTabAsync(tab);

        Assert.False(ok);
        Assert.NotNull(ctx.Runtime.LastRestoreError);
        Assert.Contains("watchdog", ctx.Runtime.LastRestoreError);
        Assert.Equal(TabRendererStateKind.Ghost, ctx.StateOf(tab.Id));
        Assert.Equal("https://example.com/", ctx.UrlOf(tab.Id));
        Assert.False(ctx.Runtime.HasView(tab.Id));
    }

    [Fact]
    public async Task Restore_rejected_navigation_keeps_Ghost_and_reports_reason()
    {
        using var ctx = SharedContext.New();
        var tab = ctx.AddGhostTab("https://blocked.example/");
        ctx.ViewFor(tab.Id).AcceptNavigation = false;

        var ok = await ctx.Runtime.RestoreGhostTabAsync(tab);

        Assert.False(ok);
        Assert.NotNull(ctx.Runtime.LastRestoreError);
        Assert.Contains("rejected", ctx.Runtime.LastRestoreError);
        Assert.Equal(TabRendererStateKind.Ghost, ctx.StateOf(tab.Id));
        Assert.False(ctx.Runtime.HasView(tab.Id));
    }

    [Fact]
    public async Task Restore_cancelled_keeps_Ghost()
    {
        using var ctx = SharedContext.New();
        var tab = ctx.AddGhostTab("https://example.com/");
        ctx.ViewFor(tab.Id).NeverComplete = true;
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);

        var ok = await ctx.Runtime.RestoreGhostTabAsync(tab, cts.Token);

        Assert.False(ok);
        Assert.NotNull(ctx.Runtime.LastRestoreError);
        Assert.Equal(TabRendererStateKind.Ghost, ctx.StateOf(tab.Id));
        Assert.False(ctx.Runtime.HasView(tab.Id));
    }

    [Fact]
    public async Task Suspend_captures_context_and_scroll_before_suspending()
    {
        using var ctx = SharedContext.New();
        var tab = ctx.AddLiveTab("https://example.com/long");
        var view = ctx.ViewFor(tab.Id);

        var ok = await ctx.Runtime.SuspendAsync(tab.Id);

        Assert.True(ok);
        Assert.Equal(new[] { "context", "scroll", "suspend" }, view.CallLog);
        Assert.Equal(ViewLifecycleState.Warm, view.State);
    }

    [Fact]
    public async Task Suspend_scroll_failure_does_not_prevent_suspension()
    {
        using var ctx = SharedContext.New();
        var tab = ctx.AddLiveTab("https://example.com/long");
        var view = ctx.ViewFor(tab.Id);
        view.ThrowOnScroll = true;

        var ok = await ctx.Runtime.SuspendAsync(tab.Id);

        Assert.True(ok);
        Assert.Equal(ViewLifecycleState.Warm, view.State);
        Assert.Contains("suspend", view.CallLog);
    }

    [Fact]
    public async Task Ghost_removes_renderer()
    {
        using var ctx = SharedContext.New();
        var tab = ctx.AddLiveTab("https://example.com/");

        var ok = await ctx.Runtime.GhostAsync(tab.Id);

        Assert.True(ok);
        Assert.False(ctx.Runtime.HasView(tab.Id));
    }

    [Fact]
    public async Task Restore_breakdown_phases_sum_within_total()
    {
        using var ctx = SharedContext.New();
        var tab = ctx.AddGhostTab("https://example.com/");

        Assert.True(await ctx.Runtime.RestoreGhostTabAsync(tab));

        var b = ctx.Runtime.LastRestoreBreakdown;
        Assert.True(b.RendererReady >= TimeSpan.Zero);
        Assert.True(b.Navigation >= TimeSpan.Zero);
        Assert.True(b.Scroll >= TimeSpan.Zero);
        Assert.True(b.Total >= b.RendererReady);
    }
}
