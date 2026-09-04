using Xunit;
using Encomm.Browser.Tabs;
using System;

namespace Encomm.Browser.Tests;

public class TabLifecycleTests
{
    [Fact]
    public void Tab_starts_live()
    {
        var t = new Tab();
        Assert.Equal(TabRendererState.Live, t.RendererState);
    }

    [Fact]
    public void Pin_protects_from_ghosted_state()
    {
        var t = new Tab { Pinned = true };
        Assert.True(t.IsProtected);
    }

    [Fact]
    public void Mute_protects_from_ghosted_state()
    {
        var t = new Tab { Muted = true };
        Assert.True(t.IsProtected);
    }

    [Fact]
    public void KeepAwake_protects_from_ghosted_state()
    {
        var t = new Tab { KeepAwake = true };
        Assert.True(t.IsProtected);
    }

    [Fact]
    public void Engine_state_mapping()
    {
        Assert.Equal(Encomm.Browser.Engine.Abstractions.ViewLifecycleState.Live, TabRendererState.Live.ToEngineState());
        Assert.Equal(Encomm.Browser.Engine.Abstractions.ViewLifecycleState.Warm, TabRendererState.Warm.ToEngineState());
        Assert.Equal(Encomm.Browser.Engine.Abstractions.ViewLifecycleState.Ghost, TabRendererState.Ghost.ToEngineState());
    }
}