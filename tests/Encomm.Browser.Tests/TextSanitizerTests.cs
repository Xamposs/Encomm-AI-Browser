using Xunit;
using Encomm.Browser.Security;

namespace Encomm.Browser.Tests;

public class TextSanitizerTests
{
    [Fact]
    public void Null_input_returns_null()
    {
        Assert.Null(TextSanitizer.TrimExcerpt(null));
        Assert.Null(TextSanitizer.TrimSelection(null));
    }

    [Fact]
    public void Strips_control_characters()
    {
        var input = "hello" + (char)1 + (char)2 + "world";
        var s = TextSanitizer.TrimExcerpt(input);
        Assert.NotNull(s);
        Assert.Equal("helloworld", s);
    }

    [Fact]
    public void Truncates_to_max()
    {
        var s = new string('x', 10000);
        var out_ = TextSanitizer.TrimExcerpt(s);
        Assert.NotNull(out_);
        Assert.True(out_!.Length <= 4 * 1024);
    }
}