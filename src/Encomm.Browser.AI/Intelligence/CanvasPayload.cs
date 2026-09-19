using System.Text.Json;
using System.Text.Json.Serialization;

namespace Encomm.Browser.AI;

/// <summary>
/// Serialises a canvas to/from a single JSON payload.
///
/// Storage (SQLite) keeps only opaque text plus a few queryable scalars, so
/// the canvas shape can evolve without a schema migration. The AI project
/// owns this contract; the storage project never interprets it.
///
/// Deserialisation is defensive: a payload written by a future or older
/// build must never crash startup, so failures return null.
/// </summary>
public static class CanvasPayload
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(AICanvas canvas) =>
        JsonSerializer.Serialize(canvas, Options);

    public static AICanvas? Deserialize(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            return JsonSerializer.Deserialize<AICanvas>(payload, Options);
        }
        catch
        {
            return null;
        }
    }
}