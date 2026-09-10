using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Encomm.Browser.AI;
using Encomm.Browser.App.Services;
using Encomm.Browser.App.ViewModels;
using Encomm.Browser.Core;
using Encomm.Browser.Core.Storage;
using Encomm.Browser.Engine.Abstractions;
using Encomm.Browser.Settings;
using Encomm.Browser.Shield;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encomm.Browser.Tests;

/// <summary>
/// Shared fake-engine harness for runtime semantics / invariant tests.
/// No WebView2 required: BrowserRuntime.CreateEngineForTests injects
/// the FakeEngine, and InlineDispatcher executes UI work inline.
/// </summary>
internal sealed class SharedContext : IDisposable
{
    private readonly string _tmp;
    public readonly TabService Tabs;
    public readonly BrowserRuntime Runtime;
    public readonly FakeEngine Engine;
    public readonly BrowserPersistenceService Persistence;
    public readonly Guid WorkspaceId = Guid.NewGuid();
    private readonly SqliteStore _store;

    public static SharedContext New() => new();

    private SharedContext()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "encomm-sem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        var paths = new BrowserPaths(_tmp);
        _store = new SqliteStore(paths.DatabaseFile);
        Persistence = new BrowserPersistenceService(_store, NullLogger<BrowserPersistenceService>.Instance);
        var ui = new InlineDispatcher();
        Tabs = new TabService(Persistence, NullLogger<TabService>.Instance, ui);
        var blocker = new RequestBlocker(new FilterRuleProvider());
        var factory = new WebView2EngineFactory(blocker, NullLogger<BrowserRuntime>.Instance);
        Runtime = new BrowserRuntime(blocker, NullLogger<BrowserRuntime>.Instance, Tabs, factory, ui);
        Engine = new FakeEngine();
        Runtime.CreateEngineForTests = _ => Task.FromResult<IBrowserEngine>(Engine);
    }

    public TabRecord AddGhostTab(string url, double scrollX = 0, double scrollY = 0)
    {
        var tab = new TabRecord(Guid.NewGuid(), WorkspaceId, url, "T", null,
            TabRendererStateKind.Ghost, TabLogicalStateKind.Background,
            false, false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null,
            ScrollX: scrollX, ScrollY: scrollY);
        Tabs.Tabs.Add(tab);
        Engine.AddView(new FakeView(tab.Id));
        return tab;
    }

    public TabRecord AddLiveTab(string url)
    {
        var tab = new TabRecord(Guid.NewGuid(), WorkspaceId, url, "T", null,
            TabRendererStateKind.Live, TabLogicalStateKind.Background,
            false, false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null);
        Tabs.Tabs.Add(tab);
        Engine.AddView(new FakeView(tab.Id));
        var created = Runtime.GetOrCreateAsync(tab).GetAwaiter().GetResult();
        if (created is null) throw new InvalidOperationException("Fake engine returned null view.");
        return tab;
    }

    public void FailNext(Guid id) => Engine.Views[id].CompleteWithSuccess = false;
    public FakeView ViewFor(Guid id) => Engine.Views[id];

    /// <summary>Build the real MainViewModel over the fake engine for
    /// canonical close/select path tests (no WebView2, no XAML).</summary>
    public MainViewModel CreateViewModel()
    {
        var secrets = new FakeSecretStore();
        var settings = new SettingsService(new SettingsStore(_store, secrets));
        var workspaces = new WorkspaceService(Persistence, NullLogger<WorkspaceService>.Instance);
        var ai = new AIService(new FakeRouter(), secrets, Runtime, NullLogger<AIService>.Instance);
        return new MainViewModel(workspaces, Tabs, Runtime, ai, settings,
            NullLogger<MainViewModel>.Instance);
    }

    public TabRendererStateKind StateOf(Guid id)
    {
        foreach (var t in Tabs.Tabs) if (t.Id == id) return t.RendererState;
        throw new InvalidOperationException("Tab not in collection.");
    }

    public string UrlOf(Guid id)
    {
        foreach (var t in Tabs.Tabs) if (t.Id == id) return t.Url;
        throw new InvalidOperationException("Tab not in collection.");
    }

    public void Dispose()
    {
        try { _store.Dispose(); } catch { }
        try { Directory.Delete(_tmp, true); } catch { }
    }
}

internal sealed class InlineDispatcher : IUiDispatcher
{
    public bool HasThreadAccess => true;
    public void Post(Action action) => action();
    public Task<T> RunAsync<T>(Func<Task<T>> func) => func();
}

internal sealed class FakeSecretStore : ISecretStore
{
    public void SetSecret(string name, string value) { }
    public string? GetSecret(string name) => null;
    public void DeleteSecret(string name) { }
}

internal sealed class FakeRouter : IModelRouter
{
    public bool IsConfigured => false;
    public IChatProvider Chat => throw new NotSupportedException();
    public IEmbeddingProvider? Embeddings => null;
    public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(false);
}

internal sealed class FakeEngine : IBrowserEngine
{
    public readonly Dictionary<Guid, FakeView> Views = new();
    public string EngineId => "fake";
    public string? RuntimeVersion => "0.0.0-test";
    public void AddView(FakeView v) => Views[v.Id] = v;
    public Task<IBrowserView> CreateViewAsync(Guid tabId, CancellationToken ct = default)
    {
        if (!Views.TryGetValue(tabId, out var v))
        {
            v = new FakeView(tabId);
            Views[tabId] = v;
        }
        return Task.FromResult<IBrowserView>(v);
    }
    public IReadOnlyList<WebViewProcessInfo> GetWebViewProcessInfos()
        => Array.Empty<WebViewProcessInfo>();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeView : IBrowserView
{
    public FakeView(Guid id) { Id = id; }
    public Guid Id { get; }
    public ViewLifecycleState State { get; private set; } = ViewLifecycleState.Live;
    public readonly List<string> CallLog = new();
    public bool CompleteWithSuccess = true;
    public bool NeverComplete;
    public bool AcceptNavigation = true;
    public bool ThrowOnScroll;

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

    public Task<NavigationResult> NavigateAsync(string url, CancellationToken ct = default)
    {
        if (!AcceptNavigation) return Task.FromResult(new NavigationResult(false, "blocked by test"));
        if (!NeverComplete)
        {
            NavigationCompleted?.Invoke(this, new NavigationCompletedEventArgs
            {
                Url = url,
                HttpStatus = CompleteWithSuccess ? 200 : 0,
                Success = CompleteWithSuccess,
                ErrorMessage = CompleteWithSuccess ? null : "ConnectionAborted"
            });
        }
        return Task.FromResult(new NavigationResult(true));
    }

    public Task<string?> ResolveUrlAsync(string userInput, CancellationToken ct = default)
        => Task.FromResult<string?>(userInput);
    public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task GoBackAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task GoForwardAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<(double X, double Y)> GetScrollAsync(CancellationToken ct = default)
    {
        CallLog.Add("scroll");
        if (ThrowOnScroll) throw new InvalidOperationException("scroll unavailable");
        return Task.FromResult((0.0, 42.0));
    }
    public Task SetScrollAsync(double x, double y, CancellationToken ct = default)
    {
        LastSetScroll = (x, y);
        return Task.CompletedTask;
    }
    public (double X, double Y) LastSetScroll { get; private set; }
    public Task<bool> SuspendAsync(CancellationToken ct = default)
    {
        CallLog.Add("suspend");
        State = ViewLifecycleState.Warm;
        return Task.FromResult(true);
    }
    public Task<bool> ResumeAsync(CancellationToken ct = default)
    {
        State = ViewLifecycleState.Live;
        return Task.FromResult(true);
    }
    public Task<bool> HasUnsavedFormStateAsync(CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> GhostAsync(CancellationToken ct = default)
    {
        State = ViewLifecycleState.Ghost;
        return Task.FromResult(true);
    }
    public Task<PageContext> ExtractPageContextAsync(CancellationToken ct = default)
    {
        CallLog.Add("context");
        return Task.FromResult(new PageContext("", "", null, null, null));
    }
    public Task<byte[]?> CapturePreviewAsync(int maxWidth, int maxHeight, CancellationToken ct = default)
        => Task.FromResult<byte[]?>(null);
    public Task OpenDevToolsAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SetZoomAsync(double zoom, CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
