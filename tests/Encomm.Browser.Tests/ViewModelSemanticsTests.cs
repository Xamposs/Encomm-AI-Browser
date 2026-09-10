using System;
using System.Threading.Tasks;
using Encomm.Browser.App.Services;
using Encomm.Browser.Core.Storage;
using Xunit;

namespace Encomm.Browser.Tests;

/// <summary>
/// Phase 3A item 31: UI flows must never allocate renderers for
/// metadata-only work. Canonical close/select paths under test with
/// the real MainViewModel over a fake engine.
/// </summary>
public class ViewModelSemanticsTests
{
    [Fact]
    public async Task CloseTab_never_materialized_creates_no_renderer()
    {
        using var ctx = SharedContext.New();
        var vm = ctx.CreateViewModel();
        var tab = ctx.AddGhostTab("encomm://newtab");

        await vm.CloseTabAsync(tab);

        Assert.False(ctx.Runtime.HasView(tab.Id));
        Assert.DoesNotContain(ctx.Tabs.Tabs, t => t.Id == tab.Id);
    }

    [Fact]
    public async Task CloseTab_live_tab_tears_down_renderer()
    {
        using var ctx = SharedContext.New();
        var vm = ctx.CreateViewModel();
        var tab = ctx.AddLiveTab("https://example.com/");

        await vm.CloseTabAsync(tab);

        Assert.False(ctx.Runtime.HasView(tab.Id));
        Assert.DoesNotContain(ctx.Tabs.Tabs, t => t.Id == tab.Id);
    }

    [Fact]
    public async Task SelectTab_ghost_restores_without_user_visible_jargon()
    {
        using var ctx = SharedContext.New();
        var vm = ctx.CreateViewModel();
        var tab = ctx.AddGhostTab("https://example.com/");

        await vm.SelectTabAsync(tab);

        Assert.True(ctx.Runtime.HasView(tab.Id));
        Assert.Equal(TabRendererStateKind.Live, ctx.StateOf(tab.Id));
        Assert.Equal(tab.Id, ctx.Tabs.ActiveTab?.Id);
    }

    [Fact]
    public async Task EnsureTabContent_encomm_url_creates_no_view()
    {
        using var ctx = SharedContext.New();
        var tab = ctx.AddGhostTab("encomm://newtab");

        var ok = await ctx.Runtime.EnsureTabContentAsync(tab);

        Assert.True(ok);
        Assert.False(ctx.Runtime.HasView(tab.Id));
    }
}

public class UiPresentationTests
{
    [Theory]
    [InlineData("Dark", "Dark")]
    [InlineData("Light", "Light")]
    [InlineData("System", "Default")]
    [InlineData("", "Default")]
    [InlineData(null, "Default")]
    [InlineData("Neon", "Default")]
    public void ThemeMapper_maps_setting_to_theme_name(string? input, string expected)
    {
        Assert.Equal(expected, ThemeMapper.ToElementThemeName(input));
    }

    [Fact]
    public void TabVisual_dev_mode_shows_badges_no_hint()
    {
        var tab = GhostTab();
        var v = TabVisualMapper.Map(tab, developerMode: true);
        Assert.True(v.ShowStateBadge);
        Assert.Equal("GHOST", v.StateBadge);
        Assert.False(v.ShowGhostHint);
        Assert.Contains("ghost", v.AccessibleName);
    }

    [Fact]
    public void TabVisual_everyday_ghost_shows_hint_no_badge_no_renderer_word()
    {
        var tab = GhostTab();
        var v = TabVisualMapper.Map(tab, developerMode: false);
        Assert.False(v.ShowStateBadge);
        Assert.Null(v.StateBadge);
        Assert.True(v.ShowGhostHint);
        Assert.DoesNotContain("renderer", v.AccessibleName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sleeping", v.AccessibleName);
    }

    [Fact]
    public void TabVisual_everyday_live_shows_nothing_extra()
    {
        var tab = new TabRecord(Guid.NewGuid(), Guid.NewGuid(), "https://x", "X", null,
            TabRendererStateKind.Live, TabLogicalStateKind.Active,
            false, false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null);
        var v = TabVisualMapper.Map(tab, developerMode: false);
        Assert.False(v.ShowStateBadge);
        Assert.False(v.ShowGhostHint);
    }

    [Theory]
    [InlineData("Personal", "PE")]
    [InlineData("Work", "WO")]
    [InlineData("Deep Research", "DR")]
    [InlineData("", "?")]
    public void WorkspaceInitials_are_stable(string name, string expected)
    {
        Assert.Equal(expected, TabVisualMapper.WorkspaceInitials(name));
    }

    private static TabRecord GhostTab() => new(
        Guid.NewGuid(), Guid.NewGuid(), "https://x", "X", null,
        TabRendererStateKind.Ghost, TabLogicalStateKind.Background,
        false, false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null);
}
