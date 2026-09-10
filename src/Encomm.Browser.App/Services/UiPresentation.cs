using Encomm.Browser.Core.Storage;
using Encomm.Browser.Engine.Abstractions;

namespace Encomm.Browser.App.Services;

/// <summary>
/// Pure UI-presentation mappings (no WinUI types): theme setting to
/// XAML theme name, and tab lifecycle/a11y state to badge visuals.
/// Unit-tested; the XAML layer consumes the results.
/// </summary>
public static class ThemeMapper
{
    /// <summary>BrowserSettings.Theme ("System"|"Light"|"Dark") to the
    /// XAML RequestedTheme name ("Default"|"Light"|"Dark").</summary>
    public static string ToElementThemeName(string? theme) => theme switch
    {
        "Light" => "Light",
        "Dark" => "Dark",
        _ => "Default",
    };
}

public sealed record TabVisualState(
    /// Developer-mode badge text (LIVE/WARM/GHOST) or null in Everyday.
    string? StateBadge,
    bool ShowStateBadge,
    /// Everyday-mode whisper hint for Ghost tabs (sleep glyph).
    bool ShowGhostHint,
    /// Accessible description for the tab (screen readers).
    string AccessibleName);

public static class TabVisualMapper
{
    public static TabVisualState Map(TabRecord tab, bool developerMode)
    {
        var state = tab.RendererState;
        string? badge = developerMode ? state switch
        {
            TabRendererStateKind.Live => "LIVE",
            TabRendererStateKind.Warm => "WARM",
            TabRendererStateKind.Ghost => "GHOST",
            _ => null,
        } : null;
        var parts = new List<string> { string.IsNullOrEmpty(tab.Title) ? "Untitled tab" : tab.Title };
        if (tab.Pinned) parts.Add("pinned");
        if (tab.Muted) parts.Add("muted");
        if (developerMode) parts.Add(state.ToString().ToLowerInvariant());
        else if (state == TabRendererStateKind.Ghost) parts.Add("sleeping");
        return new TabVisualState(
            badge,
            badge is not null,
            !developerMode && state == TabRendererStateKind.Ghost,
            string.Join(", ", parts));
    }

    /// <summary>Workspace display initials for the switcher glyph
    /// (first letters of up to two words, upper-cased).</summary>
    public static string WorkspaceInitials(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1)
            return words[0].Substring(0, Math.Min(2, words[0].Length)).ToUpperInvariant();
        return (words[0][0].ToString() + words[1][0]).ToUpperInvariant();
    }
}
