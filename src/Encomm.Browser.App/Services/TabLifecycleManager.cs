using Microsoft.Extensions.Logging;
using Encomm.Browser.Engine.Abstractions;
using Encomm.Browser.App.Services;
using Encomm.Browser.Core.Storage;

namespace Encomm.Browser.App.Services;

/// <summary>
/// Smart tab lifecycle manager. Implements deterministic, configuration-
/// driven lifecycle demotion rules. MUST NOT call any LLM.
///
/// Coordinates with `BrowserRuntime` to perform the actual renderer
/// operations (Suspend → Warm, Ghost → destroy). NEVER just flips an enum
/// flag — every transition is paired with a real renderer call, and the
/// outcome of the renderer call determines whether the logical state is
/// updated.
/// </summary>
public sealed class TabLifecycleManager
{
    private readonly TabService _tabs;
    private readonly BrowserRuntime _runtime;
    private readonly ILogger<TabLifecycleManager> _log;

    public TimeSpan WarmAfter { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan GhostAfter { get; set; } = TimeSpan.FromMinutes(20);
    public string Preset { get; set; } = "Balanced";
    public bool MemorySaverEnabled { get; set; } = true;
    private DateTimeOffset _lastTickUtc = DateTimeOffset.MinValue;

    public TabLifecycleManager(TabService tabs, BrowserRuntime runtime, ILogger<TabLifecycleManager> log)
    {
        _tabs = tabs;
        _runtime = runtime;
        _log = log;
    }

    public void Configure(string preset, bool memorySaver)
    {
        Preset = preset;
        MemorySaverEnabled = memorySaver;
        switch (preset)
        {
            case "Aggressive":
                WarmAfter = TimeSpan.FromMinutes(2);
                GhostAfter = TimeSpan.FromMinutes(8);
                break;
            case "NeverSleep":
                WarmAfter = TimeSpan.FromHours(1);
                GhostAfter = TimeSpan.FromHours(4);
                break;
            default:
                WarmAfter = TimeSpan.FromMinutes(5);
                GhostAfter = TimeSpan.FromMinutes(20);
                break;
        }
    }

    public DateTimeOffset LastTickUtc => _lastTickUtc;

    /// <summary>
    /// Run a single lifecycle pass. Each candidate is processed individually
    /// so a renderer failure on one tab doesn't poison the rest.
    /// </summary>
    public async Task TickAsync()
    {
        _lastTickUtc = DateTimeOffset.UtcNow;
        if (!MemorySaverEnabled) return;
        if (!_runtime.IsReady) return;

        var active = _tabs.ActiveTab?.Id;
        var now = DateTimeOffset.UtcNow;
        // Snapshot to avoid mutation during iteration.
        var snapshot = _tabs.TabsInCurrentWorkspace().ToList();
        foreach (var t in snapshot)
        {
            try
            {
                await ProcessOneAsync(t, active, now).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Lifecycle Tick failed for tab {Id}", t.Id);
            }
        }
    }

    /// <summary>Synchronous wrapper for legacy callers. Internally awaits.</summary>
    public void Tick()
    {
        _ = TickAsync();
    }

    private async Task ProcessOneAsync(TabRecord t, Guid? activeId, DateTimeOffset now)
    {
        if (t.Id == activeId) return;
        if (t.Pinned) return;
        if (t.Muted) return;
        if (t.KeepAwake) return;
        if (t.LogicalState == TabLogicalStateKind.Active) return;

        var idle = now - t.LastInteractionUtc;
        var shouldWarm = idle >= WarmAfter;
        var shouldGhost = idle >= GhostAfter;

        // Suspend: Live -> Warm
        if (shouldWarm && t.RendererState == TabRendererStateKind.Live)
        {
            if (!_runtime.HasView(t.Id)) return; // no view to suspend
            var ok = await _runtime.SuspendAsync(t.Id).ConfigureAwait(false);
            if (ok)
            {
                _tabs.SetRendererState(t, TabRendererStateKind.Warm);
                _log.LogInformation("Tab {Id} → Warm (idle {Minutes}m)", t.Id, idle.TotalMinutes);
            }
            else
            {
                _log.LogWarning("Tab {Id} suspend reported failure; logical state unchanged.", t.Id);
            }
            return;
        }

        // Ghost: any non-Ghost with sufficient idle → destroy renderer
        if (shouldGhost && t.RendererState != TabRendererStateKind.Ghost)
        {
            var removed = await _runtime.GhostAsync(t.Id).ConfigureAwait(false);
            if (removed || !_runtime.HasView(t.Id))
            {
                _tabs.SetRendererState(t, TabRendererStateKind.Ghost);
                _log.LogInformation("Tab {Id} → Ghost (idle {Minutes}m, renderer={Removed})",
                    t.Id, idle.TotalMinutes, removed);
            }
        }
    }

    public async Task GhostAllInWorkspaceAsync()
    {
        var snapshot = _tabs.TabsInCurrentWorkspace().ToList();
        foreach (var t in snapshot)
        {
            if (t.Pinned || t.KeepAwake) continue;
            if (t.RendererState == TabRendererStateKind.Ghost) continue;
            await _runtime.GhostAsync(t.Id).ConfigureAwait(false);
            _tabs.SetRendererState(t, TabRendererStateKind.Ghost);
        }
    }

    public void GhostNow(TabRecord tab)
    {
        if (tab is null) return;
        if (tab.Pinned || tab.KeepAwake) return;
        _ = GhostOneAsync(tab);
    }

    public void SleepNow(TabRecord tab)
    {
        if (tab is null) return;
        if (tab.Pinned || tab.KeepAwake) return;
        _ = SleepOneAsync(tab);
    }

    private async Task GhostOneAsync(TabRecord tab)
    {
        await _runtime.GhostAsync(tab.Id).ConfigureAwait(false);
        _tabs.SetRendererState(tab, TabRendererStateKind.Ghost);
    }

    private async Task SleepOneAsync(TabRecord tab)
    {
        var ok = await _runtime.SuspendAsync(tab.Id).ConfigureAwait(false);
        if (ok) _tabs.SetRendererState(tab, TabRendererStateKind.Warm);
    }
}