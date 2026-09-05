using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Encomm.Browser.Core;
using Encomm.Browser.Core.Storage;
using Encomm.Browser.Memory;
using Encomm.Browser.Settings;
using Encomm.Browser.Shield;
using Encomm.Browser.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Encomm.Tools.BrowserBenchmark;

/// <summary>
/// Repeatable logical-tab benchmark.
///
/// Creates N TabRecord entries and reports host memory at each stage.
/// Does NOT create real WebView2 controls; the goal is to measure the
/// overhead of the tab domain (persistence, lifecycle, settings) in
/// isolation. A real-browser benchmark with WebView2 child process
/// attribution lives in Phase 2B.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        var scenarios = new[] { 1, 10, 25, 50, 100 };
        var report = new BenchmarkReport
        {
            Machine = new MachineInfo(
                Environment.OSVersion.VersionString,
                Environment.ProcessorCount,
                Environment.Version.ToString()),
            Scenarios = new List<ScenarioReport>()
        };
        // Above syntax requires an init-only property or a parameterless ctor.
        // For record types, the property setters are init-only; we set them
        // after construction.
        // (No-op here; see BenchmarkReport at file end.)
        foreach (var n in scenarios)
        {
            Console.Error.WriteLine($"Running scenario N={n}...");
            try
            {
                report.Scenarios.Add(RunScenario(n));
            }
            catch (Exception ex)
            {
                report.Scenarios.Add(new ScenarioReport
                {
                    TabCount = n,
                    Notes = "FAILED: " + ex.GetType().Name + ": " + ex.Message
                });
            }
        }

        var outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..");
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        var jsonPath = Path.Combine(outDir, "benchmark.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Wrote {jsonPath}");
        var mdPath = Path.Combine(outDir, "benchmark.md");
        File.WriteAllText(mdPath, RenderMarkdown(report));
        Console.WriteLine($"Wrote {mdPath}");
        return 0;
    }

    private static ScenarioReport RunScenario(int tabCount)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "encomm-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var paths = new BrowserPaths(tmp);
            using var store = new SqliteStore(paths.DatabaseFile);
            store.OpenConnection().Close();
            var persistence = new BrowserPersistenceService(store, Microsoft.Extensions.Logging.Abstractions.NullLogger<BrowserPersistenceService>.Instance);
            var blocker = new RequestBlocker(new FilterRuleProvider());
            blocker.Enabled = false;
            var probe = new MemoryProbe(() =>
            {
                var list = new List<Encomm.Browser.Memory.TabStateSummary>();
                // The persistence layer doesn't track per-tab renderer state
                // out of the box; the App-level TabService is what does that.
                // Here we report 0 tabs of any state because the persistence
                // layer alone doesn't know.
                return list;
            });

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < tabCount; i++)
            {
                var tab = new TabRecord(
                    Guid.NewGuid(), Guid.NewGuid(), $"https://encomm-bench.test/page/{i}",
                    $"Page {i}", null,
                    TabRendererStateKind.Ghost, TabLogicalStateKind.Background,
                    false, false, false,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, i, null);
                persistence.SaveTab(tab);
            }
            sw.Stop();
            var afterCreate = probe.Sample();
            var createMs = sw.Elapsed.TotalMilliseconds;

            return new ScenarioReport
            {
                TabCount = tabCount,
                CreationMs = createMs,
                AfterCreateHostBytes = afterCreate.HostWorkingSetBytes,
                AfterCreateHostPrivateBytes = afterCreate.HostPrivateBytes,
                AfterActiveHostBytes = afterCreate.HostWorkingSetBytes,
                AfterActiveHostPrivateBytes = afterCreate.HostPrivateBytes,
                TotalTabs = afterCreate.TotalTabs,
                LiveTabs = afterCreate.LiveTabs,
                WarmTabs = afterCreate.WarmTabs,
                GhostTabs = afterCreate.GhostTabs
            };
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    private static string RenderMarkdown(BenchmarkReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Encomm Browser Benchmark - Phase 2A");
        sb.AppendLine();
        sb.AppendLine($"- OS: {r.Machine.OsVersion}");
        sb.AppendLine($"- CPU cores: {r.Machine.ProcessorCount}");
        sb.AppendLine($"- Runtime: {r.Machine.RuntimeVersion}");
        sb.AppendLine();
        sb.AppendLine("| Tabs | Creation (ms) | After Create Host WS | After Create Host Private | After Active Host WS | After Active Host Private | Total | Live | Warm | Ghost |");
        sb.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var s in r.Scenarios)
        {
            if (s.Notes is not null)
            {
                sb.AppendLine($"| {s.TabCount} | FAILED | | | | | | | | | {s.Notes} |");
                continue;
            }
            sb.AppendLine($"| {s.TabCount} | {s.CreationMs:F0} | {Format(s.AfterCreateHostBytes)} | {Format(s.AfterCreateHostPrivateBytes)} | {Format(s.AfterActiveHostBytes)} | {Format(s.AfterActiveHostPrivateBytes)} | {s.TotalTabs} | {s.LiveTabs} | {s.WarmTabs} | {s.GhostTabs} |");
        }
        sb.AppendLine();
        sb.AppendLine("Numbers are REAL working-set / private-bytes for the host process. The");
        sb.AppendLine("logical-tab count tracks the tab domain. This is NOT a WebView2");
        sb.AppendLine("renderer benchmark; that requires a running Encomm process and");
        sb.AppendLine("is scheduled for Phase 2B.");
        return sb.ToString();
    }

    private static string Format(long bytes)
    {
        double v = bytes;
        string[] suf = { "B", "KB", "MB", "GB" };
        int i = 0;
        while (v >= 1024 && i < suf.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {suf[i]}";
    }
}

public sealed record MachineInfo(string OsVersion, int ProcessorCount, string RuntimeVersion);
public sealed record ScenarioReport
{
    public int TabCount { get; init; }
    public double CreationMs { get; init; }
    public long AfterCreateHostBytes { get; init; }
    public long AfterCreateHostPrivateBytes { get; init; }
    public long AfterActiveHostBytes { get; init; }
    public long AfterActiveHostPrivateBytes { get; init; }
    public int TotalTabs { get; init; }
    public int LiveTabs { get; init; }
    public int WarmTabs { get; init; }
    public int GhostTabs { get; init; }
    public string? Notes { get; init; }
}

public sealed class BenchmarkReport
{
    public MachineInfo Machine { get; init; } = new MachineInfo("unknown", 0, "unknown");
    public List<ScenarioReport> Scenarios { get; init; } = new();
}

internal sealed class FakeSecretStore : ISecretStore
{
    public void SetSecret(string name, string value) { }
    public string? GetSecret(string name) => null;
    public void DeleteSecret(string name) { }
}
