using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Encomm.Browser.Core.Storage;
using Encomm.Browser.Engine.Abstractions;
using Encomm.Browser.Memory;
using Encomm.Browser.Shield;
using Microsoft.Extensions.DependencyInjection;

namespace Encomm.Browser.App.Services;

/// <summary>
/// In-app renderer benchmark, Phase 2C corrected edition.
///
/// Correctness-first design (a benchmark result is INVALID when the
/// measured state is broken):
///   * liveIds / warmIds / keep = live ∪ warm. Settle destroys ONLY
///     views outside keep — Warm renderers are never ghosted by
///     cleanup, so scenario D measures REAL suspended renderers.
///   * Every sample is preceded by ValidateStates: Live⇒view+Live,
///     Warm⇒view+Warm, Ghost⇒no view. Divergence marks the scenario
///     INVALID with exact mismatch records (ScenarioValid=false).
///   * Materialization requires Accepted + successful completion;
///     failures invalidate the scenario instead of being measured.
///
/// Modes: --run-bench (full A..H suite, compat output),
/// --run-bench=A|B|C|D|E|F|G|H|H1|H3|H5|CREATE|RESTORE|WEB (single
/// isolated scenario writing artifacts/bench-&lt;id&gt;-&lt;ts&gt;.json).
/// Authoritative numbers come from fresh-process single runs driven
/// by scripts/run-performance-suite.ps1.
/// </summary>
public static class BenchRunner
{
    public const string BenchmarkVersion = "2C.1";
    public const string WinAppSdkVersion = "1.7.250606001";

    private const int DefaultPort = 8099;
    /// <summary>Port chosen for this run (unique per process, so stale
    /// orphan servers from killed runs can never intercept traffic).</summary>
    private static int ActivePort = DefaultPort;
    private static readonly string[] Pages =
        { "static.html", "js.html", "images.html", "form.html", "dynamic.html" };
    private static readonly string[] RealWebPages =
        { "https://example.com", "https://example.org", "https://www.iana.org" };

    // -- Scenario selection (pure, unit-tested) -----------------------

    public static string ParseBenchScenario(string[] args)
    {
        foreach (var a in args)
        {
            if (a == "--run-bench") return "FULL";
            if (a.StartsWith("--run-bench=", StringComparison.Ordinal))
            {
                var id = a.Substring("--run-bench=".Length).Trim().ToUpperInvariant();
                return ValidScenarioIds.Contains(id) ? id : "FULL";
            }
        }
        return "FULL";
    }

    public static readonly HashSet<string> ValidScenarioIds = new(StringComparer.OrdinalIgnoreCase)
        { "A", "B", "C", "D", "E", "F", "G", "H", "H1", "H3", "H5", "CREATE", "RESTORE", "WEB" };

    // -- State validation (pure, unit-tested) -------------------------

    /// <summary>
    /// Logical-vs-runtime invariant check. Returns human-readable
    /// mismatch records; empty means the measured state is trustworthy.
    /// </summary>
    public static List<string> ValidateStates(
        IEnumerable<TabRecord> tabs,
        IReadOnlyDictionary<Guid, IBrowserView> views)
    {
        var errors = new List<string>();
        int liveViews = 0, warmViews = 0;
        foreach (var t in tabs)
        {
            var has = views.TryGetValue(t.Id, out var v);
            switch (t.RendererState)
            {
                case TabRendererStateKind.Live:
                    if (!has) errors.Add($"Live tab {t.Id} has no renderer");
                    else if (v!.State != ViewLifecycleState.Live)
                        errors.Add($"Live tab {t.Id} has view in state {v.State}");
                    else liveViews++;
                    break;
                case TabRendererStateKind.Warm:
                    if (!has) errors.Add($"Warm tab {t.Id} has no renderer (physically Ghost!)");
                    else if (v!.State != ViewLifecycleState.Warm)
                        errors.Add($"Warm tab {t.Id} has view in state {v.State}");
                    else warmViews++;
                    break;
                case TabRendererStateKind.Ghost:
                    if (has) errors.Add($"Ghost tab {t.Id} still has a renderer in state {v!.State}");
                    break;
            }
        }
        foreach (var kv in views)
        {
            bool known = false;
            foreach (var t in tabs) if (t.Id == kv.Key) { known = true; break; }
            if (!known) errors.Add($"Orphan renderer {kv.Key} with no logical tab");
        }
        return errors;
    }

    // -- Entry ---------------------------------------------------------

    public static async Task<int> RunAsync(string logPath, string[] args)
    {
        var scenario = ParseBenchScenario(args);
        var siteDir = FindBenchmarkSite();
        if (siteDir is null)
        {
            Log(logPath, "Bench: tools/BenchmarkSite not found from " + AppContext.BaseDirectory);
            return 2;
        }
        var artifactsDir = BenchmarkArtifactsDir(siteDir);
        Directory.CreateDirectory(artifactsDir);

        Process? server = null;
        int port = DefaultPort;
        try
        {
            port = PickFreePort(logPath);
            ActivePort = port;
            server = StartServer(logPath, siteDir, port);
            if (server is null) return 2;
            if (!await WaitForServerAsync(port, logPath)) return 2;

            var tabs = App.Services.GetRequiredService<TabService>();
            var runtime = App.Services.GetRequiredService<BrowserRuntime>();
            var lifecycle = App.Services.GetRequiredService<TabLifecycleManager>();
            lifecycle.MemorySaverEnabled = false;
            var blocker = App.Services.GetRequiredService<IRequestBlocker>();
            // Deterministic Shield state for the block test: the concrete
            // RequestBlocker honors Enabled; any other implementation is
            // used as-is (rules come from FilterRuleProvider either way).
            if (blocker is RequestBlocker concrete) concrete.Enabled = true;

            var probe = new MemoryProbe(() =>
            {
                var list = new List<TabStateSummary>();
                foreach (var t in tabs.TabsInCurrentWorkspace())
                    list.Add(new TabStateSummary(t.Id, (TabRendererState)(int)t.RendererState));
                return list;
            });

            var meta = CollectMeta(siteDir);
            var report = new BenchReport(meta);
            report.LogPath = logPath;
            report.TabSource = () => tabs.TabsInCurrentWorkspace();

            if (scenario == "FULL")
            {
                await ResetAsync(tabs, runtime, logPath);
                report.Scenarios.Add(await ScenarioAllLiveAsync("A", 1, tabs, runtime, probe, report, logPath));
                report.Scenarios.Add(await ScenarioAllLiveAsync("B", 5, tabs, runtime, probe, report, logPath));
                report.Scenarios.Add(await ScenarioAllLiveAsync("C", 10, tabs, runtime, probe, report, logPath));
                var d = await ScenarioWarmAsync(tabs, runtime, probe, report, logPath);
                report.Scenarios.AddRange(d.Samples);
                var e = await ScenarioGhostAsync(tabs, runtime, probe, report, logPath, restoreSamples: 5);
                report.Scenarios.AddRange(e.Samples);
                report.RestoreCost = e.Restores;
                report.Scenarios.Add(await ScenarioScaledAsync("F", 25, 3, tabs, runtime, probe, report, logPath));
                report.Scenarios.Add(await ScenarioScaledAsync("G", 50, 3, tabs, runtime, probe, report, logPath));
                report.Scenarios.Add(await ScenarioScaledAsync("H", 100, 3, tabs, runtime, probe, report, logPath));
                report.FinishedUtc = DateTimeOffset.UtcNow;

                var json = Path.Combine(artifactsDir, "benchmark-results.json");
                File.WriteAllText(json, JsonSerializer.Serialize(report,
                    new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllText(Path.Combine(artifactsDir, "benchmark-results.md"), RenderMarkdown(report));
                Log(logPath, "Bench: PASS (full suite). Wrote " + json);
            }
            else
            {
                var single = await RunSingleAsync(scenario, tabs, runtime, probe, report, logPath, meta);
                report.Scenarios.AddRange(single.Samples);
                report.RestoreCost = single.Restores;
                report.FinishedUtc = DateTimeOffset.UtcNow;
                var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
                var json = Path.Combine(artifactsDir, $"bench-{scenario}-{stamp}.json");
                File.WriteAllText(json, JsonSerializer.Serialize(
                    new BenchSingleFile(meta, single.Samples, single.Restores),
                    new JsonSerializerOptions { WriteIndented = true }));
                Log(logPath, $"Bench: PASS (single {scenario}). Wrote " + json);
            }

            lifecycle.MemorySaverEnabled = true;
            await ResetAsync(tabs, runtime, logPath);
            return report.Scenarios.Any(s => !s.ScenarioValid) ? 3 : 0;
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

    private static async Task<SingleResult> RunSingleAsync(string scenario,
        TabService tabs, BrowserRuntime runtime, MemoryProbe probe,
        BenchReport report, string logPath, BenchMeta meta)
    {
        await ResetAsync(tabs, runtime, logPath);
        return scenario switch
        {
            "A" => Single(await ScenarioAllLiveAsync("A", 1, tabs, runtime, probe, report, logPath)),
            "B" => Single(await ScenarioAllLiveAsync("B", 5, tabs, runtime, probe, report, logPath)),
            "C" => Single(await ScenarioAllLiveAsync("C", 10, tabs, runtime, probe, report, logPath)),
            "D" => await ScenarioWarmAsync(tabs, runtime, probe, report, logPath),
            "E" => await ScenarioGhostAsync(tabs, runtime, probe, report, logPath, restoreSamples: 10),
            "F" => Single(await ScenarioScaledAsync("F", 25, 3, tabs, runtime, probe, report, logPath)),
            "G" => Single(await ScenarioScaledAsync("G", 50, 3, tabs, runtime, probe, report, logPath)),
            "H" => Single(await ScenarioScaledAsync("H", 100, 3, tabs, runtime, probe, report, logPath)),
            "H1" => Single(await ScenarioScaledAsync("H1", 100, 1, tabs, runtime, probe, report, logPath)),
            "H3" => Single(await ScenarioScaledAsync("H3", 100, 3, tabs, runtime, probe, report, logPath)),
            "H5" => Single(await ScenarioScaledAsync("H5", 100, 5, tabs, runtime, probe, report, logPath)),
            "CREATE" => Single(await ScenarioCreateAsync(tabs, runtime, probe, report, logPath)),
            "RESTORE" => await ScenarioRestoreLoadAsync(tabs, runtime, probe, report, logPath),
            "WEB" => Single(await ScenarioWebAsync(tabs, runtime, probe, report, logPath)),
            _ => Single(await ScenarioAllLiveAsync("A", 1, tabs, runtime, probe, report, logPath)),
        };
    }

    private static SingleResult Single(BenchScenario s)
        => new(new List<BenchScenario> { s }, null);

    public sealed record SingleResult(List<BenchScenario> Samples, RestoreStats? Restores);

    // -- Scenarios -----------------------------------------------------

    private static async Task<BenchScenario> ScenarioAllLiveAsync(string name, int n,
        TabService tabs, BrowserRuntime runtime, MemoryProbe probe,
        BenchReport report, string logPath)
    {
        var mine = await ResetAsync(tabs, runtime, logPath);
        var liveIds = new HashSet<Guid>();
        var navFailures = new List<string>();
        for (int i = 0; i < n; i++)
        {
            var tab = await tabs.OpenNewAsync(Page(i), switchTo: false);
            mine.Add(tab.Id);
            if (await MaterializeLiveAsync(tabs, runtime, tab)) liveIds.Add(tab.Id);
            else navFailures.Add(tab.Id.ToString());
        }
        var keep = new HashSet<Guid>(liveIds);
        await SettleAsync(tabs, runtime, logPath, mine, keep, $"scenario {name} ({n} live)");
        return AddScenario(report, probe, runtime,
            $"{name}: {n} logical / {liveIds.Count} Live", null, navFailures);
    }

    private static async Task<SingleResult> ScenarioWarmAsync(TabService tabs,
        BrowserRuntime runtime, MemoryProbe probe, BenchReport report, string logPath)
    {
        var out_ = new List<BenchScenario>();
        var mine = await ResetAsync(tabs, runtime, logPath);
        var created = new List<TabRecord>();
        var navFailures = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            var tab = await tabs.OpenNewAsync(Page(i), switchTo: false);
            mine.Add(tab.Id);
            if (await MaterializeLiveAsync(tabs, runtime, tab)) created.Add(tab);
            else navFailures.Add(tab.Id.ToString());
        }
        var liveIds = new HashSet<Guid>(created.Select(t => t.Id));
        await SettleAsync(tabs, runtime, logPath, mine, liveIds, "scenario D pre-warm (10 live)");
        out_.Add(AddScenario(report, probe, runtime, "D-pre: 10 logical / 10 Live", null, navFailures));

        // Suspend 9 -> REAL Warm renderers retained.
        var warmIds = new HashSet<Guid>();
        foreach (var t in created.Skip(1))
        {
            try
            {
                if (await runtime.SuspendAsync(t.Id))
                {
                    var v = runtime.Views.TryGetValue(t.Id, out var view) ? view : null;
                    if (v is not null && v.State == ViewLifecycleState.Warm)
                    {
                        tabs.SetRendererState(t, TabRendererStateKind.Warm);
                        warmIds.Add(t.Id);
                        liveIds.Remove(t.Id);
                    }
                    else Log(logPath, $"Bench: warm state mismatch for {t.Id}");
                }
            }
            catch (Exception ex)
            {
                Log(logPath, $"Bench: warm failed for {t.Id}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        var keep = new HashSet<Guid>(liveIds);
        keep.UnionWith(warmIds);
        await SettleAsync(tabs, runtime, logPath, mine, keep, $"scenario D post-warm ({warmIds.Count}/9 warm)");
        var d = AddScenario(report, probe, runtime,
            $"D: 10 logical / {liveIds.Count} Live / {warmIds.Count} Warm", null, navFailures);
        // Phase-2C gate: D is valid ONLY with 9 real Warm views.
        if (liveIds.Count != 1 || warmIds.Count != 9)
            d = d with
            {
                ScenarioValid = false,
                ValidationErrors = new List<string>(d.ValidationErrors)
                    { $"D gate failed: expected 1 Live + 9 Warm, got {liveIds.Count} Live + {warmIds.Count} Warm" }
            };
        out_.Add(d);

        // Live Warm->Live resume verification (Phase 2C item 24).
        int resumed = 0, usable = 0;
        foreach (var id in warmIds.ToList())
        {
            try
            {
                if (await runtime.ResumeAsync(id)
                    && runtime.Views.TryGetValue(id, out var v)
                    && v.State == ViewLifecycleState.Live)
                {
                    var tab = tabs.TabsInCurrentWorkspace().FirstOrDefault(t => t.Id == id);
                    if (tab is not null) tabs.SetRendererState(tab, TabRendererStateKind.Live);
                    resumed++;
                    if (!string.IsNullOrEmpty(v.CurrentTitle)) usable++;
                }
            }
            catch { }
        }
        Log(logPath, $"Bench: Warm resume verify: {resumed}/{warmIds.Count} resumed Live, {usable} with title.");
        d.Extra["resumeVerified"] = $"{resumed}/{warmIds.Count}";
        d.Extra["resumeUsableTitles"] = usable.ToString();
        return new SingleResult(out_, null);
    }

    private static async Task<SingleResult> ScenarioGhostAsync(TabService tabs,
        BrowserRuntime runtime, MemoryProbe probe, BenchReport report,
        string logPath, int restoreSamples)
    {
        var out_ = new List<BenchScenario>();
        var mine = await ResetAsync(tabs, runtime, logPath);
        var created = new List<TabRecord>();
        var navFailures = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            var tab = await tabs.OpenNewAsync(Page(i), switchTo: false);
            mine.Add(tab.Id);
            if (await MaterializeLiveAsync(tabs, runtime, tab)) created.Add(tab);
            else navFailures.Add(tab.Id.ToString());
        }
        var liveIds = new HashSet<Guid>(created.Select(t => t.Id));
        await SettleAsync(tabs, runtime, logPath, mine, liveIds, "scenario E pre-ghost (10 live)");
        out_.Add(AddScenario(report, probe, runtime, "E-pre: 10 logical / 10 Live", null, navFailures));

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
        await SettleAsync(tabs, runtime, logPath, mine, liveIds, $"scenario E post-ghost ({ghosted}/9 ghosted)");
        out_.Add(AddScenario(report, probe, runtime,
            $"E: 10 logical / {liveIds.Count} Live / {ghosted} Ghost", null, navFailures));

        // Scroll round-trip verification on the long page (item 25).
        var scroll = await VerifyScrollAsync(tabs, runtime, mine, logPath);
        // Shield local verification (item 26).
        var shield = await VerifyShieldAsync(tabs, runtime, mine, logPath);
        // Restore timing over the remaining ghosts.
        var ghosts = tabs.TabsInCurrentWorkspace()
            .Where(t => mine.Contains(t.Id) && t.RendererState == TabRendererStateKind.Ghost).ToList();
        var samples = new List<RestoreBreakdown>();
        foreach (var g in ghosts.Take(restoreSamples))
        {
            try
            {
                if (await runtime.RestoreGhostTabAsync(g))
                    samples.Add(runtime.LastRestoreBreakdown);
            }
            catch { }
        }
        Log(logPath, $"Bench: timed {samples.Count} ghost restores; scroll={scroll}; shield={shield}.");
        var stats = Summarize("ghost-restore", samples);
        stats.Extra["scrollVerify"] = scroll;
        stats.Extra["shieldVerify"] = shield;
        return new SingleResult(out_, stats);
    }

    private static async Task<BenchScenario> ScenarioScaledAsync(string name, int n, int live,
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
        var navFailures = new List<string>();
        for (int i = 0; i < Math.Min(live, created.Count); i++)
        {
            if (await MaterializeLiveAsync(tabs, runtime, created[i])) liveIds.Add(created[i].Id);
            else navFailures.Add(created[i].Id.ToString());
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
        return AddScenario(report, probe, runtime,
            $"{name}: {n} logical / {liveIds.Count} Live / {ghosted} Ghost", null, navFailures);
    }

    private static async Task<BenchScenario> ScenarioCreateAsync(TabService tabs,
        BrowserRuntime runtime, MemoryProbe probe, BenchReport report, string logPath)
    {
        var mine = await ResetAsync(tabs, runtime, logPath);
        await SettleAsync(tabs, runtime, logPath, mine, new HashSet<Guid>(), "CREATE baseline (0 tabs)");
        var before = AddScenario(report, probe, runtime, "CREATE-pre: 0 tabs", null, new List<string>());
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            var tab = await tabs.OpenNewAsync(Page(i), switchTo: false);
            mine.Add(tab.Id);
        }
        sw.Stop();
        await SettleAsync(tabs, runtime, logPath, mine, new HashSet<Guid>(), "CREATE post (100 logical, 0 views)");
        var after = AddScenario(report, probe, runtime,
            $"CREATE: 100 logical / 0 Live (creation {sw.Elapsed.TotalMilliseconds:0} ms)",
            null, new List<string>());
        after.Extra["createMs100"] = sw.Elapsed.TotalMilliseconds.ToString("0.0");
        after.Extra["hostDeltaBytes"] =
            (after.Snapshot.HostWorkingSetBytes - before.Snapshot.HostWorkingSetBytes).ToString();
        return after;
    }

    private static async Task<SingleResult> ScenarioRestoreLoadAsync(TabService tabs,
        BrowserRuntime runtime, MemoryProbe probe, BenchReport report, string logPath)
    {
        var out_ = new List<BenchScenario>();
        // Case A: 1 Live + 9 Ghost.
        var mineA = await ResetAsync(tabs, runtime, logPath);
        var createdA = new List<TabRecord>();
        for (int i = 0; i < 10; i++)
        {
            var tab = await tabs.OpenNewAsync(Page(i), switchTo: false);
            mineA.Add(tab.Id);
            if (await MaterializeLiveAsync(tabs, runtime, tab)) createdA.Add(tab);
        }
        var liveA = new HashSet<Guid>(createdA.Take(1).Select(t => t.Id));
        foreach (var t in createdA.Skip(1))
        {
            await runtime.GhostAsync(t.Id);
            tabs.SetRendererState(t, TabRendererStateKind.Ghost);
        }
        await SettleAsync(tabs, runtime, logPath, mineA, liveA, "RESTORE case A (1+9G)");
        out_.Add(AddScenario(report, probe, runtime, "RESTORE-A: 1 Live / 9 Ghost", null, new List<string>()));
        var samplesA = await TimeRestoresAsync(tabs, runtime, mineA, 10, logPath, "A");

        // Case B: 3 Live + 97 Ghost.
        var mineB = await ResetAsync(tabs, runtime, logPath);
        var createdB = new List<TabRecord>();
        for (int i = 0; i < 100; i++)
        {
            var tab = await tabs.OpenNewAsync(Page(i), switchTo: false);
            mineB.Add(tab.Id);
            createdB.Add(tab);
        }
        var liveB = new HashSet<Guid>();
        for (int i = 0; i < 3 && i < createdB.Count; i++)
        {
            if (await MaterializeLiveAsync(tabs, runtime, createdB[i])) liveB.Add(createdB[i].Id);
        }
        foreach (var t in createdB.Skip(3))
        {
            await runtime.GhostAsync(t.Id);
            tabs.SetRendererState(t, TabRendererStateKind.Ghost);
        }
        await SettleAsync(tabs, runtime, logPath, mineB, liveB, "RESTORE case B (3+97G)");
        out_.Add(AddScenario(report, probe, runtime, "RESTORE-B: 3 Live / 97 Ghost", null, new List<string>()));
        var samplesB = await TimeRestoresAsync(tabs, runtime, mineB, 10, logPath, "B");

        var stats = Summarize("ghost-restore-under-load", samplesA.Concat(samplesB).ToList());
        stats.Extra["caseA"] = Describe(samplesA);
        stats.Extra["caseB"] = Describe(samplesB);
        return new SingleResult(out_, stats);
    }

    private static async Task<BenchScenario> ScenarioWebAsync(TabService tabs,
        BrowserRuntime runtime, MemoryProbe probe, BenchReport report, string logPath)
    {
        var mine = await ResetAsync(tabs, runtime, logPath);
        var liveIds = new HashSet<Guid>();
        var navFailures = new List<string>();
        var titles = new List<string>();
        foreach (var url in RealWebPages)
        {
            var tab = await tabs.OpenNewAsync(url, switchTo: false);
            mine.Add(tab.Id);
            if (await MaterializeLiveAsync(tabs, runtime, tab))
            {
                liveIds.Add(tab.Id);
                if (runtime.Views.TryGetValue(tab.Id, out var v))
                    titles.Add($"{url}=>{v.CurrentTitle}");
            }
            else navFailures.Add(url);
        }
        await SettleAsync(tabs, runtime, logPath, mine, liveIds, "WEB sanity (3 real pages)");
        var s = AddScenario(report, probe, runtime,
            $"WEB: 3 real pages / {liveIds.Count} Live", null, navFailures);
        s.Extra["titles"] = string.Join(" | ", titles);
        return s;
    }

    // -- Verification flows (items 24/25/26) ---------------------------

    private static async Task<List<RestoreBreakdown>> TimeRestoresAsync(TabService tabs,
        BrowserRuntime runtime, HashSet<Guid> mine, int count, string logPath, string tag)
    {
        var samples = new List<RestoreBreakdown>();
        var ghosts = tabs.TabsInCurrentWorkspace()
            .Where(t => mine.Contains(t.Id) && t.RendererState == TabRendererStateKind.Ghost).ToList();
        foreach (var g in ghosts.Take(count))
        {
            try
            {
                if (await runtime.RestoreGhostTabAsync(g))
                    samples.Add(runtime.LastRestoreBreakdown);
            }
            catch { }
        }
        Log(logPath, $"Bench: restore-under-load {tag}: timed {samples.Count} restores.");
        return samples;
    }

    /// <summary>
    /// Scroll round-trip verification (item 25), deterministic form:
    /// proves SetScroll works (read-back), then proves the RESTORE
    /// machinery end-to-end by authoritatively setting the record AFTER
    /// ghost (bypassing live-page scroll dynamics between capture and
    /// teardown, which are outside the machinery under test).
    /// </summary>
    private static async Task<string> VerifyScrollAsync(TabService tabs,
        BrowserRuntime runtime, HashSet<Guid> mine, string logPath)
    {
        try
        {
            var tab = await tabs.OpenNewAsync(PageUrl("long.html"), switchTo: false);
            mine.Add(tab.Id);
            if (!await MaterializeLiveAsync(tabs, runtime, tab)) return "materialize-failed";
            if (!runtime.Views.TryGetValue(tab.Id, out var view)) return "no-view";
            await view.SetScrollAsync(0, 2000);
            await Task.Delay(500);
            var (x0, y0) = await view.GetScrollAsync();
            var setOk = Math.Abs(y0 - 2000) <= 150;
            await runtime.GhostAsync(tab.Id);
            tabs.SetRendererState(tab, TabRendererStateKind.Ghost);
            // Authoritative record AFTER ghost: the restore path must
            // honor exactly what the record says.
            tabs.MutateScroll(tab.Id, 0, 2000);
            var ghosts = tabs.TabsInCurrentWorkspace().First(t => t.Id == tab.Id);
            if (!await runtime.RestoreGhostTabAsync(ghosts)) return "restore-failed";
            if (!runtime.Views.TryGetValue(tab.Id, out var view2)) return "no-view-after-restore";
            await Task.Delay(500);
            var (x1, y1) = await view2.GetScrollAsync();
            await runtime.GhostAsync(tab.Id);
            tabs.SetRendererState(tab, TabRendererStateKind.Ghost);
            var restoreOk = Math.Abs(y1 - 2000) <= 150;
            var result = $"set={y0:0} restored={y1:0} {(setOk && restoreOk ? "VERIFIED" : "MISMATCH")}";
            Log(logPath, "Bench: scroll verify: " + result);
            return result;
        }
        catch (Exception ex)
        {
            return "error:" + ex.GetType().Name;
        }
    }

    private static async Task<string> VerifyShieldAsync(TabService tabs,
        BrowserRuntime runtime, HashSet<Guid> mine, string logPath)
    {
        try
        {
            var tab = await tabs.OpenNewAsync(PageUrl("shield.html"), switchTo: false);
            mine.Add(tab.Id);
            var view = await runtime.GetOrCreateAsync(tab);
            if (view is null) return "no-view";
            int blocked = 0;
            void Handler(object? s, ResourceBlockedEventArgs e) => blocked++;
            view.ResourceBlocked += Handler;
            var wait = AwaitNavAsync(view);
            var result = await view.NavigateAsync(tab.Url);
            if (result.Accepted) await wait;
            view.ResourceBlocked -= Handler;
            tabs.SetRendererState(tab, TabRendererStateKind.Live);
            var title = view.CurrentTitle ?? "";
            await runtime.GhostAsync(tab.Id);
            tabs.SetRendererState(tab, TabRendererStateKind.Ghost);
            var verdict = blocked >= 1 && title.Contains("Encomm Shield Test")
                ? "VERIFIED" : "MISMATCH";
            var result2 = $"blocked={blocked} title='{title}' {verdict}";
            Log(logPath, "Bench: shield verify: " + result2);
            return result2;
        }
        catch (Exception ex)
        {
            return "error:" + ex.GetType().Name;
        }
    }

    // -- Primitives ----------------------------------------------------

    private static string Page(int i) => PageUrl(Pages[i % Pages.Length]);
    private static string PageUrl(string file) => $"http://127.0.0.1:{ActivePort}/{file}";

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
        Log(logPath, $"Bench: workspace reset (remaining={tabs.TabsInCurrentWorkspace().Count()}).");
        return new HashSet<Guid>();
    }

    /// <summary>
    /// Materialize a Live renderer. Returns true ONLY when navigation
    /// was accepted AND the main-frame completion reported success
    /// (item 23: never measure a broken state).
    /// </summary>
    private static async Task<bool> MaterializeLiveAsync(TabService tabs, BrowserRuntime runtime, TabRecord tab)
    {
        try
        {
            var view = await runtime.GetOrCreateAsync(tab);
            if (view is null) return false;
            var wait = AwaitNavAsync(view);
            var result = await view.NavigateAsync(tab.Url);
            if (!result.Accepted) return false;
            var completed = await wait;
            if (completed is null || !completed.Success) return false;
            tabs.SetRendererState(tab, TabRendererStateKind.Live);
            return true;
        }
        catch { return false; }
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

    private static async Task SettleAsync(TabService tabs, BrowserRuntime runtime,
        string logPath, HashSet<Guid> mine, HashSet<Guid> keep, string what)
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
            var keepTab = tabs.TabsInCurrentWorkspace().FirstOrDefault(t => keep.Contains(t.Id));
            if (keepTab is not null) tabs.SetActive(keepTab);
        }
        catch { }
        await Task.Delay(TimeSpan.FromSeconds(2));
        // Destroy ONLY views outside keep = live ∪ warm. Warm renderers
        // MUST survive settling; otherwise scenario D is fiction.
        foreach (var kv in runtime.Views.ToList())
        {
            if (!keep.Contains(kv.Key))
            {
                try { await runtime.GhostAsync(kv.Key); } catch { }
            }
        }
        Log(logPath, "Bench: stabilizing: " + what);
        await Task.Delay(TimeSpan.FromSeconds(8));
    }

    private static BenchScenario AddScenario(BenchReport report, MemoryProbe probe,
        BrowserRuntime runtime, string name, string? notes, List<string> navFailures)
    {
        IReadOnlyList<WebViewProcessInfo> infos;
        try { infos = runtime.GetWebViewProcessInfos(); }
        catch { infos = Array.Empty<WebViewProcessInfo>(); }
        var snap = probe.Sample(infos);
        var errors = ValidateStates(report.TabSource(), runtime.Views);
        foreach (var f in navFailures) errors.Add($"navigation failed for tab {f}");
        var s = new BenchScenario(
            Id: Slug(name), Name: name, Notes: notes, Snapshot: snap,
            ViewCount: runtime.Views.Count,
            BrowserBytes: KindBytes(infos, WebViewProcessKind.Browser),
            RendererBytes: KindBytes(infos, WebViewProcessKind.Renderer),
            GpuBytes: KindBytes(infos, WebViewProcessKind.Gpu),
            UtilBytes: KindBytes(infos, WebViewProcessKind.Utility),
            ScenarioValid: errors.Count == 0,
            ValidationErrors: errors,
            Restores: null,
            Extra: new Dictionary<string, string>());
        if (!s.ScenarioValid)
            Log(ReportLog(report), "Bench: scenario INVALID: " + name + " :: " + string.Join("; ", errors));
        return s;
    }

    private static string ReportLog(BenchReport report) => report.LogPath;

    private static string Slug(string name)
    {
        if (name.StartsWith("D-pre")) return "D-pre";
        if (name.StartsWith("E-pre")) return "E-pre";
        if (name.StartsWith("RESTORE-A")) return "RESTORE-A";
        if (name.StartsWith("RESTORE-B")) return "RESTORE-B";
        if (name.StartsWith("CREATE-pre")) return "CREATE-pre";
        if (name.StartsWith("H1")) return "H1";
        if (name.StartsWith("H3")) return "H3";
        if (name.StartsWith("H5")) return "H5";
        if (name.StartsWith("CREATE")) return "CREATE";
        if (name.StartsWith("RESTORE")) return "RESTORE";
        if (name.StartsWith("WEB")) return "WEB";
        var c = name.Length >= 1 ? name.Substring(0, 1) : "X";
        return c;
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
            return new RestoreStats(name, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                new Dictionary<string, string>());
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
            total.Sum() / total.Count, 0,
            new Dictionary<string, string>());
    }

    private static string Describe(List<RestoreBreakdown> samples)
    {
        if (samples.Count == 0) return "n=0";
        var t = samples.Select(s => s.Total.TotalMilliseconds).OrderBy(v => v).ToList();
        double med = t.Count % 2 == 1 ? t[t.Count / 2] : (t[t.Count / 2 - 1] + t[t.Count / 2]) / 2.0;
        return $"n={t.Count} med={med:0}ms min={t.Min():0}ms max={t.Max():0}ms";
    }

    // -- Environment + versioning (item 18) ----------------------------

    public static string BenchmarkArtifactsDir(string siteDir)
    {
        var tools = Directory.GetParent(siteDir)!.FullName;
        return Path.Combine(Directory.GetParent(tools)!.FullName, "artifacts");
    }

    private static BenchMeta CollectMeta(string siteDir)
    {
        string repo = Directory.GetParent(Directory.GetParent(siteDir)!.FullName)!.FullName;
        return new BenchMeta(
            BenchmarkVersion, GitSha(repo), "Release",
            Environment.OSVersion.VersionString, Environment.ProcessorCount,
            PhysicalRamBytes(), WebView2Version(), WinAppSdkVersion,
            DateTimeOffset.UtcNow);
    }

    private static string GitSha(string repo)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                WorkingDirectory = repo, CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return "unknown";
            var sha = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10000);
            return string.IsNullOrEmpty(sha) ? "unknown" : sha;
        }
        catch { return "unknown"; }
    }

    private static long PhysicalRamBytes()
    {
        try
        {
            if (GetPhysicallyInstalledSystemMemory(out ulong kb)) return (long)kb * 1024;
        }
        catch { }
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKb);

    private static string WebView2Version()
    {
        try
        {
            return Microsoft.Web.WebView2.Core.CoreWebView2Environment
                .GetAvailableBrowserVersionString() ?? "unknown";
        }
        catch { return "unknown"; }
    }

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

    /// <summary>
    /// Pick a free loopback port so this run can never collide with
    /// orphaned servers from killed runs (each of which squats on the
    /// fixed default port and sprays connections across stale
    /// listeners). Falls back to the default when probing fails.
    /// </summary>
    private static int PickFreePort(string logPath)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            Log(logPath, $"Bench: selected port {port}.");
            return port;
        }
        catch (Exception ex)
        {
            Log(logPath, "Bench: port probe failed, using default: " + ex.Message);
            return DefaultPort;
        }
    }

    private static async Task<bool> WaitForServerAsync(int port, string logPath)
    {
        // Probe the ACTUAL page bytes: a stale listener may accept the
        // TCP connection and then drop it, which connect() alone cannot
        // distinguish from a healthy server.
        for (int i = 0; i < 20; i++)
        {
            try
            {
                using var http = new System.Net.Http.HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(2)
                };
                var body = await http.GetStringAsync($"http://127.0.0.1:{port}/static.html");
                if (body.Contains("Encomm Benchmark"))
                {
                    Log(logPath, $"Bench: server verified on port {port}.");
                    return true;
                }
            }
            catch { }
            await Task.Delay(500);
        }
        Log(logPath, $"Bench: server on port {port} never served valid content.");
        return false;
    }

    private static Process? StartServer(string logPath, string siteDir)
    {
        return StartServer(logPath, siteDir, ActivePort);
    }

    private static Process? StartServer(string logPath, string siteDir, int port)
    {
        foreach (var py in new[] { "python", "py" })
        {
            try
            {
                var psi = new ProcessStartInfo(py,
                    $"-m http.server {port} --bind 127.0.0.1 --directory \"{siteDir}\"")
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
        sb.AppendLine($"- Benchmark: {r.Meta.BenchmarkVersion} @ {r.Meta.CommitSha} ({r.Meta.BuildConfig})");
        sb.AppendLine($"- OS: {r.Meta.WindowsVersion} / CPU: {r.Meta.CpuCores} / RAM: {Fmt(r.Meta.PhysicalRamBytes)}");
        sb.AppendLine($"- WebView2: {r.Meta.WebView2Version} / WinAppSDK: {r.Meta.WinAppSdkVersion}");
        sb.AppendLine($"- Started: {r.Meta.StartedUtc:O} / Finished: {r.FinishedUtc:O}");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Valid | Host WS | Host Priv | WV2 Browser | WV2 Renderer | WV2 GPU | WV2 Util | Tree Total | Procs | Tabs L/W/G | Views |");
        sb.AppendLine("|---|:---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---|---|");
        foreach (var s in r.Scenarios)
        {
            var snap = s.Snapshot;
            sb.AppendLine($"| {s.Name} | {(s.ScenarioValid ? "yes" : "NO")} | {Fmt(snap.HostWorkingSetBytes)} | {Fmt(snap.HostPrivateBytes)} | " +
                $"{Fmt(s.BrowserBytes)} | {Fmt(s.RendererBytes)} | {Fmt(s.GpuBytes)} | {Fmt(s.UtilBytes)} | " +
                $"{Fmt(snap.ProcessTreeBytes)} | {snap.ProcessCount} | " +
                $"{snap.LiveTabs}/{snap.WarmTabs}/{snap.GhostTabs} | {s.ViewCount} |");
            foreach (var e in s.ValidationErrors) sb.AppendLine($"  - INVALID: {e}");
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
            foreach (var kv in c.Extra) sb.AppendLine($"- {kv.Key}: {kv.Value}");
            sb.AppendLine();
        }
        foreach (var s in r.Scenarios)
            foreach (var kv in s.Extra) sb.AppendLine($"- [{s.Id}] {kv.Key}: {kv.Value}");
        sb.AppendLine();
        sb.AppendLine("All numbers are measured Windows working-set bytes and stopwatch latencies.");
        sb.AppendLine("INVALID rows must not be used for comparison.");
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

public sealed record BenchMeta(
    string BenchmarkVersion,
    string CommitSha,
    string BuildConfig,
    string WindowsVersion,
    int CpuCores,
    long PhysicalRamBytes,
    string WebView2Version,
    string WinAppSdkVersion,
    DateTimeOffset StartedUtc);

public sealed record BenchScenario(
    string Id,
    string Name,
    string? Notes,
    MemorySnapshot Snapshot,
    int ViewCount,
    long BrowserBytes,
    long RendererBytes,
    long GpuBytes,
    long UtilBytes,
    bool ScenarioValid,
    List<string> ValidationErrors,
    RestoreStats? Restores,
    Dictionary<string, string> Extra);

public sealed record RestoreStats(
    string Name,
    int SampleCount,
    double ReadyMedianMs, double ReadyMinMs, double ReadyMaxMs,
    double NavMedianMs, double NavMinMs, double NavMaxMs,
    double ScrollMedianMs, double ScrollMinMs, double ScrollMaxMs,
    double TotalMedianMs, double TotalMinMs, double TotalMaxMs,
    double TotalMeanMs, double Reserved,
    Dictionary<string, string> Extra);

public sealed class BenchReport
{
    public BenchReport(BenchMeta meta)
    {
        Meta = meta;
        Machine = $"{meta.WindowsVersion} x64/{meta.CpuCores}";
        StartedUtc = meta.StartedUtc;
    }
    public BenchMeta Meta { get; init; }
    // Compat fields (2B readers).
    public string Machine { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset FinishedUtc { get; set; }
    public List<BenchScenario> Scenarios { get; init; } = new();
    public RestoreStats? RestoreCost { get; set; }
    // Bench needs a log path + tab source without DI cycles.
    public string LogPath { get; set; } = "";
    public Func<IEnumerable<TabRecord>> TabSource { get; set; } = Enumerable.Empty<TabRecord>;
}

public sealed record BenchSingleFile(BenchMeta Meta, List<BenchScenario> Samples, RestoreStats? Restores);
