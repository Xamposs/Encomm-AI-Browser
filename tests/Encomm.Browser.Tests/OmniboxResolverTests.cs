using Xunit;
using Encomm.Browser.Engine.Abstractions;
using System;

namespace Encomm.Browser.Tests;

public class OmniboxResolverTests
{
    [Theory]
    [InlineData("https://example.com/", "https://example.com/")]
    [InlineData("http://example.com/path", "http://example.com/path")]
    public void Recognizes_absolute_url(string input, string expected)
    {
        Assert.Equal(expected, OmniboxResolver.Resolve(input));
    }

    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("sub.example.co", "https://sub.example.co")]
    public void Domain_only_becomes_https(string input, string expected)
    {
        Assert.Equal(expected, OmniboxResolver.Resolve(input));
    }

    [Theory]
    [InlineData("foo bar")]
    [InlineData("hello world")]
    public void Free_text_becomes_search_query(string input)
    {
        var result = OmniboxResolver.Resolve(input);
        Assert.NotNull(result);
        Assert.StartsWith("https://", result!);
        Assert.Contains(Uri.EscapeDataString(input), result!);
    }

    [Fact]
    public void Empty_returns_null()
    {
        Assert.Null(OmniboxResolver.Resolve(""));
        Assert.Null(OmniboxResolver.Resolve("   "));
    }

    [Fact]
    public void Encomm_scheme_passes_through()
    {
        Assert.Equal("encomm://newtab", OmniboxResolver.Resolve("encomm://newtab"));
    }
}