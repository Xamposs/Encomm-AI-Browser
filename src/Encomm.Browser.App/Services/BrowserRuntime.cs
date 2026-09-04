using Microsoft.Extensions.Logging;
using Encomm.Browser.Engine.Abstractions;
using Encomm.Browser.Core.Storage;
using Encomm.Browser.Shield;

namespace Encomm.Browser.App.Services;

/// <summary>
/// Authoritative owner of the WebView2 runtime.
///
/// Responsibilities:
///   * Lazily create the single shared CoreWebView2Environment.
///   * Own the per-tab renderer dictionary (renderer-instance lifecycle).
///   * Create, suspend, resume, and ghost renderers for logical tabs.
///   * Drive real renderer-level operations (TrySuspendAsync / Close / Dispose)
///     when lifecycle state changes — NEVER just toggle an enum.
///
/// `TabService` is responsible for logical tab state (URL, title, pinned, ...).
/// `BrowserRuntime` is responsible for renderer state.
/// `TabLifecycleManager` is responsible for deciding WHEN to transition.
/// </summary>
public sealed class BrowserRuntime : IAsyncDisposable
{
    private readonly IRequestBlocker _blocker;
    private readonly ILogger<BrowserRuntime> _log;
    private readonly TabService _tabService;
    private readonly Func<BrowserRuntime, Task<IBrowserEngine>> _engineFactory;
    private readonly object _initGate = new();
    private readonly Dictionary<Guid, IBrowserView> _views = new();
    private readonly object _viewsGate = new();
    private Task<IBrowserEngine>? _initTask;
    private bool _disposed;

    public BrowserRuntime(
        IRequestBlocker blocker,
        ILogger<BrowserRuntime> log,
        TabService tabService,
        Func<BrowserRuntime, Task<IBrowserEngine>> engineFactory)
    {
        _blocker = blocker;
        _log = log;
        _tabService = tabService;
        _engineFactory = engineFactory;
    }

    /// <summary>
    /// Idempotent. Multiple callers see the same Task; the engine is created once.
    /// </summary>
    public Task<IBrowserEngine> InitializeAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BrowserRuntime));
        lock (_initGate)
        {
            if (_initTask is null)
            {
                _initTask = _engineFactory(this);
            }
        }
        return _initTask;
    }

    public bool IsReady => _initTask is { IsCompletedSuccessfully: true };

    public IReadOnlyDictionary<Guid, IBrowserView> Views
    {
        get { lock (_viewsGate) return new Dictionary<Guid, IBrowserView>(_views); }
    }

    /// <summary>Get a live view for the tab, creating it if needed. Lazy.</summary>
    public async Task<IBrowserView> GetOrCreateAsync(TabRecord tab, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BrowserRuntime));
        if (tab is null) throw new ArgumentNullException(nameof(tab));

        lock (_viewsGate)
        {
            if (_views.TryGetValue(tab.Id, out var existing)) return existing;
        }
        var engine = await InitializeAsync(ct).ConfigureAwait(false);
        var view = await engine.CreateViewAsync(tab.Id, ct).ConfigureAwait(false);
        if (view is null) throw new InvalidOperationException("Engine returned null view");
        // Bridge the renderer's events to the logical tab so the tab record
        // gets its URL/title/favicon kept up to date.
        AttachViewEventBridge(tab, view);
        lock (_viewsGate) _views[tab.Id] = view;
        _log.LogInformation("Renderer created for tab {Id} url={Url}", tab.Id, tab.Url);
        return view;
    }

    private void AttachViewEventBridge(TabRecord tab, IBrowserView view)
    {
        view.TitleChanged += (_, e) =>
        {
            // TabRecord's mutability is restricted; the canonical way to
            // change fields is via `TabService`. We dispatch to the UI
            // thread so the property change happens on the dispatcher.
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() =>
            {
                _tabService.MutateTitle(tab.Id, e.Title);
            });
        };
        view.NavigationCompleted += (_, e) =>
        {
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() =>
            {
                _tabService.MutateNavigationCompleted(tab.Id, e.Url);
            });
        };
    }

    public bool HasView(Guid tabId)
    {
        lock (_viewsGate) return _views.ContainsKey(tabId);
    }

    /// <summary>
    /// Suspend the renderer for the tab (Warm). Returns true on success.
    /// No-op if no view exists. No-op if the view is already Live (it cannot
    /// suspend a Live view without first becoming a candidate, which is the
    /// caller's responsibility to decide).
    /// </summary>
    public async Task<bool> SuspendAsync(Guid tabId, CancellationToken ct = default)
    {
        IBrowserView? view;
        lock (_viewsGate)
        {
            _views.TryGetValue(tabId, out view);
        }
        if (view is null) return false;
        try
        {
            await view.SuspendAsync(ct).ConfigureAwait(false);
            return view.State == ViewLifecycleState.Warm;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Suspend failed for tab {Id}", tabId);
            return false;
        }
    }

    /// <summary>
    /// Destroy the renderer for the tab (Ghost). Removes the view from the
    /// registry and releases native resources. Returns true if a renderer was
    /// actually removed.
    /// </summary>
    public async Task<bool> GhostAsync(Guid tabId, CancellationToken ct = default)
    {
        IBrowserView? view;
        lock (_viewsGate)
        {
            if (!_views.TryGetValue(tabId, out view)) return false;
            _views.Remove(tabId);
        }
        try
        {
            await view.GhostAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ghost dispose raised for tab {Id}", tabId);
        }
        try { await view.DisposeAsync().ConfigureAwait(false); } catch { }
        _log.LogInformation("Renderer ghosted for tab {Id}", tabId);
        return true;
    }

    /// <summary>
    /// Bring a Ghost or Warm renderer back to Live, recreating it if needed.
    /// For Ghost: creates a fresh view and re-navigates to the tab's stored URL.
    /// For Warm: asks the engine to resume.
    /// Returns the live view, or null if WakeAsync failed.
    /// </summary>
    public async Task<IBrowserView?> WakeAsync(TabRecord tab, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BrowserRuntime));
        if (tab is null) throw new ArgumentNullException(nameof(tab));
        IBrowserView? existing;
        lock (_viewsGate) _views.TryGetValue(tab.Id, out existing);
        if (existing is not null)
        {
            await existing.WakeAsync(ct).ConfigureAwait(false);
            return existing;
        }
        // No view at all: create a new one. GetOrCreateAsync handles it.
        return await GetOrCreateAsync(tab, ct).ConfigureAwait(false);
    }

    /// <summary>Ghost all currently-tracked renderers. Used during workspace switch / shutdown.</summary>
    public async Task GhostAllAsync(CancellationToken ct = default)
    {
        List<IBrowserView> toDispose;
        lock (_viewsGate)
        {
            toDispose = _views.Values.ToList();
            _views.Clear();
        }
        foreach (var v in toDispose)
        {
            try { await v.GhostAsync(ct).ConfigureAwait(false); } catch { }
            try { await v.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _log.LogInformation("All {Count} renderers ghosted.", toDispose.Count);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await GhostAllAsync().ConfigureAwait(false);
    }
}