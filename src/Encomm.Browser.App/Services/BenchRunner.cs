using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Encomm.Browser.Core.Storage;
using Encomm.Browser.Engine.Abstractions;
using Encomm.Browser.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Encomm.Browser.App.Services;

/// <summary>
/// In-app renderer memory benchmark (Phase 2B addendum items 5-8).
///
/// Runs inside the live Encomm process because WebView2 controls can
/// only exist on the app UI thread. Uses the local BenchmarkSite pages
/// over http://127.0.0.1:8099/ (python http.server child process) and
/// measures the ACTUAL Windows process tree via
/// CoreWebView2Environment.GetProcessInfos() + MemoryProbe.
///
/// Scenarios: A(1/1) B(5/5) C(10/10) D(10/1/9W) E(10/1/9G)
/// F(25/3/G) G(50/3/G) H(100/3/G). For D/E we measure before AND
/// after the Warm/Ghost transition. Restore cost (renderer-ready,
/// navigation, scroll, total) is sampled over real Ghost restores
/// with median/min/max. Nothing is inferred: every number is a
/// measured working-set / stopwatch sample.
///
/// Invoked once via `Encomm.exe --run-bench`. Writes
/// artifacts/benchmark-results.{json,md} under the repo root and
/// returns a process exit code (0 = ok).
/// </summary>
public static class BenchRunner
{
    private const int Port = 8099;
    private static readonly string[] Pages =
        { "static.html", "js.html", "images.html", "form.html", "dynamic.html" };

    public static async Task<int> RunAsync(string logPath, string[] args)
    {
        var siteDir = FindBenchmarkSite();
        if (siteDir is null)
        {
            Log(logPath, "Bench: tools/BenchmarkSite not found from " + AppContext.BaseDirectory);
            return 2;
        }
        var artifactsDir = Path.Combine(Directory.GetParent(Path.GetDirectoryName(siteDir)!)!.FullName, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        Process? server = null;
        try
        {
            server = StartServer(logPath, siteDir);
            if (server is null) return 2;
            await Task.Delay(1500);

            var tabs = App.Services.GetRequiredService<TabService>();
            var runtime = App.Services.GetRequiredService<BrowserRuntime>();
            var lifecycle = App.Services.GetRequiredService<TabLifecycleManager>();
            lifecycle.MemorySaverEnabled = false;

            var probe = new MemoryProbe(() =>
            {
                var list = new List<TabStateSummary>();
                foreach (var t in tabs.TabsInCurrentWorkspace())
                    list.Add(new TabStateSummary(t.Id, (TabRendererState)(int)t.RendererState));
                return list;
            });

            var report = new BenchReport(
                Machine: $"{Environment.OSVersion.VersionString} x64/{Environment.ProcessorCount}",
                StartedUtc: DateTimeOffset.UtcNow,
                Scenarios: new List<BenchScenario>());

            await ResetAsync(tabs, runtime, logPath);

            // A: 1 logical / 1 Live
            await ScenarioAllLiveAsync("A", 1, tabs, runtime, probe, report, logPath);
            // B: 5 logical / 5 Live
            await ScenarioAllLiveAsync("B", 5, tabs, runtime, probe, report, logPath);
            // C: 10 logical / 10 Live
            await ScenarioAllLiveAsync("C", 10, tabs, runtime, probe, report, logPath);
            // D: 10 logical / 1 Live / 9 Warm (measure before + after)
            await ScenarioWarmAsync(tabs, runtime, probe, report, logPath);
            // E: 10 logical / 1 Live / 9 Ghost (measure before + after, then restore cost)
            var restoreSamples = await ScenarioGhostAsync(tabs, runtime, probe, report, logPath);
            // F/G/H: N logical / 3 Live / rest Ghost
            await ScenarioScaledAsync("F", 25, 3, tabs, runtime, probe, report, logPath);
            await ScenarioScaledAsync("G", 50, 3, tabs, runtime, probe, report, logPath);
            await ScenarioScaledAsync("H", 100, 3, tabs, runtime, probe, report, logPath);

            report.RestoreCost = Summarize("ghost-restore", restoreSamples);
            report.FinishedUtc = DateTimeOffset.UtcNow;

            // Leave the profile tidy: remove all bench tabs.
            await ResetAsync(tabs, runtime, logPath);            var json = Path.Combine(artifactsDir, "benchmark-results.json");
            File.WriteAllText(json, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }));
            var md = Path.Combine(artifactsDir, "benchmark-results.md");
            File.WriteAllText(md, RenderMarkdown(report));
            Log(logPath, "Bench: PASS. Wrote " + json);

            lifecycle.MemorySaverEnabled = true;
            return 0;
        }
        catch (Exception ex)
        {
            Log(logPath, "Bench: FAIL: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
        finally
        {
            try
            {
                if (server is not null && !server.HasExited) server.Kill(true);
                server?.Dispose();
            }
            catch { }
        }
    }

    // -- Scenarios ----------------------------------------------------

    private static async Task ScenarioAllLiveAsync(string name, int n,
        TabService tabs, BrowserRuntime runtime, MemoryProbe probe,
        BenchReport report, string logPath)
    {
        var mine = await ResetAsync(tabs, runtime, logPath);
        var liveIds = new HashSet<Guid>();
        for (int i = 0; i < n; i++)
        {
            var tab = await tabs.OpenNewAsync(Page(i), switchTo: false);
            mine.Add(tab.Id);
            await MaterializeLiveAsync(tabs, runtime, tab);
            liveIds.Add(tab.Id);
        }
        await SettleAsync(tabs, runtime, logPath, mine, liveIds, $"scenario {name} ({n} live)");
        AddScenario(report, probe, runtime, $"{name}: {n} logical / {n} Live", null);
    }

    private static async Task ScenarioWarmAsync(TabService tabs, BrowserRuntime runtime,
        MemoryProbe probe, BenchReport report, string logPath)
    {
        var mine = await ResetAsync(tabs, runtime, logPath);
        var created = new List<TabRecord>();
        for (int i = 0; i < 10; i++)
        {
            var tab = await tabs.OpenNewAsync(Page(i), switchTo: false);
            mine.Add(tab.Id);
            await MaterializeLiveAsync(tabs, runtime, tab);
            created.Add(tab);
        }
        var liveIds = new HashSet<Guid>(created.Select(t => t.Id));
        await SettleAsync(tabs, runtime, logPath, mine, liveIds, "scenario D pre-warm (10 live)");
        AddScenario(report, probe, runtime, "D-pre: 10 logical / 10 Live", null);

        int warmed = 0;
        foreach (var t in created.Skip(1))
        {
            try
            {
                if (await runtime.SuspendAsync(t.Id))
                {
                    tabs.SetRendererState(t, TabRendererStateKind.Warm);
                    warmed++;
                    liveIds.Remove(t.Id);
                }
            }
            catch (Exception ex)
            {
                Log(logPath, $"Bench: warm failed for {t.Id}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        await SettleAsync(tabs, runtime, logPath, mine, liveIds, $"scenario D post-warm ({warmed}/9 warmed)");
        AddScenario(report, probe, runtime, $"D: 10 logical / 1 Live / {warmed} Warm", null);
    }

    private static async Task<List<RestoreBreakdown>> ScenarioGhostAsync(TabService tabs,
        BrowserRuntime runtime, MemoryProbe probe, BenchReport report, string logPath)
    {
        var mine = await ResetAsync(tabs, runtime, logPath);
        var created = new List<TabRecord>();
        for (int i = 0; i < 10; i++)
        {
            var tab = await tabs.OpenNewAsync(Page(i), switchTo: false);
            mine.Add(tab.Id);
            await MaterializeLiveAsync(tabs, runtime, tab);
            created.Add(tab);
        }
        var liveIds = new HashSet<Guid>(created.Select(t => t.Id));
        await SettleAsync(tabs, runtime, logPath, mine, liveIds, "scenario E pre-ghost (10 live)");
        AddScenario(report, probe, runtime, "E-pre: 10 logical / 10 Live", null);

        int ghosted = 0;
        foreach (var t in created.Skip(1))
        {
            try
            {
                await runtime.GhostAsync(t.Id);
                tabs.SetRendererState(t, TabRendererStateKind.Ghost);
                ghosted++;
                liveIds.Remove(t.Id);
            }
            catch { }
        }
        // Sample the 1-Live/9-Ghost state BEFORE restore-timing changes it.
        await SettleAsync(tabs, runtime, logPath, mine, liveIds, $"scenario E post-ghost ({ghosted}/9 ghosted)");
        AddScenario(report, probe, runtime, $"E: 10 logical / 1 Live / {ghosted} Ghost", null);

        var ghosts = tabs.TabsInCurrentWorkspace()
            .Where(t => t.RendererState == TabRendererStateKind.Ghost).ToList();
        var samples = new List<RestoreBreakdown>();
        int toRestore = Math.Min(5, ghosts.Count);
        for (int i = 0; i < toRestore; i++)
        {
            try
            {
                if (await runtime.RestoreGhostTabAsync(ghosts[i]))
                    samples.Add(runtime.LastRestoreBreakdown);
            }
            catch { }
        }
        Log(logPath, $"Bench: timed {samples.Count}/{toRestore} ghost restores.");
        return samples;
    }

    private static async Task ScenarioScaledAsync(string name, int n, int live,
        TabService tabs, BrowserRuntime runtime, MemoryProbe probe,
        BenchReport report, string logPath)
    {
        var mine = await ResetAsync(tabs, runtime, logPath);
        var created = new List<TabRecord>();
        for (int i = 0; i < n; i++)
        {
            var tab = await tabs.OpenNewAsync(Page(i), switchTo: false);
            mine.Add(tab.Id);
            created.Add(tab);
        }
        var liveIds = new HashSet<Guid>();
        for (int i = 0; i < Math.Min(live, created.Count); i++)
        {
            await MaterializeLiveAsync(tabs, runtime, created[i]);
            liveIds.Add(created[i].Id);
        }
        int ghosted = 0;
        foreach (var t in created.Skip(live))
        {
            try
            {
                await runtime.GhostAsync(t.Id);
                tabs.SetRendererState(t, TabRendererStateKind.Ghost);
                ghosted++;
            }
            catch { }
        }
        await SettleAsync(tabs, runtime, logPath, mine, liveIds, $"scenario {name} ({n} logical)");
        AddScenario(report, probe, runtime,
            $"{name}: {n} logical / {live} Live / {ghosted} Ghost", null);
    }

    // -- Primitives ---------------------------------------------------

    private static string Page(int i) => $"http://127.0.0.1:{Port}/{Pages[i % Pages.Length]}";

    /// <summary>
    /// Drain the workspace. Retries because the app's async session
    /// restore can re-add persisted tabs after the first pass.
    /// Returns the (empty-at-return) set for callers to track their own.
    /// </summary>
    private static async Task<HashSet<Guid>> ResetAsync(TabService tabs, BrowserRuntime runtime, string logPath)
    {
        for (int round = 0; round < 5; round++)
        {
            var all = tabs.TabsInCurrentWorkspace().ToList();
            if (all.Count == 0) break;
            foreach (var t in all)
            {
                try { await runtime.GhostAsync(t.Id); } catch { }
                try { tabs.Close(t); } catch { }
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        var left = tabs.TabsInCurrentWorkspace().Count();
        Log(logPath, $"Bench: workspace reset (remaining={left}).");
        return new HashSet<Guid>();
    }

    private static async Task MaterializeLiveAsync(TabService tabs, BrowserRuntime runtime, TabRecord tab)
    {
        var view = await runtime.GetOrCreateAsync(tab);
        if (view is null) return;
        var wait = AwaitNavAsync(view);
        var result = await view.NavigateAsync(tab.Url);
        if (!result.Accepted) return;
        await wait;
        tabs.SetRendererState(tab, TabRendererStateKind.Live);
    }

    private static Task<NavigationCompletedEventArgs?> AwaitNavAsync(IBrowserView view)
    {
        var tcs = new TaskCompletionSource<NavigationCompletedEventArgs?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<NavigationCompletedEventArgs>? handler = null;
        handler = (_, e) =>
        {
            if (e.Url is not null && e.Url.Contains("about:blank", StringComparison.OrdinalIgnoreCase)) return;
            if (handler is not null) view.NavigationCompleted -= handler;
            tcs.TrySetResult(e);
        };
        view.NavigationCompleted += handler;
        var watchdog = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
        {
            if (handler is not null) view.NavigationCompleted -= handler;
            tcs.TrySetResult(null);
        }, TaskScheduler.Default);
        _ = watchdog;
        return tcs.Task;
    }

    /// <summary>
    /// Settle before sampling: remove any tab that is not ours (late
    /// session-restores, host-control races), point the UI at one of our
    /// Live tabs (so the host attaches an existing view instead of
    /// restoring Ghosts), let async host refreshes finish, tear down any
    /// renderer whose logical tab is not in our Live set, then wait for
    /// the process tree to stabilize.
    /// </summary>
    private static async Task SettleAsync(TabService tabs, BrowserRuntime runtime,
        string logPath, HashSet<Guid> mine, HashSet<Guid> liveIds, string what)
    {
        for (int round = 0; round < 3; round++)
        {
            var strays = tabs.TabsInCurrentWorkspace().Where(t => !mine.Contains(t.Id)).ToList();
            if (strays.Count == 0) break;
            foreach (var t in strays)
            {
                try { await runtime.GhostAsync(t.Id); } catch { }
                try { tabs.Close(t); } catch { }
            }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        try
        {
            var live = tabs.TabsInCurrentWorkspace()
                .FirstOrDefault(t => liveIds.Contains(t.Id));
            if (live is not null) tabs.SetActive(live);
        }
        catch { }
        await Task.Delay(TimeSpan.FromSeconds(2));
        foreach (var kv in runtime.Views.ToList())
        {
            if (!liveIds.Contains(kv.Key))
            {
                try { await runtime.GhostAsync(kv.Key); } catch { }
            }
        }
        Log(logPath, "Bench: stabilizing: " + what);
        await Task.Delay(TimeSpan.FromSeconds(8));
    }

    private static void AddScenario(BenchReport report, MemoryProbe probe,
        BrowserRuntime runtime, string name, string? notes)
    {
        IReadOnlyList<WebViewProcessInfo> infos;
        // Bench flows run on the UI thread (no ConfigureAwait(false)),
        // so engine COM state is safe to touch directly here.
        try { infos = runtime.GetWebViewProcessInfos(); }
        catch { infos = Array.Empty<WebViewProcessInfo>(); }
        var snap = probe.Sample(infos);
        report.Scenarios.Add(new BenchScenario(
            name, notes, snap, runtime.Views.Count,
            KindBytes(infos, WebViewProcessKind.Browser),
            KindBytes(infos, WebViewProcessKind.Renderer),
            KindBytes(infos, WebViewProcessKind.Gpu),
            KindBytes(infos, WebViewProcessKind.Utility)));
    }

    private static long KindBytes(IReadOnlyList<WebViewProcessInfo> infos, WebViewProcessKind kind)
    {
        long sum = 0;
        foreach (var p in infos) if (p.Kind == kind) sum += p.WorkingSet64;
        return sum;
    }

    private static RestoreStats Summarize(string name, List<RestoreBreakdown> samples)
    {
        if (samples.Count == 0)
            return new RestoreStats(name, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        static double Med(List<double> v)
        {
            v.Sort();
            int n = v.Count;
            return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2.0;
        }
        var ready = samples.Select(s => s.RendererReady.TotalMilliseconds).ToList();
        var nav = samples.Select(s => s.Navigation.TotalMilliseconds).ToList();
        var scroll = samples.Select(s => s.Scroll.TotalMilliseconds).ToList();
        var total = samples.Select(s => s.Total.TotalMilliseconds).ToList();
        return new RestoreStats(name, samples.Count,
            Med(ready), ready.Min(), ready.Max(),
            Med(nav), nav.Min(), nav.Max(),
            Med(scroll), scroll.Min(), scroll.Max(),
            Med(total), total.Min(), total.Max(),
            total.Sum() / total.Count, 0);
    }

    // -- Environment --------------------------------------------------

    private static string? FindBenchmarkSite()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "BenchmarkSite", "static.html");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
        }
        return null;
    }

    private static Process? StartServer(string logPath, string siteDir)
    {
        foreach (var py in new[] { "python", "py" })
        {
            try
            {
                var psi = new ProcessStartInfo(py,
                    $"-m http.server {Port} --bind 127.0.0.1 --directory \"{siteDir}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                var proc = Process.Start(psi);
                if (proc is not null)
                {
                    Log(logPath, $"Bench: serving {siteDir} via {py} (pid {proc.Id}).");
                    return proc;
                }
            }
            catch (Exception ex)
            {
                Log(logPath, $"Bench: server start via {py} failed: {ex.Message}");
            }
        }
        Log(logPath, "Bench: no python found; cannot serve BenchmarkSite.");
        return null;
    }

    private static void Log(string path, string line)
    {
        try
        {
            File.AppendAllText(path,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [bench] {line}{Environment.NewLine}");
        }
        catch { }
    }

    private static string RenderMarkdown(BenchReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Encomm Renderer Benchmark (in-app, measured)");
        sb.AppendLine();
        sb.AppendLine($"- Machine: {r.Machine}");
        sb.AppendLine($"- Started: {r.StartedUtc:O} / Finished: {r.FinishedUtc:O}");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Host WS | Host Priv | WV2 Browser | WV2 Renderer | WV2 GPU | WV2 Util | Tree Total | Procs | Tabs L/W/G | Views |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---|---|---|");
        foreach (var s in r.Scenarios)
        {
            var snap = s.Snapshot;
            sb.AppendLine($"| {s.Name} | {Fmt(snap.HostWorkingSetBytes)} | {Fmt(snap.HostPrivateBytes)} | " +
                $"{Fmt(s.BrowserBytes)} | {Fmt(s.RendererBytes)} | {Fmt(s.GpuBytes)} | {Fmt(s.UtilBytes)} | " +
                $"{Fmt(snap.ProcessTreeBytes)} | {snap.ProcessCount} | " +
                $"{snap.LiveTabs}/{snap.WarmTabs}/{snap.GhostTabs} | {s.ViewCount} |");
        }
        sb.AppendLine();
        if (r.RestoreCost is not null)
        {
            var c = r.RestoreCost;
            sb.AppendLine($"## Restore cost ({c.SampleCount} samples)");
            sb.AppendLine();
            sb.AppendLine("| Phase | Median | Min | Max |");
            sb.AppendLine("|---|---:|---:|---:|");
            sb.AppendLine($"| Renderer ready (create+init) | {c.ReadyMedianMs:0} ms | {c.ReadyMinMs:0} ms | {c.ReadyMaxMs:0} ms |");
            sb.AppendLine($"| Navigation | {c.NavMedianMs:0} ms | {c.NavMinMs:0} ms | {c.NavMaxMs:0} ms |");
            sb.AppendLine($"| Scroll | {c.ScrollMedianMs:0} ms | {c.ScrollMinMs:0} ms | {c.ScrollMaxMs:0} ms |");
            sb.AppendLine($"| Total Ghost-to-usable | {c.TotalMedianMs:0} ms | {c.TotalMinMs:0} ms | {c.TotalMaxMs:0} ms |");
            sb.AppendLine();
        }
        sb.AppendLine("All numbers are measured Windows working-set bytes and stopwatch latencies.");
        return sb.ToString();
    }

    private static string Fmt(long bytes)
    {
        double v = bytes;
        string[] suf = { "B", "KB", "MB", "GB" };
        int i = 0;
        while (v >= 1024 && i < suf.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {suf[i]}";
    }
}

public sealed record BenchScenario(
    string Name,
    string? Notes,
    MemorySnapshot Snapshot,
    int ViewCount,
    long BrowserBytes,
    long RendererBytes,
    long GpuBytes,
    long UtilBytes);

public sealed record RestoreStats(
    string Name,
    int SampleCount,
    double ReadyMedianMs, double ReadyMinMs, double ReadyMaxMs,
    double NavMedianMs, double NavMinMs, double NavMaxMs,
    double ScrollMedianMs, double ScrollMinMs, double ScrollMaxMs,
    double TotalMedianMs, double TotalMinMs, double TotalMaxMs,
    double TotalMeanMs, double Reserved);

public sealed class BenchReport
{
    public BenchReport(string Machine, DateTimeOffset StartedUtc, List<BenchScenario> Scenarios)
    {
        this.Machine = Machine;
        this.StartedUtc = StartedUtc;
        this.Scenarios = Scenarios;
    }
    public string Machine { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset FinishedUtc { get; set; }
    public List<BenchScenario> Scenarios { get; init; }
    public RestoreStats? RestoreCost { get; set; }
}
