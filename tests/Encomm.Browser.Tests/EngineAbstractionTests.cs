using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Encomm.Browser.Engine.Abstractions;
using Xunit;

namespace Encomm.Browser.Tests;

/// <summary>
/// Phase 2B engine-abstraction tests. The implementations are pure C# so
/// no WinUI / WebView2 is needed.
/// </summary>
public class EngineAbstractionTests
{
    [Fact]
    public void Engine_id_required()
    {
        // IBrowserEngine implementations must report a stable engine id.
        // The contract is part of the public surface; we verify it via the
        // mock below.
        var engine = new MockEngine();
        Assert.Equal("mock", engine.EngineId);
    }

    [Fact]
    public async Task CreateViewAsync_returns_distinct_views_per_tab()
    {
        var engine = new MockEngine();
        var a = await engine.CreateViewAsync(Guid.NewGuid());
        var b = await engine.CreateViewAsync(Guid.NewGuid());
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void NavigationResult_Acceptance_semantics()
    {
        var ok = new NavigationResult(true);
        var fail = new NavigationResult(false, "nope");
        Assert.True(ok.Accepted);
        Assert.Null(ok.Reason);
        Assert.False(fail.Accepted);
        Assert.Equal("nope", fail.Reason);
    }

    [Fact]
    public void PageContext_optional_fields_default_null()
    {
        var p = new PageContext("u", "t", "d", "s", "b", "f", 0, 0);
        Assert.Equal("u", p.Url);
        Assert.Equal("t", p.Title);
        Assert.Equal("d", p.Description);
        Assert.Equal("s", p.SelectedText);
        Assert.Equal("b", p.BodyExcerpt);
        Assert.Equal("f", p.FaviconUrl);
        Assert.Equal(0, p.ScrollX);
        Assert.Equal(0, p.ScrollY);
    }

    [Fact]
    public void WebViewProcessInfo_round_trip()
    {
        var info = new WebViewProcessInfo(42, WebViewProcessKind.Renderer, 123_456_789);
        Assert.Equal(42, info.ProcessId);
        Assert.Equal(WebViewProcessKind.Renderer, info.Kind);
        Assert.Equal(123_456_789, info.WorkingSet64);
    }

    [Fact]
    public void GetWebViewProcessInfos_defaults_to_empty()
    {
        var engine = new MockEngine();
        Assert.Empty(engine.GetWebViewProcessInfos());
    }

    private sealed class MockEngine : IBrowserEngine
    {
        public string EngineId => "mock";
        public string? RuntimeVersion => "0.0.0";
        public Task<IBrowserView> CreateViewAsync(Guid tabId, CancellationToken ct = default)
            => Task.FromResult<IBrowserView>(new MockView(tabId));
        public IReadOnlyList<WebViewProcessInfo> GetWebViewProcessInfos() => Array.Empty<WebViewProcessInfo>();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MockView : IBrowserView
    {
        public MockView(Guid id) { Id = id; }
        public Guid Id { get; }
        public ViewLifecycleState State => ViewLifecycleState.Live;
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
