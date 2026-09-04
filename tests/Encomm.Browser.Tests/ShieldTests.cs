using Xunit;
using Encomm.Browser.Shield;
using Encomm.Browser.Engine.Abstractions;
using System.Collections.Generic;

namespace Encomm.Browser.Tests;

public class ShieldTests
{
    [Fact]
    public void AllowList_decision_when_no_rules_match()
    {
        var p = new FilterRuleProvider(new List<ShieldRule>());
        var b = new RequestBlocker(p);
        var d = b.ShouldBlock("https://example.com/foo", ResourceCategory.Document);
        Assert.True(d.Allow);
    }

    [Fact]
    public void Blocks_known_tracker_host()
    {
        var b = new RequestBlocker(new FilterRuleProvider());
        var d = b.ShouldBlock("https://doubleclick.net/pixel.js", ResourceCategory.Script);
        Assert.False(d.Allow);
        Assert.True(b.Stats.BlockedRequests >= 1);
    }

    [Fact]
    public void Wildcard_subdomain_match_works()
    {
        var b = new RequestBlocker(new FilterRuleProvider());
        var d = b.ShouldBlock("https://anything.ads.example.test/img.png", ResourceCategory.Image);
        Assert.False(d.Allow);
    }

    [Fact]
    public void Disabled_blocker_returns_allow()
    {
        var b = new RequestBlocker(new FilterRuleProvider());
        b.Enabled = false;
        var d = b.ShouldBlock("https://doubleclick.net/x", ResourceCategory.Script);
        Assert.True(d.Allow);
    }

    [Fact]
    public void Bad_url_does_not_throw()
    {
        var b = new RequestBlocker(new FilterRuleProvider());
        var d = b.ShouldBlock("not a url", ResourceCategory.Other);
        Assert.True(d.Allow);
    }

    [Fact]
    public void Stats_count_requests()
    {
        var b = new RequestBlocker(new FilterRuleProvider());
        b.ShouldBlock("https://example.com/a", ResourceCategory.Document);
        b.ShouldBlock("https://example.com/b", ResourceCategory.Document);
        Assert.True(b.Stats.TotalRequests >= 2);
    }
}