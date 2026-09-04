using System.Text;

namespace Encomm.Browser.Security;

/// <summary>
/// Defensive sanitization for text that crosses from untrusted web
/// content into the native host. This is NOT a substitute for full DOM
/// sanitization in the renderer; the renderer is treated as untrusted
/// but still does its own escaping. These utilities reduce blast radius
/// if hostile content is ever passed to host code.
/// </summary>
public static class TextSanitizer
{
    private const int MaxExcerptLength = 4 * 1024;
    private const int MaxSelectedLength = 8 * 1024;

    public static string? TrimExcerpt(string? input)
        => Trim(input, MaxExcerptLength);

    public static string? TrimSelection(string? input)
        => Trim(input, MaxSelectedLength);

    private static string? Trim(string? input, int max)
    {
        if (input is null) return null;
        // Strip control characters that should never reach host APIs.
        var sb = new StringBuilder(Math.Min(input.Length, max));
        foreach (var c in input)
        {
            if (c == '\t' || c == '\n' || c == '\r' || (!char.IsControl(c) && c != '\u007F'))
                sb.Append(c);
        }
        var s = sb.ToString();
        return s.Length <= max ? s : s.Substring(0, max);
    }
}