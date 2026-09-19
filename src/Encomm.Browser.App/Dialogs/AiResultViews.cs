using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;

namespace Encomm.Browser.App.Dialogs;

/// <summary>
/// One clickable citation chip. Resolved to a real tab id, so selecting a
/// source is an ordinary tab switch (normal Ghost restore rules apply).
/// </summary>
public sealed record AiSourceChip(string Label, string Title, string Url, Guid TabId)
{
    public string AccessibleName => $"Open source {Label}: {Title}";
}

/// <summary>Rendered shape of one structured AI result item.</summary>
public sealed record AiItemView(
    string Label,
    string DetailLine,
    string FactsLine,
    Visibility DetailVisibility,
    Visibility FactsVisibility,
    IReadOnlyList<AiSourceChip> SourceChips);

/// <summary>Rendered shape of one canvas row (evidence / summary mode).</summary>
public sealed record CanvasRowView(
    string Label,
    string Detail,
    Visibility DetailVisibility,
    IReadOnlyList<AiSourceChip> SourceChips);