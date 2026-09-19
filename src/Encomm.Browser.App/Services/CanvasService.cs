using Microsoft.Extensions.Logging;
using Encomm.Browser.AI;
using Encomm.Browser.Core.Storage;

namespace Encomm.Browser.App.Services;

/// <summary>
/// Owns ENCOMM Canvas lifecycle for the product: generation (delegated to
/// <see cref="AIService"/>, which owns the bounded-context and renderer
/// policy), persistence per workspace, listing, and deletion.
///
/// Division of responsibility:
/// * <see cref="AIService"/> decides WHAT may be sent and how it is parsed.
/// * <c>CanvasService</c> decides what the workspace REMEMBERS.
///
/// Only usable canvases are stored. "Not configured", "no sources" and
/// failure results are transient UX states, not workspace content, and
/// storing them would pollute the workspace with empty canvases.
/// </summary>
public sealed class CanvasService
{
    private readonly BrowserPersistenceService _persistence;
    private readonly AIService _ai;
    private readonly ILogger<CanvasService> _log;

    public CanvasService(BrowserPersistenceService persistence, AIService ai, ILogger<CanvasService> log)
    {
        _persistence = persistence;
        _ai = ai;
        _log = log;
    }

    public bool IsConfigured => _ai.IsConfigured;

    /// <summary>Generate a canvas for an intent and persist it when usable.</summary>
    public async Task<AICanvas> GenerateAsync(
        Guid workspaceId, string intent, TabRecord? activeTab, IReadOnlyList<TabRecord> workspaceTabs)
    {
        var canvas = await _ai.GenerateCanvasAsync(workspaceId, intent, activeTab, workspaceTabs);
        if (canvas.Status == AIResultStatus.Ok)
            Save(canvas);
        return canvas;
    }

    /// <summary>The most recent stored canvas for a workspace, or null.</summary>
    public AICanvas? LoadLatest(Guid workspaceId)
    {
        try
        {
            var record = _persistence.LoadLatestCanvas(workspaceId);
            return record is null ? null : FromRecord(record);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not load canvas for workspace {Workspace}", workspaceId);
            return null;
        }
    }

    /// <summary>Newest stored canvases for a workspace (defensive: skips unreadable payloads).</summary>
    public IReadOnlyList<AICanvas> List(Guid workspaceId, int limit = 20)
    {
        var list = new List<AICanvas>();
        try
        {
            foreach (var record in _persistence.LoadCanvases(workspaceId, limit))
            {
                var canvas = FromRecord(record);
                if (canvas is not null) list.Add(canvas);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not list canvases for workspace {Workspace}", workspaceId);
        }
        return list;
    }

    public void Save(AICanvas canvas)
    {
        try
        {
            _persistence.SaveCanvas(ToRecord(canvas));
        }
        catch (Exception ex)
        {
            // Persistence failure must never lose the canvas the user is
            // looking at; it simply will not survive a restart.
            _log.LogWarning(ex, "Could not persist canvas {Id}", canvas.Id);
        }
    }

    public void Delete(Guid canvasId)
    {
        try { _persistence.DeleteCanvas(canvasId); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not delete canvas {Id}", canvasId); }
    }

    /// <summary>
    /// Intents the user can act on, so an empty canvas surface is never a
    /// dead end. Everyday-Mode wording only.
    /// </summary>
    public static IReadOnlyList<string> SuggestedIntents { get; } = new[]
    {
        "Compare these pages side by side",
        "Build an evidence list from these sources",
        "Collect the key facts into one table",
        "Find the differences between these pages"
    };

    private static CanvasRecord ToRecord(AICanvas canvas) => new(
        canvas.Id, canvas.WorkspaceId, canvas.Title, canvas.Intent,
        (int)canvas.Kind, (int)canvas.Status,
        CanvasPayload.Serialize(canvas),
        canvas.CreatedUtc, DateTimeOffset.UtcNow);

    private static AICanvas? FromRecord(CanvasRecord record) => CanvasPayload.Deserialize(record.PayloadJson);
}