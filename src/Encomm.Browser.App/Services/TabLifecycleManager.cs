using Microsoft.Extensions.Logging;
using Encomm.Browser.Engine.Abstractions;
using Encomm.Browser.App.Services;
using Encomm.Browser.Core.Storage;

namespace Encomm.Browser.App.Services;

/// <summary>
/// Smart tab lifecycle manager. Implements deterministic, configuration-
/// driven lifecycle demotion rules. MUST NOT call any LLM.
/// </summary>
public sealed class TabLifecycleManager
{
    private readonly TabService _tabs;
    private readonly IBrowserEngine _engine;
    private readonly ILogger<TabLifecycleManager> _log;

    public TimeSpan WarmAfter { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan GhostAfter { get; set; } = TimeSpan.FromMinutes(20);
    public string Preset { get; set; } = "Balanced";
    public bool MemorySaverEnabled { get; set; } = true;

    public TabLifecycleManager(TabService tabs, IBrowserEngine engine, ILogger<TabLifecycleManager> log)
    {
        _tabs = tabs;
        _engine = engine;
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

    /// <summary>Run a single lifecycle pass.</summary>
    public void Tick()
    {
        if (!MemorySaverEnabled) return;
        var active = _tabs.ActiveTab?.Id;
        var now = DateTimeOffset.UtcNow;
        foreach (var t in _tabs.TabsInCurrentWorkspace())
        {
            if (t.Id == active) continue;
            if (t.Pinned) continue;
            if (t.Muted) continue;
            if (t.KeepAwake) continue;
            if (t.LogicalState == TabLogicalStateKind.Active) continue;

            var idle = now - t.LastInteractionUtc;
            if (t.RendererState == TabRendererStateKind.Live && idle >= WarmAfter)
            {
                var updated = t with { RendererState = TabRendererStateKind.Warm };
                PersistUpdated(updated);
            }
            if (t.RendererState != TabRendererStateKind.Ghost && idle >= GhostAfter)
            {
                var updated = t with { RendererState = TabRendererStateKind.Ghost };
                PersistUpdated(updated);
                _log.LogInformation("Tab {Id} ghosted (idle {IdleMinutes}m)", t.Id, idle.TotalMinutes);
            }
        }
    }

    public void GhostNow(TabRecord tab)
    {
        if (tab is null) return;
        if (tab.Pinned || tab.KeepAwake) return;
        var updated = tab with { RendererState = TabRendererStateKind.Ghost };
        PersistUpdated(updated);
    }

    public void SleepNow(TabRecord tab)
    {
        if (tab is null) return;
        if (tab.Pinned || tab.KeepAwake) return;
        var updated = tab with { RendererState = TabRendererStateKind.Warm };
        PersistUpdated(updated);
    }

    private void PersistUpdated(TabRecord updated)
    {
        var idx = _tabs.Tabs.IndexOf(_tabs.Tabs.FirstOrDefault(t => t.Id == updated.Id)!);
        if (idx < 0) return;
        _tabs.Tabs[idx] = updated;
    }
}