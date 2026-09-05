using System;
using Xunit;
using Encomm.Browser.Memory;

namespace Encomm.Browser.Tests;

public class MemoryProbeTests
{
    [Fact]
    public void Sample_returns_host_memory_and_zero_tabs_when_source_empty()
    {
        var probe = new MemoryProbe(() => Array.Empty<TabStateSummary>());
        var s = probe.Sample();
        Assert.True(s.HostWorkingSetBytes > 0);
        Assert.Equal(0, s.TotalTabs);
        Assert.Equal(0, s.LiveTabs);
        Assert.Equal(0, s.WarmTabs);
        Assert.Equal(0, s.GhostTabs);
        Assert.Equal(s.HostWorkingSetBytes, s.ProcessTreeBytes);
    }

    [Fact]
    public void Sample_aggregates_webview2_child_processes()
    {
        var tabId = Guid.NewGuid();
        var snapshot = new[]
        {
            new Encomm.Browser.Engine.Abstractions.WebViewProcessInfo(101, Encomm.Browser.Engine.Abstractions.WebViewProcessKind.Browser, 100_000_000),
            new Encomm.Browser.Engine.Abstractions.WebViewProcessInfo(102, Encomm.Browser.Engine.Abstractions.WebViewProcessKind.Renderer, 200_000_000),
            new Encomm.Browser.Engine.Abstractions.WebViewProcessInfo(103, Encomm.Browser.Engine.Abstractions.WebViewProcessKind.Gpu, 50_000_000),
            new Encomm.Browser.Engine.Abstractions.WebViewProcessInfo(104, Encomm.Browser.Engine.Abstractions.WebViewProcessKind.Utility, 25_000_000)
        };
        var probe = new MemoryProbe(() => new[] { new TabStateSummary(tabId, TabRendererState.Live) });
        var s = probe.Sample(snapshot);
        Assert.Equal(100_000_000, s.WebView2BrowserBytes);
        Assert.Equal(200_000_000, s.WebView2RendererBytes);
        Assert.Equal(50_000_000, s.WebView2GpuBytes);
        Assert.Equal(25_000_000, s.WebView2UtilityBytes);
        Assert.True(s.ProcessTreeBytes >= s.HostWorkingSetBytes + 375_000_000);
        Assert.Equal(1, s.LiveTabs);
        Assert.Equal(1, s.TotalTabs);
    }

    [Fact]
    public void Sample_ignores_null_process_infos()
    {
        var probe = new MemoryProbe(() => Array.Empty<TabStateSummary>());
        var s = probe.Sample(null);
        Assert.Equal(s.HostWorkingSetBytes, s.ProcessTreeBytes);
    }
}