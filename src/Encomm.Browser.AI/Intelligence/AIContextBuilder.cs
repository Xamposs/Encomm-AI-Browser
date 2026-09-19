using System.Text;

namespace Encomm.Browser.AI;

/// <summary>
/// Builds the bounded, ranked, citable context for an ENCOMM AI action.
///
/// Rules (product policy, not provider tuning):
/// * Native ENCOMM surfaces (encomm://) are never sent to a model.
/// * Plain about:blank / empty tabs are never sent.
/// * The active tab is ranked first, then most recently used.
/// * Only <see cref="AIContextLimits.MaxTabs"/> tabs may contribute.
/// * Every source is truncated to the per-tab budget and the whole
///   request to the total budget; omissions are reported, never hidden.
/// * Tabs without available content still contribute metadata, but are
///   explicitly labelled "metadata only" so a model cannot pretend it
///   read a page it never saw.
///
/// This type is pure and renderer-agnostic: it never touches a WebView.
/// </summary>
public static class AIContextBuilder
{
    public static BuiltContext Build(
        IReadOnlyList<TabContextInput>? tabs,
        AIContextLimits? limits = null)
    {
        limits ??= AIContextLimits.Default;
        var eligible = Rank(tabs);
        var considered = eligible.Count;

        var selected = eligible.Take(Math.Max(1, limits.MaxTabs)).ToList();
        var omitted = considered - selected.Count;

        var sources = new List<ContextSource>(selected.Count);
        var sb = new StringBuilder();
        var totalChars = 0;
        var labelNo = 0;

        foreach (var tab in selected)
        {
            var remaining = limits.MaxTotalChars - totalChars;
            if (remaining <= 0)
            {
                // Total budget exhausted: the tab is dropped and counted,
                // so the result can disclose it honestly.
                omitted++;
                continue;
            }

            var kind = KindOf(tab);
            var raw = BestText(tab, limits);
            var contentOmitted = false;
            string? body = null;

            if (raw is not null)
            {
                var budget = Math.Min(limits.MaxCharsPerTab, remaining);
                if (raw.Length > budget)
                {
                    raw = raw[..Math.Max(0, budget)];
                    contentOmitted = true;
                }
                if (raw.Length > 0)
                {
                    body = raw;
                    totalChars += raw.Length;
                }
            }

            labelNo++;
            var label = "S" + labelNo.ToString();
            sources.Add(new ContextSource(
                label,
                tab.TabId,
                Safe(tab.Title),
                Safe(tab.Url),
                kind,
                body?.Length ?? 0,
                contentOmitted));

            sb.Append('[').Append(label).Append("] ").Append(Safe(tab.Title));
            if (!string.IsNullOrWhiteSpace(tab.Url)) sb.Append(" — ").Append(tab.Url);
            if (tab.IsActive) sb.Append("  (active tab)");
            sb.AppendLine();

            if (body is not null) sb.AppendLine(body);
            else sb.AppendLine("(metadata only — page content was not read)");
        }

        return new BuiltContext(sources, sb.ToString().TrimEnd(), considered, omitted);
    }

    /// <summary>
    /// Active tab first, then most recently interacted. Stable and
    /// deterministic: no model, no heuristics that change run to run.
    /// </summary>
    private static List<TabContextInput> Rank(IReadOnlyList<TabContextInput>? tabs)
    {
        if (tabs is null) return new List<TabContextInput>();
        return tabs
            .Where(IsEligible)
            .OrderByDescending(t => t.IsActive)
            .ThenByDescending(t => t.LastInteractionUtc)
            .ToList();
    }

    /// <summary>
    /// A tab may contribute only if it points at real web content. Native
    /// ENCOMM surfaces and blank tabs are local UI, not Web sources.
    /// </summary>
    public static bool IsEligible(TabContextInput? tab)
    {
        if (tab is null) return false;
        var url = tab.Url?.Trim() ?? string.Empty;
        if (url.Length == 0) return false;
        if (url.StartsWith("encomm://", StringComparison.OrdinalIgnoreCase)) return false;
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return false;
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static AIContextKind KindOf(TabContextInput tab)
    {
        if (!string.IsNullOrWhiteSpace(tab.SelectedText)) return AIContextKind.Selection;
        if (tab.HasContent) return AIContextKind.PageContent;
        return AIContextKind.TabMetadata;
    }

    /// <summary>
    /// The single best bounded text for a tab. A user selection is the
    /// most intentional signal, so it outranks the body excerpt.
    /// </summary>
    private static string? BestText(TabContextInput tab, AIContextLimits limits)
    {
        if (!string.IsNullOrWhiteSpace(tab.SelectedText))
            return Clip(tab.SelectedText!.Trim(), limits.MaxSelectionChars);
        if (!string.IsNullOrWhiteSpace(tab.BodyExcerpt))
            return tab.BodyExcerpt!.Trim();
        if (!string.IsNullOrWhiteSpace(tab.Description))
            return tab.Description!.Trim();
        return null;
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..Math.Max(0, max)];

    /// <summary>Collapse newlines/tabs so a title or URL can never forge a source header.</summary>
    private static string Safe(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "(untitled)";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(char.IsControl(c) ? ' ' : c);
        return sb.ToString().Trim();
    }
}