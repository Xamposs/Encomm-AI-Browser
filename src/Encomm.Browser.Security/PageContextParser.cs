using Encomm.Browser.Engine.Abstractions;

namespace Encomm.Browser.Security;

/// <summary>
/// Parses a WebView2 ExecuteScriptAsync result into a PageContext.
/// The engine adapter's script returns a plain object — so the result
/// is the JSON-encoded string of that object. We unwrap the outer
/// JSON string and parse the inner object. All text is passed through
/// <see cref="TextSanitizer"/> bounds before it reaches the host.
/// </summary>
public static class PageContextParser
{
    public static PageContext Parse(string? jsonResult, string url, string title)
    {
        if (string.IsNullOrEmpty(jsonResult) || jsonResult == "null" || jsonResult == "undefined")
            return new PageContext(url, title, null, null, null, null);

        string? inner;
        try
        {
            using var outer = System.Text.Json.JsonDocument.Parse(jsonResult);
            if (outer.RootElement.ValueKind != System.Text.Json.JsonValueKind.String)
                return new PageContext(url, title, null, null, null, null);
            inner = outer.RootElement.GetString();
        }
        catch { return new PageContext(url, title, null, null, null, null); }

        if (string.IsNullOrEmpty(inner)) return new PageContext(url, title, null, null, null, null);

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(inner);
            var root = doc.RootElement;
            var d = root.TryGetProperty("d", out var dv) ? dv.GetString() : null;
            var sel = root.TryGetProperty("sel", out var sv) ? sv.GetString() : null;
            var ex = root.TryGetProperty("ex", out var ev) ? ev.GetString() : null;
            var x = root.TryGetProperty("x", out var xv) ? xv.GetDouble() : 0;
            var y = root.TryGetProperty("y", out var yv) ? yv.GetDouble() : 0;
            return new PageContext(url, title,
                TextSanitizer.TrimExcerpt(d),
                TextSanitizer.TrimSelection(sel),
                TextSanitizer.TrimExcerpt(ex),
                null,
                x, y);
        }
        catch
        {
            return new PageContext(url, title, null, null, null, null);
        }
    }

    /// <summary>
    /// Parses an ExecuteScriptAsync boolean result (used by the
    /// unsaved-form detector). Accepts JSON true/false and the strings
    /// "true"/"false". Anything else — including malformed content —
    /// returns false so lifecycle never protects a tab on bad data.
    /// </summary>
    public static bool ParseBoolResult(string? json)
    {
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            using var outer = System.Text.Json.JsonDocument.Parse(json);
            if (outer.RootElement.ValueKind == System.Text.Json.JsonValueKind.True) return true;
            if (outer.RootElement.ValueKind == System.Text.Json.JsonValueKind.False) return false;
            if (outer.RootElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var s = outer.RootElement.GetString();
                return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        catch { return false; }
    }
}
