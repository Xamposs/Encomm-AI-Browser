using System.Text.Json;

namespace Encomm.Browser.AI;

/// <summary>
/// Parses a model response into an <see cref="AICanvas"/>.
///
/// The canvas contract asks models for a table
/// (<c>columns</c> + <c>rows[].cells[]</c>). Because models are
/// unreliable, the parser degrades gracefully instead of failing:
///
/// * explicit table                 -> Comparison canvas
/// * rows with facts but no columns -> columns synthesised from the facts
/// * rows with plain text only      -> Evidence canvas
/// * summary only                   -> Summary canvas
/// * anything unparseable           -> the raw text becomes the summary
///
/// Citations are resolved against the real context index; unknown labels
/// are dropped so a canvas can never cite a source that does not exist.
/// </summary>
public static class CanvasParser
{
    private const int MaxSynthesisedColumns = 6;

    public static AICanvas Parse(
        string? raw,
        Guid workspaceId,
        string intent,
        string fallbackTitle,
        SourceLabelIndex? index = null,
        IReadOnlyList<ContextSource>? sources = null,
        bool truncatedContext = false,
        AIResultStatus status = AIResultStatus.Ok)
    {
        index ??= SourceLabelIndex.Empty;
        var sent = sources ?? Array.Empty<ContextSource>();
        var text = raw?.Trim() ?? string.Empty;
        var title = string.IsNullOrWhiteSpace(fallbackTitle) ? "ENCOMM Canvas" : fallbackTitle;

        if (text.Length == 0)
        {
            return Build(workspaceId, intent, CanvasKind.Summary, title,
                "(no response from the model)", Array.Empty<CanvasColumn>(), Array.Empty<CanvasRow>(),
                Array.Empty<string>(), sent, status, "(no response from the model)", truncatedContext);
        }

        var json = StructuredResultParser.ExtractJsonObject(text);
        if (json is not null && TryParse(json, workspaceId, intent, title, index, sent,
                truncatedContext, status, out var canvas))
            return canvas!;

        // Unstructured: keep it, as a Summary canvas.
        return Build(workspaceId, intent, CanvasKind.Summary, title, text,
            Array.Empty<CanvasColumn>(), Array.Empty<CanvasRow>(), Array.Empty<string>(), sent,
            status, text, truncatedContext);
    }

    private static AICanvas Build(
        Guid workspaceId, string intent, CanvasKind kind, string title, string summary,
        IReadOnlyList<CanvasColumn> columns, IReadOnlyList<CanvasRow> rows,
        IReadOnlyList<string> uncertainties, IReadOnlyList<ContextSource> sources,
        AIResultStatus status, string message, bool truncatedContext) =>
        new(Guid.NewGuid(), workspaceId, intent, kind, title, summary, columns, rows,
            uncertainties, sources, status, message, DateTimeOffset.UtcNow, truncatedContext);

    private static bool TryParse(
        string json,
        Guid workspaceId,
        string intent,
        string fallbackTitle,
        SourceLabelIndex index,
        IReadOnlyList<ContextSource> sources,
        bool truncatedContext,
        AIResultStatus status,
        out AICanvas? canvas)
    {
        canvas = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var root = doc.RootElement;

            var title = GetString(root, "title") ?? fallbackTitle;
            var summary = GetString(root, "summary") ?? GetString(root, "answer") ?? string.Empty;
            var columns = ParseColumns(root);
            var rows = ParseRows(root, index);
            var uncertainties = ParseStringArray(root, "uncertainties", "uncertainty");

            if (columns.Count == 0) columns = SynthesiseColumns(rows);
            var kind = ParseKind(root, columns, rows);

            canvas = Build(workspaceId, intent, kind, title, summary, columns, rows, uncertainties,
                sources, status, string.IsNullOrWhiteSpace(summary) ? title : summary, truncatedContext);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static CanvasKind ParseKind(JsonElement root, IReadOnlyList<CanvasColumn> columns,
        IReadOnlyList<CanvasRow> rows)
    {
        var raw = GetString(root, "kind");
        if (raw is not null)
        {
            if (raw.Contains("compar", StringComparison.OrdinalIgnoreCase)) return CanvasKind.Comparison;
            if (raw.Contains("evid", StringComparison.OrdinalIgnoreCase)) return CanvasKind.Evidence;
            if (raw.Contains("summ", StringComparison.OrdinalIgnoreCase)) return CanvasKind.Summary;
        }
        if (columns.Count > 0 && rows.Any(r => r.Cells.Count > 0)) return CanvasKind.Comparison;
        return rows.Count > 0 ? CanvasKind.Evidence : CanvasKind.Summary;
    }

    private static IReadOnlyList<CanvasColumn> ParseColumns(JsonElement root)
    {
        if (!root.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array)
            return Array.Empty<CanvasColumn>();

        var list = new List<CanvasColumn>();
        foreach (var entry in columns.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var label = entry.GetString();
                if (!string.IsNullOrWhiteSpace(label))
                    list.Add(new CanvasColumn(ColumnKey(label!), label!));
                continue;
            }
            if (entry.ValueKind != JsonValueKind.Object) continue;

            var labelText = GetString(entry, "label") ?? GetString(entry, "name") ?? GetString(entry, "key");
            if (string.IsNullOrWhiteSpace(labelText)) continue;
            var key = GetString(entry, "key") ?? labelText!;
            list.Add(new CanvasColumn(ColumnKey(key), labelText!));
        }
        return list;
    }

    private static IReadOnlyList<CanvasRow> ParseRows(JsonElement root, SourceLabelIndex index)
    {
        if (!root.TryGetProperty("rows", out var rows) &&
            !root.TryGetProperty("items", out rows) &&
            !root.TryGetProperty("results", out rows))
            return Array.Empty<CanvasRow>();

        if (rows.ValueKind != JsonValueKind.Array) return Array.Empty<CanvasRow>();

        var list = new List<CanvasRow>();
        foreach (var entry in rows.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var only = entry.GetString();
                if (!string.IsNullOrWhiteSpace(only))
                    list.Add(new CanvasRow(only!, Array.Empty<CanvasCell>(),
                        Array.Empty<string>(), Array.Empty<Guid>()));
                continue;
            }
            if (entry.ValueKind != JsonValueKind.Object) continue;

            var label = GetString(entry, "label") ?? GetString(entry, "name")
                        ?? GetString(entry, "title") ?? "Item";
            var rowLabels = Resolve(index, ParseStringArray(entry, "sourceLabels", "sources", "citations"));
            var cells = ParseCells(entry, index, rowLabels);

            list.Add(new CanvasRow(label, cells, rowLabels, index.ResolveTabIds(rowLabels)));
        }
        return list;
    }

    private static IReadOnlyList<CanvasCell> ParseCells(
        JsonElement entry, SourceLabelIndex index, IReadOnlyList<string> rowLabels)
    {
        var cells = new List<CanvasCell>();

        if (entry.TryGetProperty("cells", out var raw) && raw.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in raw.EnumerateArray())
            {
                if (c.ValueKind == JsonValueKind.String)
                {
                    var only = c.GetString();
                    if (!string.IsNullOrWhiteSpace(only))
                        cells.Add(new CanvasCell(string.Empty, only!,
                            Array.Empty<string>(), Array.Empty<Guid>()));
                    continue;
                }
                if (c.ValueKind != JsonValueKind.Object) continue;

                var text = GetString(c, "text") ?? GetString(c, "value") ?? GetString(c, "detail");
                if (string.IsNullOrWhiteSpace(text)) continue;
                var column = GetString(c, "column") ?? GetString(c, "key") ?? string.Empty;
                var labels = Resolve(index, ParseStringArray(c, "sourceLabels", "sources", "citations"));
                if (labels.Count == 0) labels = rowLabels;
                cells.Add(new CanvasCell(ColumnKey(column), text!, labels, index.ResolveTabIds(labels)));
            }
        }

        // Facts without explicit cells still become comparable attributes.
        if (cells.Count == 0 && entry.TryGetProperty("facts", out var facts) &&
            facts.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in facts.EnumerateObject())
            {
                var value = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : prop.Value.ToString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                cells.Add(new CanvasCell(ColumnKey(prop.Name), value, rowLabels,
                    index.ResolveTabIds(rowLabels)));
            }
        }

        // A plain-language row is still useful as a single detail column.
        if (cells.Count == 0)
        {
            var detail = GetString(entry, "detail") ?? GetString(entry, "description")
                         ?? GetString(entry, "text") ?? GetString(entry, "value");
            if (!string.IsNullOrWhiteSpace(detail))
                cells.Add(new CanvasCell(string.Empty, detail!, rowLabels, index.ResolveTabIds(rowLabels)));
        }

        return cells;
    }

    /// <summary>
    /// When the model returned structured facts but no column list (e.g. it
    /// answered with the simpler item shape), build columns from the union
    /// of fact keys so the user still gets a comparable table.
    /// </summary>
    private static IReadOnlyList<CanvasColumn> SynthesiseColumns(IReadOnlyList<CanvasRow> rows)
    {
        if (rows.Count < 2) return Array.Empty<CanvasColumn>();
        var keys = new List<string>();
        foreach (var row in rows)
        {
            foreach (var cell in row.Cells)
            {
                if (cell.ColumnKey.Length == 0 || keys.Contains(cell.ColumnKey)) continue;
                keys.Add(cell.ColumnKey);
                if (keys.Count >= MaxSynthesisedColumns) break;
            }
            if (keys.Count >= MaxSynthesisedColumns) break;
        }
        if (keys.Count == 0) return Array.Empty<CanvasColumn>();

        // Only claim a table when the attributes actually differ between
        // rows; otherwise an evidence list is the honest presentation.
        var comparable = keys.Any(key => rows
            .Select(r => r.Cells.FirstOrDefault(c =>
                string.Equals(c.ColumnKey, key, StringComparison.OrdinalIgnoreCase))?.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() > 1);
        if (!comparable) return Array.Empty<CanvasColumn>();

        return keys.Select(k => new CanvasColumn(k, DisplayLabel(k))).ToList();
    }

    private static string DisplayLabel(string key)
    {
        if (key.Length == 0) return key;
        var spaced = System.Text.RegularExpressions.Regex.Replace(key, "(?<!^)([A-Z])", " $1");
        spaced = spaced.Replace('_', ' ').Replace('-', ' ').Trim();
        return spaced.Length == 0 ? key : char.ToUpper(spaced[0]) + spaced[1..];
    }

    /// <summary>Canonical column key: case-insensitive, whitespace-free.</summary>
    public static string ColumnKey(string? column) =>
        string.IsNullOrWhiteSpace(column)
            ? string.Empty
            : column.Trim().Replace(" ", string.Empty).ToLowerInvariant();

    private static IReadOnlyList<string> Resolve(SourceLabelIndex index, IReadOnlyList<string> labels)
    {
        var resolved = new List<string>();
        foreach (var label in labels)
        {
            if (index.TryResolve(label, out var src) && !resolved.Contains(src.Label))
                resolved.Add(src.Label);
        }
        return resolved;
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String)
            {
                var single = value.GetString();
                return string.IsNullOrWhiteSpace(single) ? Array.Empty<string>() : new[] { single! };
            }
            if (value.ValueKind != JsonValueKind.Array) continue;

            var list = new List<string>();
            foreach (var entry in value.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String) continue;
                var s = entry.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
            }
            return list;
        }
        return Array.Empty<string>();
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;
        var s = value.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }
}