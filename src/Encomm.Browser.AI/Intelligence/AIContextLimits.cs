namespace Encomm.Browser.AI;

/// <summary>
/// Hard bounds for anything the browser is willing to hand to a model.
/// These are deliberately conservative: ENCOMM never sends whole pages by
/// default, never sends "everything open", and always discloses when a
/// source had to be trimmed or dropped.
///
/// Nothing here is model-specific — limits are product policy, not
/// provider tuning.
/// </summary>
public sealed record AIContextLimits(
    /// <summary>Maximum number of tabs that may contribute context.</summary>
    int MaxTabs = 24,
    /// <summary>Maximum characters taken from any single tab.</summary>
    int MaxCharsPerTab = 1800,
    /// <summary>Maximum characters across the whole request.</summary>
    int MaxTotalChars = 12000,
    /// <summary>Maximum characters taken from an explicit text selection.</summary>
    int MaxSelectionChars = 4000,
    /// <summary>Maximum length of a user question.</summary>
    int MaxQuestionChars = 500)
{
    public static AIContextLimits Default { get; } = new();

    /// <summary>Smaller budget for a single-page action.</summary>
    public static AIContextLimits SinglePage { get; } = new(MaxTabs: 1, MaxCharsPerTab: 4000, MaxTotalChars: 4000);
}
