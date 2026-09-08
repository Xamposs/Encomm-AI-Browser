using System;
using Encomm.Browser.Core.Storage;
using Encomm.Browser.Tabs;
using Xunit;

namespace Encomm.Browser.Tests;

public class LifecyclePolicyTests
{
    private static readonly TimeSpan Warm = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Ghost = TimeSpan.FromMinutes(20);
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static TabRecord Tab(
        TabRendererStateKind rs = TabRendererStateKind.Live,
        TabLogicalStateKind ls = TabLogicalStateKind.Background,
        bool pinned = false, bool keepAwake = false,
        DateTimeOffset? last = null) => new(
            Guid.NewGuid(), Guid.NewGuid(), "https://x", "X", null,
            rs, ls, pinned, false, keepAwake,
            Now.AddHours(-1), last ?? Now.AddHours(-1), 0, null);

    [Fact]
    public void Active_tab_never_transitions()
    {
        var active = Guid.NewGuid();
        var t = Tab(TabRendererStateKind.Live, TabLogicalStateKind.Background,
            last: Now.AddHours(-5));
        t = t with { Id = active };
        Assert.Equal(TabLifecyclePolicy.Decision.None,
            TabLifecyclePolicy.Decide(t, active, Now, Warm, Ghost, false));
    }

    [Fact]
    public void Pinned_and_keepawake_never_transition()
    {
        Assert.Equal(TabLifecyclePolicy.Decision.None,
            TabLifecyclePolicy.Decide(Tab(true, false), null, Now, Warm, Ghost, false));
        Assert.Equal(TabLifecyclePolicy.Decision.None,
            TabLifecyclePolicy.Decide(Tab(false, true), null, Now, Warm, Ghost, false));

        static TabRecord Tab(bool pinned, bool keep) =>
            new(Guid.NewGuid(), Guid.NewGuid(), "https://x", "X", null,
                TabRendererStateKind.Live, TabLogicalStateKind.Background,
                pinned, false, keep, Now.AddHours(-1), Now.AddHours(-5), 0, null);
    }

    [Fact]
    public void Logical_active_never_transitions()
    {
        var t = Tab(TabRendererStateKind.Live, TabLogicalStateKind.Active, last: Now.AddHours(-5));
        Assert.Equal(TabLifecyclePolicy.Decision.None,
            TabLifecyclePolicy.Decide(t, null, Now, Warm, Ghost, false));
    }

    [Fact]
    public void Idle_live_becomes_warm()
    {
        var t = Tab(TabRendererStateKind.Live, TabLogicalStateKind.Background, last: Now.AddMinutes(-6));
        Assert.Equal(TabLifecyclePolicy.Decision.Warm,
            TabLifecyclePolicy.Decide(t, null, Now, Warm, Ghost, false));
    }

    [Fact]
    public void Fresh_live_stays()
    {
        var t = Tab(TabRendererStateKind.Live, TabLogicalStateKind.Background, last: Now.AddMinutes(-1));
        Assert.Equal(TabLifecyclePolicy.Decision.None,
            TabLifecyclePolicy.Decide(t, null, Now, Warm, Ghost, false));
    }

    [Fact]
    public void Old_non_ghost_becomes_ghost()
    {
        var t = Tab(TabRendererStateKind.Warm, TabLogicalStateKind.Background, last: Now.AddMinutes(-30));
        Assert.Equal(TabLifecyclePolicy.Decision.Ghost,
            TabLifecyclePolicy.Decide(t, null, Now, Warm, Ghost, false));
    }

    [Fact]
    public void Ghost_stays_ghost()
    {
        var t = Tab(TabRendererStateKind.Ghost, TabLogicalStateKind.Background, last: Now.AddHours(-5));
        Assert.Equal(TabLifecyclePolicy.Decision.None,
            TabLifecyclePolicy.Decide(t, null, Now, Warm, Ghost, false));
    }

    [Fact]
    public void Unsaved_form_blocks_automatic_ghost_but_not_warm()
    {
        var oldWarm = Tab(TabRendererStateKind.Warm, TabLogicalStateKind.Background, last: Now.AddMinutes(-30));
        Assert.Equal(TabLifecyclePolicy.Decision.None,
            TabLifecyclePolicy.Decide(oldWarm, null, Now, Warm, Ghost, hasUnsavedForm: true));

        var idleLive = Tab(TabRendererStateKind.Live, TabLogicalStateKind.Background, last: Now.AddMinutes(-6));
        Assert.Equal(TabLifecyclePolicy.Decision.Warm,
            TabLifecyclePolicy.Decide(idleLive, null, Now, Warm, Ghost, hasUnsavedForm: true));
    }

    [Fact]
    public void Adaptive_halves_ghost_threshold_under_pressure()
    {
        var t = Tab(TabRendererStateKind.Warm, TabLogicalStateKind.Background, last: Now.AddMinutes(-12));
        // 12 min < 20 min: no ghost normally…
        Assert.Equal(TabLifecyclePolicy.Decision.None,
            TabLifecyclePolicy.Decide(t, null, Now, Warm, Ghost, false));
        // …but pressured Adaptive ghosts at 10 min.
        Assert.Equal(TabLifecyclePolicy.Decision.Ghost,
            TabLifecyclePolicy.Decide(t, null, Now, Warm, Ghost, false,
                memoryPressured: true, halveGhostWhenPressured: true));
        // Without the Adaptive flag, pressure alone does nothing.
        Assert.Equal(TabLifecyclePolicy.Decision.None,
            TabLifecyclePolicy.Decide(t, null, Now, Warm, Ghost, false,
                memoryPressured: true, halveGhostWhenPressured: false));
    }

    [Fact]
    public void Manual_transition_allowed_for_background_refused_for_active()
    {
        var activeId = Guid.NewGuid();
        var bg = Tab();
        var active = Tab() with { Id = activeId };
        Assert.True(TabLifecyclePolicy.AllowManualTransition(bg, activeId));
        Assert.False(TabLifecyclePolicy.AllowManualTransition(active, activeId));
        Assert.False(TabLifecyclePolicy.AllowManualTransition(null!, activeId));
    }
}
