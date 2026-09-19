using System.Text.Json;

namespace Encomm.Browser.AI;

/// <summary>
/// Turns raw model output into the structured <see cref="AIResult"/> the
/// product actually renders. Models are unreliable, so this parser is
/// deliberately forgiving and NEVER throws:
///
/// * plain JSON object
/// * ```json fenced JSON
/// * prose with an embedded JSON object
/// * anything else → the text is kept as the summary
///
/// Citation labels are resolved against the real context index; unknown
/// labels are dropped rather than invented, so a model can never cite a
/// source that does not exist.
/// </summary>
public static class StructuredResultParser
{
    public static AIResult Parse(
        string? raw,
        string title,
        SourceLabelIndex? index = null,
        bool truncatedContext = false,
        IReadOnlyList<ContextSource>? sources = null)
    {
        index ??= SourceLabelIndex.Empty;
        var sent = sources ?? Array.Empty<ContextSource>();
        var text = raw?.Trim() ?? string.Empty;

        if (text.Length == 0)
        {
            return new AIResult(title, AIResultStatus.Ok, "(no response from the model)",
                Array.Empty<AIResultItem>(), Array.Empty<string>(), sent, string.Empty, truncatedContext);
        }

        var json = ExtractJsonObject(text);
        if (json is not null && TryParseJson(json, title, index, truncatedContext, sent, out var parsed))
            return parsed!;

        // Unstructured but non-empty: still usable, still traceable.
        return new AIResult(title, AIResultStatus.Ok, text,
            Array.Empty<AIResultItem>(), Array.Empty<string>(), sent, text, truncatedContext);
    }

    private static bool TryParseJson(
        string json,
        string title,
        SourceLabelIndex index,
        bool truncatedContext,
        IReadOnlyList<ContextSource> sources,
        out AIResult? result)
    {
        result = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var root = doc.RootElement;

            var resultTitle = GetString(root, "title") ?? title;
            var summary = GetString(root, "summary") ?? GetString(root, "answer") ?? string.Empty;
            var items = ParseItems(root, index);
            var uncertainties = ParseStringArray(root, "uncertainties", "uncertainty");

            result = new AIResult(resultTitle, AIResultStatus.Ok, summary, items,
                uncertainties, sources, json, truncatedContext);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<AIResultItem> ParseItems(JsonElement root, SourceLabelIndex index)
    {
        if (!root.TryGetProperty("items", out var items) &&
            !root.TryGetProperty("results", out items) &&
            !root.TryGetProperty("comparison", out items))
            return Array.Empty<AIResultItem>();

        if (items.ValueKind != JsonValueKind.Array) return Array.Empty<AIResultItem>();

        var list = new List<AIResultItem>();
        foreach (var element in items.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                list.Add(AIResultItem.Text(element.GetString() ?? string.Empty));
                continue;
            }
            if (element.ValueKind != JsonValueKind.Object) continue;

            var label = GetString(element, "label") ?? GetString(element, "name")
                        ?? GetString(element, "title") ?? "Item";
            var detail = GetString(element, "detail") ?? GetString(element, "description")
                         ?? GetString(element, "value") ?? GetString(element, "text");

            var facts = ParseFacts(element);
            var labels = ParseStringArray(element, "sourceLabels", "sources", "citations");
            var resolvedLabels = new List<string>();
            foreach (var l in labels)
            {
                if (index.TryResolve(l, out var src) && !resolvedLabels.Contains(src.Label))
                    resolvedLabels.Add(src.Label);
            }

            list.Add(new AIResultItem(label, detail, facts, resolvedLabels,
                index.ResolveTabIds(resolvedLabels)));
        }
        return list;
    }

    private static IReadOnlyDictionary<string, string> ParseFacts(JsonElement element)
    {
        if (!element.TryGetProperty("facts", out var facts) || facts.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>();

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in facts.EnumerateObject())
        {
            var value = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                JsonValueKind.Null => string.Empty,
                _ => prop.Value.ToString()
            };
            if (!string.IsNullOrWhiteSpace(value)) dict[prop.Name] = value;
        }
        return dict;
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

    /// <summary>
    /// Finds the first balanced JSON object in arbitrary text, skipping
    /// braces inside string literals and honouring backslash escapes.
    /// Returns null when the text contains no complete object.
    /// </summary>
    public static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return text.Substring(start, i - start + 1);
                    break;
            }
        }
        return null;
    }
}