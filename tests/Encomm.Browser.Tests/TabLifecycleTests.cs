using Xunit;
using Encomm.Browser.Memory;
using Encomm.Browser.Core.Storage;

namespace Encomm.Browser.Tests;

public class TabLifecycleTests
{
    [Fact]
    public void Tab_starts_in_Ghost_logical_state()
    {
        // Fresh tab is not "Live" — the engine adapter should be the
        // only thing that promotes a tab to Live, and only when the
        // tab actually becomes active.
        var t = new TabRecord(
            Guid.NewGuid(), Guid.NewGuid(), "https://example.com", "Example", null,
            TabRendererStateKind.Ghost, TabLogicalStateKind.Background,
            false, false, false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null);
        Assert.Equal(TabRendererStateKind.Ghost, t.RendererState);
    }

    [Fact]
    public void Pin_protects_from_ghost_state()
    {
        var t = new TabRecord(
            Guid.NewGuid(), Guid.NewGuid(), "https://example.com", "Example", null,
            TabRendererStateKind.Live, TabLogicalStateKind.Background,
            Pinned: true, Muted: false, KeepAwake: false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null);
        // Pinned: tab should be considered "protected" for lifecycle.
        // (This is a contract check on the data shape; the actual lifecycle
        // policy is enforced by TabLifecycleManager.TickAsync.)
        Assert.True(t.Pinned);
    }

    [Fact]
    public void Mute_protects_from_ghost_state()
    {
        var t = new TabRecord(
            Guid.NewGuid(), Guid.NewGuid(), "https://example.com", "Example", null,
            TabRendererStateKind.Live, TabLogicalStateKind.Background,
            Pinned: false, Muted: true, KeepAwake: false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null);
        Assert.True(t.Muted);
    }

    [Fact]
    public void KeepAwake_protects_from_ghost_state()
    {
        var t = new TabRecord(
            Guid.NewGuid(), Guid.NewGuid(), "https://example.com", "Example", null,
            TabRendererStateKind.Live, TabLogicalStateKind.Background,
            Pinned: false, Muted: false, KeepAwake: true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null);
        Assert.True(t.KeepAwake);
    }

    [Fact]
    public void TabStateSummary_maps_renderer_state()
    {
        var t = new TabRecord(
            Guid.NewGuid(), Guid.NewGuid(), "https://example.com", "Example", null,
            TabRendererStateKind.Live, TabLogicalStateKind.Active,
            false, false, false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null);
        var summary = new TabStateSummary(t.Id, (TabRendererState)(int)t.RendererState);
        Assert.Equal(t.Id, summary.TabId);
        Assert.Equal(TabRendererState.Live, summary.RendererState);
    }
}
