using System;
using System.Text.Json;
using Encomm.Browser.Security;
using Xunit;

namespace Encomm.Browser.Tests;

public class PageContextParserTests
{
    private static string Encode(object? o) => JsonSerializer.Serialize(o) is string s
        ? JsonSerializer.Serialize(s)
        : "null";

    [Fact]
    public void Parses_full_object_with_scroll()
    {
        var inner = new { d = "desc", sel = "sel", ex = "body", x = 10.0, y = 200.0 };
        var result = PageContextParser.Parse(Encode(inner), "https://x", "T");
        Assert.Equal("desc", result.Description);
        Assert.Equal("sel", result.SelectedText);
        Assert.Equal("body", result.BodyExcerpt);
        Assert.Equal(10, result.ScrollX);
        Assert.Equal(200, result.ScrollY);
        Assert.Equal("https://x", result.Url);
        Assert.Equal("T", result.Title);
    }

    [Fact]
    public void Null_undefined_empty_become_empty_context()
    {
        foreach (var raw in new[] { (string?)null, "", "null", "undefined" })
        {
            var r = PageContextParser.Parse(raw, "https://x", "T");
            Assert.Null(r.Description);
            Assert.Null(r.SelectedText);
            Assert.Null(r.BodyExcerpt);
            Assert.Equal(0, r.ScrollX);
            Assert.Equal(0, r.ScrollY);
        }
    }

    [Fact]
    public void Malformed_outer_and_inner_become_empty_context()
    {
        var r1 = PageContextParser.Parse("{not json", "https://x", "T");
        Assert.Null(r1.Description);
        // Outer is a valid JSON number, not a string → empty.
        var r2 = PageContextParser.Parse("42", "https://x", "T");
        Assert.Null(r2.Description);
        // Outer string that is not a JSON object → empty.
        var r3 = PageContextParser.Parse(Encode("hello"), "https://x", "T");
        Assert.Null(r3.Description);
    }

    [Fact]
    public void Missing_fields_default_sensibly()
    {
        var result = PageContextParser.Parse(Encode(new { }), "https://x", "T");
        Assert.Null(result.Description);
        Assert.Null(result.SelectedText);
        Assert.Null(result.BodyExcerpt);
        Assert.Equal(0, result.ScrollX);
        Assert.Equal(0, result.ScrollY);
    }

    [Fact]
    public void Long_body_is_bounded()
    {
        var big = new string('a', 100_000);
        var result = PageContextParser.Parse(Encode(new { d = big, sel = big, ex = big, x = 0.0, y = 0.0 }), "https://x", "T");
        Assert.True((result.Description?.Length ?? 0) <= 4 * 1024 + 64);
        Assert.True((result.BodyExcerpt?.Length ?? 0) <= 4 * 1024 + 64);
    }

    [Fact]
    public void ParseBoolResult_accepts_true_false_and_strings()
    {
        Assert.True(PageContextParser.ParseBoolResult("true"));
        Assert.False(PageContextParser.ParseBoolResult("false"));
        Assert.True(PageContextParser.ParseBoolResult(JsonSerializer.Serialize("true")));
        Assert.False(PageContextParser.ParseBoolResult(JsonSerializer.Serialize("false")));
        Assert.False(PageContextParser.ParseBoolResult(null));
        Assert.False(PageContextParser.ParseBoolResult(""));
        Assert.False(PageContextParser.ParseBoolResult("{bad"));
        Assert.False(PageContextParser.ParseBoolResult("42"));
    }
}
