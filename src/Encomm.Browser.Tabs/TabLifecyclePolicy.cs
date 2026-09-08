using Encomm.Browser.Core.Storage;

namespace Encomm.Browser.Tabs;

/// <summary>
/// Pure, deterministic tab-lifecycle policy. No renderer access, no I/O,
/// no LLM. Decides WHAT should happen to a tab; `TabLifecycleManager`
/// (App layer) performs the renderer operations.
///
/// Extracted here so the policy is unit-testable without WinUI.
/// </summary>
public static class TabLifecyclePolicy
{
    public enum Decision
    {
        None,
        Warm,
        Ghost
    }

    /// <summary>
    /// Decide the lifecycle transition for one background tab.
    /// </summary>
    /// <param name="tab">The logical tab record.</param>
    /// <param name="activeTabId">Currently active tab (never transitioned).</param>
    /// <param name="now">Reference time (injectable for tests).</param>
    /// <param name="warmAfter">Idle time before Warm.</param>
    /// <param name="ghostAfter">Idle time before Ghost.</param>
    /// <param name="hasUnsavedForm">True when the page reports unsaved form state.</param>
    /// <param name="memoryPressured">True when the process tree is over budget (Adaptive halves Ghost).</param>
    /// <param name="halveGhostWhenPressured">Whether the Adaptive rule applies.</param>
    public static Decision Decide(
        TabRecord tab,
        Guid? activeTabId,
        DateTimeOffset now,
        TimeSpan warmAfter,
        TimeSpan ghostAfter,
        bool hasUnsavedForm,
        bool memoryPressured = false,
        bool halveGhostWhenPressured = false)
    {
        if (tab.Id == activeTabId) return Decision.None;
        if (tab.Pinned) return Decision.None;
        if (tab.KeepAwake) return Decision.None;
        if (tab.LogicalState == TabLogicalStateKind.Active) return Decision.None;

        var idle = now - tab.LastInteractionUtc;
        var effectiveGhost = (halveGhostWhenPressured && memoryPressured)
            ? TimeSpan.FromTicks(ghostAfter.Ticks / 2)
            : ghostAfter;

        if (tab.RendererState == TabRendererStateKind.Live && idle >= warmAfter)
            return Decision.Warm;

        if (tab.RendererState != TabRendererStateKind.Ghost && idle >= effectiveGhost)
        {
            // Automatic Ghost obeys form-state protection. Manual Ghost
            // (TabLifecycleManager.GhostNow) bypasses this gate by design.
            if (hasUnsavedForm) return Decision.None;
            return Decision.Ghost;
        }

        return Decision.None;
    }

    /// <summary>
    /// Whether a manual (user-initiated) Ghost/Sleep is allowed. Manual
    /// actions bypass KeepAwake/Pinned protection; only the active tab is
    /// refused (ghosting it would blank the visible page).
    /// </summary>
    public static bool AllowManualTransition(TabRecord tab, Guid? activeTabId)
    {
        if (tab is null) return false;
        if (tab.Id == activeTabId) return false;
        return true;
    }
}
