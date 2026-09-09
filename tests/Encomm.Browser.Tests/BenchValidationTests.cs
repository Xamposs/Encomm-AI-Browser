using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Encomm.Browser.App.Services;
using Encomm.Browser.Core.Storage;
using Encomm.Browser.Engine.Abstractions;
using Xunit;

namespace Encomm.Browser.Tests;

/// <summary>
/// Phase 2C item 28: benchmark validation, scenario parsing, deployment
/// consistency, restore-under-load, scroll record propagation.
/// </summary>
public class BenchValidationTests
{
    [Theory]
    [InlineData("--run-bench", "FULL")]
    [InlineData("--run-bench=D", "D")]
    [InlineData("--run-bench=h1", "H1")]
    [InlineData("--run-bench=CREATE", "CREATE")]
    [InlineData("--run-bench=RESTORE", "RESTORE")]
    [InlineData("--run-bench=WEB", "WEB")]
    [InlineData("--run-bench=bogus", "FULL")]
    public void ParseBenchScenario_maps_known_ids(string arg, string expected)
    {
        Assert.Equal(expected, BenchRunner.ParseBenchScenario(new[] { arg }));
    }

    [Fact]
    public void ParseBenchScenario_defaults_to_full()
    {
        Assert.Equal("FULL", BenchRunner.ParseBenchScenario(Array.Empty<string>()));
        Assert.Equal("FULL", BenchRunner.ParseBenchScenario(new[] { "--browser-smoke-test" }));
    }

    [Fact]
    public void ValidateStates_accepts_coherent_live_warm_ghost()
    {
        var live = NewTab(TabRendererStateKind.Live);
        var warm = NewTab(TabRendererStateKind.Warm);
        var ghost = NewTab(TabRendererStateKind.Ghost);
        var views = new Dictionary<Guid, IBrowserView>
        {
            [live.Id] = new StateView(live.Id, ViewLifecycleState.Live),
            [warm.Id] = new StateView(warm.Id, ViewLifecycleState.Warm),
        };
        var errors = BenchRunner.ValidateStates(new[] { live, warm, ghost }, views);
        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateStates_flags_live_without_renderer()
    {
        var live = NewTab(TabRendererStateKind.Live);
        var errors = BenchRunner.ValidateStates(
            new[] { live }, new Dictionary<Guid, IBrowserView>());
        Assert.Contains(errors, e => e.Contains("no renderer"));
    }

    [Fact]
    public void ValidateStates_flags_warm_without_renderer_as_physical_ghost()
    {
        var warm = NewTab(TabRendererStateKind.Warm);
        var errors = BenchRunner.ValidateStates(
            new[] { warm }, new Dictionary<Guid, IBrowserView>());
        Assert.Contains(errors, e => e.Contains("physically Ghost"));
    }

    [Fact]
    public void ValidateStates_flags_warm_with_live_view()
    {
        var warm = NewTab(TabRendererStateKind.Warm);
        var views = new Dictionary<Guid, IBrowserView>
        {
            [warm.Id] = new StateView(warm.Id, ViewLifecycleState.Live),
        };
        var errors = BenchRunner.ValidateStates(new[] { warm }, views);
        Assert.Contains(errors, e => e.Contains("Warm") && e.Contains("Live"));
    }

    [Fact]
    public void ValidateStates_flags_ghost_with_renderer()
    {
        var ghost = NewTab(TabRendererStateKind.Ghost);
        var views = new Dictionary<Guid, IBrowserView>
        {
            [ghost.Id] = new StateView(ghost.Id, ViewLifecycleState.Live),
        };
        var errors = BenchRunner.ValidateStates(new[] { ghost }, views);
        Assert.Contains(errors, e => e.Contains("still has a renderer"));
    }

    [Fact]
    public void ValidateStates_flags_orphan_renderer()
    {
        var errors = BenchRunner.ValidateStates(
            Array.Empty<TabRecord>(),
            new Dictionary<Guid, IBrowserView> { [Guid.NewGuid()] = new StateView(Guid.NewGuid(), ViewLifecycleState.Live) });
        Assert.Contains(errors, e => e.Contains("Orphan"));
    }

    [Fact]
    public async Task Restore_under_many_tabs_restores_one_and_leaves_rest_ghost()
    {
        using var ctx = SharedContext.New();
        TabRecord? target = null;
        for (int i = 0; i < 100; i++)
        {
            var t = ctx.AddGhostTab($"https://example.com/p/{i}");
            if (i == 42) target = t;
        }
        Assert.NotNull(target);

        var ok = await ctx.Runtime.RestoreGhostTabAsync(target!);

        Assert.True(ok);
        Assert.Equal(TabRendererStateKind.Live, ctx.StateOf(target!.Id));
        Assert.True(ctx.Runtime.HasView(target.Id));
        // All others untouched: still Ghost, no renderer.
        var others = ctx.Tabs.Tabs.Where(t => t.Id != target.Id).ToList();
        Assert.Equal(99, others.Count);
        Assert.All(others, t => Assert.Equal(TabRendererStateKind.Ghost, t.RendererState));
        foreach (var t in others) Assert.False(ctx.Runtime.HasView(t.Id));
    }

    [Fact]
    public async Task Restore_propagates_record_scroll_to_set_scroll()
    {
        using var ctx = SharedContext.New();
        var tab = ctx.AddGhostTab("https://example.com/long", scrollX: 0, scrollY: 2000);

        Assert.True(await ctx.Runtime.RestoreGhostTabAsync(tab));

        Assert.Equal((0.0, 2000.0), ctx.ViewFor(tab.Id).LastSetScroll);
    }

    [Fact]
    public void Deployment_csproj_matches_documented_model()
    {
        // Single source of truth lives in the csproj; docs must match it:
        // framework-dependent WinAppSDK (false) + self-contained .NET (true)
        // + explicit bootstrap (false).
        var csproj = FindAppCsproj();
        var doc = XDocument.Load(csproj);
        string? Prop(string name) =>
            doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        Assert.Equal("false", Prop("WindowsAppSDKSelfContained"));
        Assert.Equal("true", Prop("SelfContained"));
        Assert.Equal("false", Prop("WindowsAppSdkBootstrapInitialize"));
    }

    // -- Helpers --------------------------------------------------------

    private static TabRecord NewTab(TabRendererStateKind state) => new(
        Guid.NewGuid(), Guid.NewGuid(), "https://x", "X", null,
        state, TabLogicalStateKind.Background,
        false, false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null);

    private static string FindAppCsproj()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Encomm.Browser.App", "Encomm.Browser.App.csproj");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("App csproj not found from test binary.");
    }

    private sealed class StateView : IBrowserView
    {
        public StateView(Guid id, ViewLifecycleState state) { Id = id; State = state; }
        public Guid Id { get; }
        public ViewLifecycleState State { get; }
        public string CurrentUrl => "";
        public string CurrentTitle => "";
        public string? CurrentFaviconUrl => null;
        public bool CanGoBack => false;
        public bool CanGoForward => false;
        public bool IsLoading => false;
        public bool IsDocumentPlayingAudio => false;
        public object HostElement => new object();
#pragma warning disable CS0067
        public event EventHandler<NavigationStartingEventArgs>? NavigationStarting;
        public event EventHandler<NavigationCompletedEventArgs>? NavigationCompleted;
        public event EventHandler<TitleChangedEventArgs>? TitleChanged;
        public event EventHandler<FaviconChangedEventArgs>? FaviconChanged;
        public event EventHandler<LoadingStateEventArgs>? LoadingStateChanged;
        public event EventHandler<AudioEventArgs>? AudioStateChanged;
        public event EventHandler<DownloadEventArgs>? DownloadRequested;
        public event EventHandler<PermissionRequestEventArgs>? PermissionRequested;
        public event EventHandler<NewWindowRequestEventArgs>? NewWindowRequested;
        public event EventHandler<ResourceBlockedEventArgs>? ResourceBlocked;
        public event EventHandler<RenderErrorEventArgs>? RenderError;
        public event EventHandler<AcceleratorKeyEventArgs>? AcceleratorKeyPressed;
#pragma warning restore CS0067
        public Task<NavigationResult> NavigateAsync(string url, CancellationToken ct = default) => Task.FromResult(new NavigationResult(true));
        public Task<string?> ResolveUrlAsync(string userInput, CancellationToken ct = default) => Task.FromResult<string?>(userInput);
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task GoBackAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task GoForwardAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<(double X, double Y)> GetScrollAsync(CancellationToken ct = default) => Task.FromResult((0.0, 0.0));
        public Task SetScrollAsync(double x, double y, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> SuspendAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ResumeAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> HasUnsavedFormStateAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> GhostAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<PageContext> ExtractPageContextAsync(CancellationToken ct = default) => Task.FromResult(new PageContext("", "", null, null, null));
        public Task<byte[]?> CapturePreviewAsync(int maxWidth, int maxHeight, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task OpenDevToolsAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SetZoomAsync(double zoom, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
