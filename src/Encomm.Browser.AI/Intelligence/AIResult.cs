using System.Text;

namespace Encomm.Browser.AI;

/// <summary>Outcome of an ENCOMM AI action.</summary>
public enum AIResultStatus
{
    /// <summary>A model produced a usable answer.</summary>
    Ok = 0,
    /// <summary>ENCOMM AI has no provider configured. Browsing is unaffected.</summary>
    NotConfigured = 1,
    /// <summary>There was nothing legitimate to send (no eligible sources).</summary>
    NoSources = 2,
    /// <summary>The provider or transport failed. User context is preserved.</summary>
    Failed = 3
}

/// <summary>
/// One structured result entry. Claims are never free-floating: every
/// item carries the source handles and resolved tab ids it came from.
/// </summary>
public sealed record AIResultItem(
    string Label,
    string? Detail,
    IReadOnlyDictionary<string, string> Facts,
    IReadOnlyList<string> SourceLabels,
    IReadOnlyList<Guid> SourceTabIds)
{
    public static AIResultItem Text(string label, string? detail = null) =>
        new(label, detail, new Dictionary<string, string>(),
            Array.Empty<string>(), Array.Empty<Guid>());
}

/// <summary>
/// The single result shape the UI renders. Raw provider text is kept so
/// nothing is lost, but the product surface renders the structured form.
/// </summary>
public sealed record AIResult(
    string Title,
    AIResultStatus Status,
    string Summary,
    IReadOnlyList<AIResultItem> Items,
    IReadOnlyList<string> Uncertainties,
    IReadOnlyList<ContextSource> Sources,
    string RawText,
    bool TruncatedContext = false,
    string? Diagnostic = null)
{
    public bool HasSources => Sources.Count > 0;
    public bool IsUsable => Status == AIResultStatus.Ok;

    /// <summary>
    /// Everyday-Mode friendly wording. Developer Mode may additionally
    /// show <see cref="Diagnostic"/>; credentials never appear here.
    /// </summary>
    public const string NotConfiguredMessage =
        "ENCOMM AI is not configured.\nConfigure a provider in Settings to use page and workspace intelligence.";

    public static AIResult NotConfigured(string title) => new(
        title,
        AIResultStatus.NotConfigured,
        NotConfiguredMessage,
        Array.Empty<AIResultItem>(),
        Array.Empty<string>(),
        Array.Empty<ContextSource>(),
        string.Empty);

    public static AIResult NoSources(string title, string reason) => new(
        title,
        AIResultStatus.NoSources,
        reason,
        Array.Empty<AIResultItem>(),
        Array.Empty<string>(),
        Array.Empty<ContextSource>(),
        string.Empty);

    public static AIResult Failure(string title, string friendlyMessage, string? diagnostic = null) => new(
        title,
        AIResultStatus.Failed,
        friendlyMessage,
        Array.Empty<AIResultItem>(),
        Array.Empty<string>(),
        Array.Empty<ContextSource>(),
        string.Empty,
        Diagnostic: diagnostic);

    /// <summary>
    /// Plain-text projection used for copy-to-clipboard and logs. Contains
    /// the source list, so an exported answer stays traceable.
    /// </summary>
    public string ToPlainText()
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(Title)) sb.AppendLine(Title);
        if (!string.IsNullOrWhiteSpace(Summary)) sb.AppendLine(Summary);
        if (Items.Count > 0)
        {
            sb.AppendLine();
            foreach (var item in Items)
            {
                sb.Append("- ").Append(item.Label);
                if (!string.IsNullOrWhiteSpace(item.Detail)) sb.Append(": ").Append(item.Detail);
                if (item.Facts.Count > 0)
                {
                    sb.Append(" [");
                    sb.Append(string.Join(", ", item.Facts.Select(f => $"{f.Key}={f.Value}")));
                    sb.Append(']');
                }
                if (item.SourceLabels.Count > 0)
                    sb.Append(" (").Append(string.Join(", ", item.SourceLabels)).Append(')');
                sb.AppendLine();
            }
        }
        if (Uncertainties.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Uncertain:");
            foreach (var u in Uncertainties) sb.Append("- ").AppendLine(u);
        }
        if (Sources.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Sources:");
            foreach (var s in Sources)
                sb.Append("  ").Append(s.Label).Append(" — ").Append(s.Title).Append(" — ").AppendLine(s.Url);
            if (TruncatedContext)
                sb.AppendLine("  (some sources were trimmed to fit the context budget)");
        }
        return sb.ToString().TrimEnd();
    }
}