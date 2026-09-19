namespace Encomm.Browser.AI;

/// <summary>How a source contributed to the request.</summary>
public enum AIContextKind
{
    /// <summary>Tab metadata only (title/URL). No page content was read.</summary>
    TabMetadata = 0,
    /// <summary>A bounded readable excerpt of the page body.</summary>
    PageContent = 1,
    /// <summary>The user's own text selection on the page.</summary>
    Selection = 2
}

/// <summary>
/// One tab as seen by the context builder. The App layer fills this from
/// LOGICAL tab state plus — only when a renderer already exists or the
/// user explicitly asked about that tab — bounded page context.
/// </summary>
public sealed record TabContextInput(
    Guid TabId,
    string Title,
    string Url,
    bool IsActive,
    DateTimeOffset LastInteractionUtc,
    string? Description = null,
    string? BodyExcerpt = null,
    string? SelectedText = null,
    string? FaviconUrl = null)
{
    public bool HasContent =>
        !string.IsNullOrWhiteSpace(BodyExcerpt) || !string.IsNullOrWhiteSpace(SelectedText) ||
        !string.IsNullOrWhiteSpace(Description);
}

/// <summary>
/// A source that actually made it into the request. <see cref="Label"/>
/// ("S1", "S2", ...) is the citation handle the model must use; the host
/// maps labels back to real tabs, so every claim stays traceable.
/// </summary>
public sealed record ContextSource(
    string Label,
    Guid TabId,
    string Title,
    string Url,
    AIContextKind Kind,
    int CharsUsed,
    bool ContentOmitted);

/// <summary>The bounded payload actually handed to a model.</summary>
public sealed record BuiltContext(
    IReadOnlyList<ContextSource> Sources,
    string RenderedText,
    int TabsConsidered,
    int TabsOmitted)
{
    public bool IsEmpty => Sources.Count == 0;
    public bool Truncated => TabsOmitted > 0 || Sources.Any(s => s.ContentOmitted);
}

/// <summary>
/// Maps model-produced citation labels ("S1") back onto real tabs. This is
/// the mechanism that makes AI output auditable instead of opaque.
/// </summary>
public sealed class SourceLabelIndex
{
    private readonly Dictionary<string, ContextSource> _byLabel;

    private SourceLabelIndex(Dictionary<string, ContextSource> byLabel) => _byLabel = byLabel;

    public static SourceLabelIndex From(IReadOnlyList<ContextSource> sources)
    {
        var map = new Dictionary<string, ContextSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sources)
        {
            if (!string.IsNullOrWhiteSpace(s.Label)) map[s.Label.Trim()] = s;
        }
        return new SourceLabelIndex(map);
    }

    public static SourceLabelIndex Empty { get; } =
        new(new Dictionary<string, ContextSource>(StringComparer.OrdinalIgnoreCase));

    public bool TryResolve(string? label, out ContextSource source)
    {
        source = null!;
        if (string.IsNullOrWhiteSpace(label)) return false;
        var key = Normalize(label);
        if (key.Length == 0) return false;
        return _byLabel.TryGetValue(key, out source!);
    }

    /// <summary>Resolve a set of labels, dropping unknown ones and deduplicating.</summary>
    public IReadOnlyList<Guid> ResolveTabIds(IEnumerable<string>? labels)
    {
        if (labels is null) return Array.Empty<Guid>();
        var ids = new List<Guid>();
        foreach (var label in labels)
        {
            if (TryResolve(label, out var src) && !ids.Contains(src.TabId)) ids.Add(src.TabId);
        }
        return ids;
    }

    /// <summary>Resolve a set of labels to real sources, dropping unknown ones.</summary>
    public IReadOnlyList<ContextSource> ResolveSources(IEnumerable<string>? labels)
    {
        if (labels is null) return Array.Empty<ContextSource>();
        var list = new List<ContextSource>();
        foreach (var label in labels)
        {
            if (TryResolve(label, out var src) && !list.Any(s => s.Label == src.Label)) list.Add(src);
        }
        return list;
    }

    /// <summary>Accepts "S1", "[S1]", "s1" and "S1 extra text".</summary>
    private static string Normalize(string label)
    {
        var s = label.Trim().TrimStart('[').TrimEnd(']').Trim();
        var end = 0;
        while (end < s.Length && !char.IsWhiteSpace(s[end]) && s[end] != ',' && s[end] != ';') end++;
        return s[..end];
    }
}
