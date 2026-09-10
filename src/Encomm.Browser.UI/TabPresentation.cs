namespace Encomm.Browser.UI;

/// <summary>
/// Shared developer-layer visibility state. A plain INPC object (never
/// a Window property): WinUI Windows are not FrameworkElements and
/// cannot be binding sources.
/// </summary>
public sealed class DevModeState : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private bool _showBadges;
    public bool ShowBadges
    {
        get => _showBadges;
        set
        {
            if (_showBadges == value) return;
            _showBadges = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowBadges)));
        }
    }
}

/// <summary>First-letter badge for tabs/omnibox (no renderer, no favicon fetch).</summary>
public static class TabItemHelper
{
    public static string Initial(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "?";
        foreach (var c in title.Trim())
            if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
        return "?";
    }
}
