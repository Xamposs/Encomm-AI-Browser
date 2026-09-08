using System.Collections.Generic;
using Encomm.Browser.Core.Storage;
using Encomm.Browser.Engine.Abstractions;

namespace Encomm.Browser.Developer;

/// <summary>
/// Process-wide toggle and access surface for Developer Mode. The UI
/// queries <see cref="IsEnabled"/> to decide whether to show Developer
/// surfaces, and the host uses <see cref="Diagnostics"/> to expose
/// extra capabilities.
/// </summary>
public sealed class DeveloperModeService
{
    public bool IsEnabled { get; private set; }
    public DeveloperDiagnostics Diagnostics { get; } = new();

    public void Enable() => IsEnabled = true;
    public void Disable() => IsEnabled = false;
    public void Toggle() => IsEnabled = !IsEnabled;
}

public sealed class DeveloperDiagnostics
{
    public bool ShowMemoryPanel { get; set; }
    public bool ShowDevToolsOnF12 { get; set; } = true;
    public bool ShowRendererState { get; set; } = true;
    public bool ShowShieldDiagnostics { get; set; } = true;
    public bool ShowAIDiagnostics { get; set; }
    public bool VerboseLogging { get; set; }
}

/// <summary>
/// Phase 2B: per-tab state-divergence detection. Compares the logical
/// TabRecord.RendererState against the actual renderer dictionary and
/// reports any mismatch. Used in Developer Mode to surface "logical says
/// Live but actual is no renderer" and similar inconsistencies.
/// </summary>
public sealed class StateDivergenceReport
{
    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();
    public int LogicalTabs { get; set; }
    public int Live { get; set; }
    public int Warm { get; set; }
    public int Ghost { get; set; }
    public int ActualRenderers { get; set; }

    public bool HasIssues => Warnings.Count > 0 || Errors.Count > 0;
}

/// <summary>
/// Builds a StateDivergenceReport by comparing the logical tab
/// list against the actual view dictionary.
/// </summary>
public sealed class StateDivergenceInspector
{
    private readonly IEnumerable<TabRecord> _tabs;
    private readonly IReadOnlyDictionary<Guid, IBrowserView> _views;

    public StateDivergenceInspector(IEnumerable<TabRecord> tabs, IReadOnlyDictionary<Guid, IBrowserView> views)
    {
        _tabs = tabs;
        _views = views;
    }

    public StateDivergenceReport Inspect()
    {
        var report = new StateDivergenceReport();
        var live = 0; var warm = 0; var ghost = 0;
        var byId = new Dictionary<Guid, IBrowserView>(_views);

        foreach (var tab in _tabs)
        {
            report.LogicalTabs++;
            switch (tab.RendererState)
            {
                case TabRendererStateKind.Live: live++; break;
                case TabRendererStateKind.Warm: warm++; break;
                case TabRendererStateKind.Ghost: ghost++; break;
            }

            var hasRenderer = byId.TryGetValue(tab.Id, out var view);
            if (tab.RendererState == TabRendererStateKind.Live && !hasRenderer)
            {
                report.Errors.Add(
                    $"Tab {tab.Id} ({ShortLabel(tab)}): logical=Live but no actual renderer instance.");
            }
            else if (tab.RendererState == TabRendererStateKind.Ghost && hasRenderer)
            {
                report.Errors.Add(
                    $"Tab {tab.Id} ({ShortLabel(tab)}): logical=Ghost but actual renderer instance still exists.");
            }
            else if (hasRenderer)
            {
                var st = view!.State;
                var ok = (tab.RendererState, st) switch
                {
                    (TabRendererStateKind.Live, ViewLifecycleState.Live) => true,
                    (TabRendererStateKind.Warm, ViewLifecycleState.Warm) => true,
                    (TabRendererStateKind.Ghost, ViewLifecycleState.Ghost) => true,
                    _ => false
                };
                if (!ok)
                {
                    report.Warnings.Add(
                        $"Tab {tab.Id} ({ShortLabel(tab)}): logical={tab.RendererState} but actual view state={st}.");
                }
            }
        }

        report.Live = live;
        report.Warm = warm;
        report.Ghost = ghost;
        report.ActualRenderers = _views.Count;
        return report;
    }

    private static string ShortLabel(TabRecord tab)
    {
        if (string.IsNullOrEmpty(tab.Title)) return tab.Url;
        return tab.Title.Length > 30 ? tab.Title.Substring(0, 27) + "..." : tab.Title;
    }
}

/// <summary>
/// Renders a StateDivergenceReport as a human-readable string for the
/// Developer Mode memory panel.
/// </summary>
public static class StateDivergenceFormatter
{
    public static string Format(StateDivergenceReport r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"State divergence:");
        sb.AppendLine($"  logical tabs: {r.LogicalTabs} (Live={r.Live} Warm={r.Warm} Ghost={r.Ghost})");
        sb.AppendLine($"  actual renderers: {r.ActualRenderers}");
        if (r.Errors.Count > 0)
        {
            sb.AppendLine($"  ERRORS ({r.Errors.Count}):");
            foreach (var e in r.Errors) sb.AppendLine($"    {e}");
        }
        if (r.Warnings.Count > 0)
        {
            sb.AppendLine($"  WARNINGS ({r.Warnings.Count}):");
            foreach (var w in r.Warnings) sb.AppendLine($"    {w}");
        }
        if (!r.HasIssues) sb.AppendLine("  no issues detected.");
        return sb.ToString();
    }
}
