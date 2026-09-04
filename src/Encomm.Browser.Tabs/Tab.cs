using Encomm.Browser.Engine.Abstractions;

namespace Encomm.Browser.Tabs;

/// <summary>
/// Engine-facing lifecycle states. Tab domain distinguishes
/// "logical state" from "renderer state" — the renderer may be Live,
/// Warm, or Ghost while the logical state is Active / Background.
/// </summary>
public enum TabRendererState
{
    Live,
    Warm,
    Ghost
}

public enum TabLogicalState
{
    Active,
    Background
}

/// <summary>
/// Pure-domain tab model. No engine references, no XAML.
/// </summary>
public sealed class Tab
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string? FaviconUrl { get; set; }
    public TabRendererState RendererState { get; set; } = TabRendererState.Live;
    public TabLogicalState LogicalState { get; set; } = TabLogicalState.Background;
    public bool Pinned { get; set; }
    public bool Muted { get; set; }
    public bool KeepAwake { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastInteractionUtc { get; set; } = DateTimeOffset.UtcNow;
    public int OrderIndex { get; set; }
    public string? PreviewPath { get; set; }
    public bool IsProtected =>
        Pinned || KeepAwake || Muted;
}

/// <summary>Recently closed tab, persisted for Ctrl+Shift+T.</summary>
public sealed class RecentlyClosedTab
{
    public Guid OriginalTabId { get; init; } = Guid.NewGuid();
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public Guid WorkspaceId { get; set; }
    public DateTimeOffset ClosedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Convenience adapter between renderer state and engine state.</summary>
public static class RendererStateExtensions
{
    public static ViewLifecycleState ToEngineState(this TabRendererState s) => s switch
    {
        TabRendererState.Live => ViewLifecycleState.Live,
        TabRendererState.Warm => ViewLifecycleState.Warm,
        TabRendererState.Ghost => ViewLifecycleState.Ghost,
        _ => ViewLifecycleState.None
    };
}