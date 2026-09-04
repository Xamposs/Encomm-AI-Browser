using System.Diagnostics;
using Encomm.Browser.Engine.Abstractions;

namespace Encomm.Browser.Memory;

/// <summary>
/// Process-tree memory snapshot. Includes:
///   * Encomm host process working set + private bytes
///   * Aggregate WebView2 child process memory (browser, renderer, GPU,
///     utility) when the engine exposes a CoreWebView2Environment
///   * Logical tab counts (Live / Warm / Ghost)
///   * Renderer instance count
///
/// We DO NOT invent per-tab attribution. The WebView2 browser process
/// shares its renderer count with all tabs and the underlying Chromium
/// process is shared across windows in the same process tree.
/// </summary>
public sealed class MemoryProbe
{
    private readonly Func<IReadOnlyList<TabStateSummary>> _tabSource;

    public MemoryProbe(Func<IReadOnlyList<TabStateSummary>> tabSource)
    {
        _tabSource = tabSource;
    }

    /// <summary>
    /// Capture a snapshot of the current process tree.
    /// </summary>
    /// <param name="processInfos">
    /// Optional list of WebView2 child process memory snapshots (process
    /// tree of the engine). If null, only the host is measured.
    /// </param>
    public MemorySnapshot Sample(IReadOnlyList<WebViewProcessInfo>? processInfos = null)
    {
        var hostProcess = Process.GetCurrentProcess();
        long hostWs, hostPrivate;
        try
        {
            hostProcess.Refresh();
            hostWs = hostProcess.WorkingSet64;
            hostPrivate = hostProcess.PrivateMemorySize64;
        }
        catch
        {
            hostWs = 0;
            hostPrivate = 0;
        }

        long browser = 0, renderer = 0, gpu = 0, utility = 0, total = hostWs;
        int count = 1;
        if (processInfos is not null)
        {
            foreach (var p in processInfos)
            {
                total += p.WorkingSet64;
                count++;
                switch (p.Kind)
                {
                    case WebViewProcessKind.Browser: browser += p.WorkingSet64; break;
                    case WebViewProcessKind.Renderer: renderer += p.WorkingSet64; break;
                    case WebViewProcessKind.Gpu: gpu += p.WorkingSet64; break;
                    case WebViewProcessKind.Utility: utility += p.WorkingSet64; break;
                }
            }
        }

        int live = 0, warm = 0, ghost = 0, total2 = 0;
        var tabs = _tabSource();
        foreach (var t in tabs)
        {
            total2++;
            switch (t.RendererState)
            {
                case TabRendererState.Live: live++; break;
                case TabRendererState.Warm: warm++; break;
                case TabRendererState.Ghost: ghost++; break;
            }
        }

        return new MemorySnapshot(
            HostProcessId: hostProcess.Id,
            HostWorkingSetBytes: hostWs,
            HostPrivateBytes: hostPrivate,
            WebView2BrowserBytes: browser,
            WebView2RendererBytes: renderer,
            WebView2GpuBytes: gpu,
            WebView2UtilityBytes: utility,
            ProcessTreeBytes: total,
            ProcessCount: count,
            TotalTabs: total2,
            LiveTabs: live,
            WarmTabs: warm,
            GhostTabs: ghost,
            SampledUtc: DateTimeOffset.UtcNow);
    }
}

public enum WebViewProcessKindRemoved_Duplicate { } // moved to Encomm.Browser.Engine.Abstractions

public enum TabRendererState
{
    Live,
    Warm,
    Ghost
}

public sealed record TabStateSummary(Guid TabId, TabRendererState RendererState);

public sealed record MemorySnapshot(
    int HostProcessId,
    long HostWorkingSetBytes,
    long HostPrivateBytes,
    long WebView2BrowserBytes,
    long WebView2RendererBytes,
    long WebView2GpuBytes,
    long WebView2UtilityBytes,
    long ProcessTreeBytes,
    int ProcessCount,
    int TotalTabs,
    int LiveTabs,
    int WarmTabs,
    int GhostTabs,
    DateTimeOffset SampledUtc);