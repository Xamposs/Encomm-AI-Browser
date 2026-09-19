namespace Encomm.Browser.AI;

using System.Text.Json.Serialization;

/// <summary>What shape of workspace ENCOMM generated for the user's intent.</summary>
public enum CanvasKind
{
    /// <summary>Entities compared across shared attributes (a table).</summary>
    Comparison = 0,
    /// <summary>A list of findings, each citing its source.</summary>
    Evidence = 1,
    /// <summary>A narrative summary with cited points.</summary>
    Summary = 2
}

/// <summary>One comparable attribute (a table column).</summary>
public sealed record CanvasColumn(string Key, string Label);

/// <summary>
/// One cell of a comparison row. Each cell carries its own citations, so a
/// single claim ("price = 999") can be traced to the exact source it came
/// from — not merely to "the workspace".
/// </summary>
public sealed record CanvasCell(
    string ColumnKey,
    string Text,
    IReadOnlyList<string> SourceLabels,
    IReadOnlyList<Guid> SourceTabIds);

/// <summary>One entity / finding in the canvas.</summary>
public sealed record CanvasRow(
    string Label,
    IReadOnlyList<CanvasCell> Cells,
    IReadOnlyList<string> SourceLabels,
    IReadOnlyList<Guid> SourceTabIds)
{
    /// <summary>Best text for the row when rendered outside a table.</summary>
    [JsonIgnore]
    public string? FirstText => Cells.Select(c => c.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
}

/// <summary>
/// A browser-generated workspace (ENCOMM Canvas).
///
/// This is deliberately NOT a chat answer. It is a task-specific native
/// surface: a comparison table or an evidence list whose every claim is
/// traceable to a real tab, stored per workspace so intent survives a
/// restart. Web sites remain the underlying sources and stay reachable.
/// </summary>
public sealed record AICanvas(
    Guid Id,
    Guid WorkspaceId,
    string Intent,
    CanvasKind Kind,
    string Title,
    string Summary,
    IReadOnlyList<CanvasColumn> Columns,
    IReadOnlyList<CanvasRow> Rows,
    IReadOnlyList<string> Uncertainties,
    IReadOnlyList<ContextSource> Sources,
    AIResultStatus Status,
    string Message,
    DateTimeOffset CreatedUtc,
    bool TruncatedContext = false)
{
    [JsonIgnore]
    public bool HasTable => Kind == CanvasKind.Comparison && Columns.Count > 0 && Rows.Count > 0;
    [JsonIgnore]
    public bool HasRows => Rows.Count > 0;
    [JsonIgnore]
    public bool HasSources => Sources.Count > 0;

    public static AICanvas NotConfigured(Guid workspaceId, string intent) => new(
        Guid.NewGuid(), workspaceId, intent, CanvasKind.Summary,
        "ENCOMM Canvas", AIResult.NotConfiguredMessage,
        Array.Empty<CanvasColumn>(), Array.Empty<CanvasRow>(),
        Array.Empty<string>(), Array.Empty<ContextSource>(),
        AIResultStatus.NotConfigured, AIResult.NotConfiguredMessage, DateTimeOffset.UtcNow);

    public static AICanvas NoSources(Guid workspaceId, string intent, string reason) => new(
        Guid.NewGuid(), workspaceId, intent, CanvasKind.Summary,
        "ENCOMM Canvas", reason,
        Array.Empty<CanvasColumn>(), Array.Empty<CanvasRow>(),
        Array.Empty<string>(), Array.Empty<ContextSource>(),
        AIResultStatus.NoSources, reason, DateTimeOffset.UtcNow);

    public static AICanvas Failure(Guid workspaceId, string intent, string message) => new(
        Guid.NewGuid(), workspaceId, intent, CanvasKind.Summary,
        "ENCOMM Canvas", message,
        Array.Empty<CanvasColumn>(), Array.Empty<CanvasRow>(),
        Array.Empty<string>(), Array.Empty<ContextSource>(),
        AIResultStatus.Failed, message, DateTimeOffset.UtcNow);

    /// <summary>Plain-text projection for copy/paste; keeps citations and sources.</summary>
    public string ToPlainText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Title);
        if (!string.IsNullOrWhiteSpace(Intent)) sb.AppendLine("Intent: " + Intent);
        if (!string.IsNullOrWhiteSpace(Summary)) sb.AppendLine(Summary);

        if (HasTable)
        {
            sb.AppendLine();
            sb.AppendLine(string.Join(" | ",
                new[] { "Item" }.Concat(Columns.Select(c => c.Label))));
            foreach (var row in Rows)
            {
                var cells = new List<string> { row.Label };
                foreach (var column in Columns)
                {
                    var cell = row.Cells.FirstOrDefault(c =>
                        string.Equals(c.ColumnKey, column.Key, StringComparison.OrdinalIgnoreCase));
                    cells.Add(cell is null ? "" : cell.Text + Citations(cell.SourceLabels));
                }
                sb.AppendLine(string.Join(" | ", cells));
            }
        }
        else if (Rows.Count > 0)
        {
            sb.AppendLine();
            foreach (var row in Rows)
            {
                sb.Append("- ").Append(row.Label);
                if (row.FirstText is { Length: > 0 } text && text != row.Label)
                    sb.Append(": ").Append(text);
                sb.Append(Citations(row.SourceLabels));
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

    private static string Citations(IReadOnlyList<string> labels) =>
        labels.Count == 0 ? "" : " (" + string.Join(", ", labels) + ")";
}