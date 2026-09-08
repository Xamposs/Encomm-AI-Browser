using System;
using System.Collections.Generic;
using Encomm.Browser.Core.Storage;
using Encomm.Browser.Developer;
using Encomm.Browser.Engine.Abstractions;
using Xunit;

namespace Encomm.Browser.Tests;

public class StateDivergenceTests
{
    [Fact]
    public void Reports_no_issues_when_logical_and_actual_agree()
    {
        var (tabs, views) = MakeState();
        var div = new StateDivergenceInspector(tabs, views);
        var report = div.Inspect();
        Assert.Empty(report.Errors);
        Assert.Empty(report.Warnings);
        Assert.False(report.HasIssues);
    }

    [Fact]
    public void Reports_error_when_logical_Live_but_no_actual_renderer()
    {
        var (tabs, views) = MakeState();
        var live = NewLiveTab();
        tabs.Add(live);
        // No view added for this tab.
        var div = new StateDivergenceInspector(tabs, views);
        var report = div.Inspect();
        Assert.True(report.HasIssues);
        Assert.Contains(report.Errors, e => e.Contains("Live") && e.Contains("no actual renderer"));
    }

    [Fact]
    public void Reports_error_when_logical_Ghost_but_actual_renderer_exists()
    {
        var (tabs, views) = MakeState();
        var ghost = NewGhostTab();
        tabs.Add(ghost);
        views[ghost.Id] = new MockView(ghost.Id, ViewLifecycleState.Live);
        var div = new StateDivergenceInspector(tabs, views);
        var report = div.Inspect();
        Assert.True(report.HasIssues);
        Assert.Contains(report.Errors, e => e.Contains("Ghost") && e.Contains("still exists"));
    }

    [Fact]
    public void Reports_warning_when_logical_state_and_actual_view_state_mismatch()
    {
        var (tabs, views) = MakeState();
        var live = NewLiveTab();
        tabs.Add(live);
        // Logical=Live but actual view state=Warm.
        views[live.Id] = new MockView(live.Id, ViewLifecycleState.Warm);

        var div = new StateDivergenceInspector(tabs, views);
        var report = div.Inspect();
        Assert.True(report.HasIssues);
        Assert.Contains(report.Warnings, w => w.Contains("Live") && w.Contains("Warm"));
    }

    [Fact]
    public void Formatter_produces_human_readable_output()
    {
        var report = new StateDivergenceReport
        {
            LogicalTabs = 5,
            Live = 2,
            Warm = 1,
            Ghost = 2,
            ActualRenderers = 4,
            Warnings = { "warn-1" },
            Errors = { "err-1", "err-2" }
        };
        var s = StateDivergenceFormatter.Format(report);
        Assert.Contains("logical tabs: 5", s);
        Assert.Contains("actual renderers: 4", s);
        Assert.Contains("WARNINGS (1)", s);
        Assert.Contains("ERRORS (2)", s);
    }

    private static (List<TabRecord>, Dictionary<Guid, IBrowserView>) MakeState()
    {
        return (new List<TabRecord>(), new Dictionary<Guid, IBrowserView>());
    }

    private static TabRecord NewLiveTab() => new(
        Guid.NewGuid(), Guid.NewGuid(), "https://x", "X", null,
        TabRendererStateKind.Live, TabLogicalStateKind.Active,
        false, false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null);

    private static TabRecord NewGhostTab() => new(
        Guid.NewGuid(), Guid.NewGuid(), "https://x", "X", null,
        TabRendererStateKind.Ghost, TabLogicalStateKind.Background,
        false, false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null);

    private sealed class MockView : IBrowserView
    {
        public MockView(Guid id, ViewLifecycleState state) { Id = id; State = state; }
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
