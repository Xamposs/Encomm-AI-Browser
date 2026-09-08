using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encomm.Browser.Engine.Abstractions;
using Encomm.Browser.Core.Storage;
using Encomm.Browser.Shield;

namespace Encomm.Browser.App.Services;

/// <summary>
/// Authoritative owner of the WebView2 runtime.
///
/// Responsibilities:
///   * Lazily create the single shared engine (exactly once).
///   * Own the per-tab renderer dictionary (renderer-instance lifecycle).
///   * Create, suspend, resume, and ghost renderers for logical tabs.
///   * Drive real renderer-level operations when lifecycle state changes.
///   * Marshal every WinUI-touching call onto the application UI thread
///     via the injected <see cref="IUiDispatcher"/>.
///
/// `TabService` owns logical tab state. `BrowserRuntime` owns renderer
/// state. `TabLifecycleManager` decides WHEN to transition.
/// </summary>
public class BrowserRuntime : IAsyncDisposable
{
    private readonly IRequestBlocker _blocker;
    private readonly ILogger<BrowserRuntime> _log;
    private readonly TabService _tabService;
    private readonly WebView2EngineFactory _engineFactory;
    private IUiDispatcher _ui;
    private readonly object _initGate = new();
    private readonly Dictionary<Guid, IBrowserView> _views = new();
    private readonly object _viewsGate = new();
    private Task<IBrowserEngine>? _initTask;
    private bool _disposed;

    /// <summary>
    /// Optional permission prompt. Set by the UI layer (which owns a
    /// XamlRoot). Return true to Allow, false to Deny. When null, all
    /// sensitive permission requests are denied and logged.
    /// </summary>
    public Func<PermissionKind, string, Task<bool>>? PermissionPromptAsync { get; set; }

    /// <summary>
    /// Optional download prompt. Set by the UI layer. The handler receives
    /// the suggested file name and download URL and returns the accepted
    /// local path, or null to cancel.
    /// </summary>
    public Func<string, string, Task<string?>>? DownloadPromptAsync { get; set; }

    /// <summary>
    /// Optional new-window handler. Set by the UI layer. Receives the URL
    /// and whether the request was user-initiated. The default opens
    /// user-initiated requests as background tabs and declines the rest.
    /// </summary>
    public Func<string, bool, Task>? NewWindowHandlerAsync { get; set; }

    /// <summary>
    /// Optional keyboard-accelerator handler. Set by the UI layer.
    /// Invoked synchronously on the WebView2 input thread when focus is
    /// inside page content (where XAML accelerators never fire). Return
    /// true to mark the key handled and suppress WebView2 defaults.
    /// Implementations must not block; marshal to the UI thread.
    /// </summary>
    public Func<AcceleratorKeyEventArgs, bool>? AcceleratorHandler { get; set; }

    /// <summary>Last Ghost-restore total latency, for diagnostics.</summary>
    public TimeSpan LastRestoreLatency { get; private set; }

    /// <summary>Last suspend / resume latency, for diagnostics.</summary>
    public TimeSpan LastSuspendLatency { get; private set; }
    public TimeSpan LastResumeLatency { get; private set; }

    /// <summary>
    /// When the total process-tree memory exceeds this threshold,
    /// the lifecycle treats unprotected tabs as Ghost candidates early
    /// (Adaptive preset). 0 disables the check.
    /// </summary>
    public long MemoryPressureThresholdBytes { get; set; } = 0;

    public BrowserRuntime(
        IRequestBlocker blocker,
        ILogger<BrowserRuntime> log,
        TabService tabService,
        WebView2EngineFactory engineFactory,
        IUiDispatcher ui)
    {
        _blocker = blocker;
        _log = log;
        _tabService = tabService;
        _engineFactory = engineFactory;
        _ui = ui;
    }

    public IUiDispatcher Ui => _ui;

    public void AttachUiDispatcher(IUiDispatcher dispatcher)
    {
        if (dispatcher is null) throw new ArgumentNullException(nameof(dispatcher));
        _ui = dispatcher;
    }

    public Task<IBrowserEngine> InitializeAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BrowserRuntime));
        lock (_initGate)
        {
            if (_initTask is null)
            {
                _initTask = _engineFactory.CreateAsync(this, ct);
            }
        }
        return _initTask;
    }

    public bool IsReady => _initTask is { IsCompletedSuccessfully: true };

    public virtual IReadOnlyDictionary<Guid, IBrowserView> Views
    {
        get { lock (_viewsGate) return new Dictionary<Guid, IBrowserView>(_views); }
    }

    public virtual IReadOnlyList<WebViewProcessInfo> GetWebViewProcessInfos()
    {
        if (_initTask is null || !_initTask.IsCompletedSuccessfully) return Array.Empty<WebViewProcessInfo>();
        var engine = _initTask.Result;
        return engine.GetWebViewProcessInfos();
    }

    /// <summary>
    /// Get or create a fully-initialized live renderer for the tab.
    /// The engine adapter initializes the control; this method only
    /// registers the view and wires the event bridge. WinUI control
    /// creation happens on the UI thread.
    /// </summary>
    public async Task<IBrowserView?> GetOrCreateAsync(TabRecord tab, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BrowserRuntime));
        if (tab is null) throw new ArgumentNullException(nameof(tab));

        lock (_viewsGate)
        {
            if (_views.TryGetValue(tab.Id, out var existing)) return existing;
        }
        try
        {
            var engine = await InitializeAsync(ct).ConfigureAwait(false);
            // Engine adapter implementations create WinUI controls, which
            // require the UI thread. Marshal the whole creation there.
            var view = await _ui.RunAsync(() => engine.CreateViewAsync(tab.Id, ct)).ConfigureAwait(false);
            if (view is null) throw new InvalidOperationException("Engine returned null view");
            AttachViewEventBridge(tab, view);
            lock (_viewsGate) _views[tab.Id] = view;
            _log.LogInformation("Renderer created for tab {Id} url={Url}", tab.Id, tab.Url);
            return view;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to create renderer for tab {Id}", tab.Id);
            return null;
        }
    }

    private void AttachViewEventBridge(TabRecord tab, IBrowserView view)
    {
        view.TitleChanged += (_, e) => _ui.Post(() => _tabService.TryMutateTitle(tab.Id, e.Title));
        view.NavigationCompleted += (_, e) => _ui.Post(() => _tabService.MutateNavigationCompleted(tab.Id, e.Url));
        view.FaviconChanged += (_, e) => _ui.Post(() => _tabService.MutateFavicon(tab.Id, e.Url));
        view.LoadingStateChanged += (_, e) => _ui.Post(() => _tabService.MutateLoadingState(tab.Id, e.IsLoading));
        view.AudioStateChanged += (_, e) => _ui.Post(() => _tabService.MutateAudioState(tab.Id, e.Playing));
        view.NavigationStarting += (_, e) => _ui.Post(() => _tabService.MutateLastInteractionUtc(tab.Id));
        view.RenderError += (_, e) => _log.LogWarning("Tab {Id} render error: {Msg}", tab.Id, e.Message);

        view.PermissionRequested += (_, e) => _ui.Post(() => _ = DecidePermissionAsync(tab, e));
        view.DownloadRequested += (_, e) => _ui.Post(() => _ = DecideDownloadAsync(tab, e));
        view.NewWindowRequested += (_, e) => _ui.Post(() => _ = HandleNewWindowAsync(tab, e));
        // Accelerator keys forwarded from page content (JS bridge) are
        // invoked inline — never posted — so a claimed combo is answered
        // promptly. The UI layer's handler marshals command execution to
        // the UI thread itself.
        view.AcceleratorKeyPressed += (_, e) =>
        {
            try
            {
                var h = AcceleratorHandler;
                if (h is not null && h(e)) e.Handled = true;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Accelerator handler failed for tab {Id}", tab.Id);
            }
        };
    }

    private async Task DecidePermissionAsync(TabRecord tab, PermissionRequestEventArgs e)
    {
        try
        {
            if (PermissionPromptAsync is not null)
            {
                var allow = await PermissionPromptAsync(e.Kind, e.Origin).ConfigureAwait(false);
                if (allow) e.Allow(); else e.Deny();
                _log.LogInformation("Permission {Kind} for {Origin}: {Decision}", e.Kind, e.Origin, allow ? "allow" : "deny");
                return;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Permission prompt failed; denying {Kind}", e.Kind);
        }
        // Default conservative policy: deny sensitive permissions.
        e.Deny();
    }

    private async Task DecideDownloadAsync(TabRecord tab, DownloadEventArgs e)
    {
        try
        {
            if (DownloadPromptAsync is not null)
            {
                var accepted = await DownloadPromptAsync(e.SuggestedFileName, e.Url).ConfigureAwait(false);
                if (accepted is not null) e.Accept(accepted); else e.Decline();
                return;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Download prompt failed; accepting default location");
        }
        // No UI prompt wired: accept the engine default location.
        e.Accept(e.SuggestedFileName);
    }

    private async Task HandleNewWindowAsync(TabRecord source, NewWindowRequestEventArgs e)
    {
        try
        {
            if (NewWindowHandlerAsync is not null)
            {
                await NewWindowHandlerAsync(e.Url, e.IsUserInitiated).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "New-window handler failed for {Url}", e.Url);
        }
        // Default policy: open user-initiated requests as background tabs,
        // decline the rest to prevent unsolicited popup abuse.
        if (e.IsUserInitiated && Uri.TryCreate(e.Url, UriKind.Absolute, out _))
        {
            try { await _tabService.OpenNewAsync(e.Url, switchTo: false).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to open popup tab {Url}", e.Url); }
        }
        else
        {
            e.Decline();
        }
    }

    public bool HasView(Guid tabId)
    {
        lock (_viewsGate) return _views.ContainsKey(tabId);
    }

    public async Task<bool> SuspendAsync(Guid tabId, CancellationToken ct = default)
    {
        IBrowserView? view;
        lock (_viewsGate) _views.TryGetValue(tabId, out view);
        if (view is null) return false;
        var sw = Stopwatch.StartNew();
        try
        {
            var ctx = await view.ExtractPageContextAsync(ct).ConfigureAwait(false);
            _ui.Post(() => _tabService.UpdatePageContext(tabId, ctx));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Suspend pre-save failed for tab {Id}", tabId);
        }
        try
        {
            var ok = await view.SuspendAsync(ct).ConfigureAwait(false);
            LastSuspendLatency = sw.Elapsed;
            if (ok && view.State == ViewLifecycleState.Warm)
            {
                try
                {
                    var (sx, sy) = await view.GetScrollAsync(ct).ConfigureAwait(false);
                    _ui.Post(() => _tabService.MutateScroll(tabId, sx, sy));
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Suspend scroll save failed for tab {Id}", tabId);
                }
                return true;
            }
            _log.LogWarning("Suspend for tab {Id} reported failure", tabId);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Suspend failed for tab {Id}", tabId);
            return false;
        }
    }

    public async Task<bool> ResumeAsync(Guid tabId, CancellationToken ct = default)
    {
        IBrowserView? view;
        lock (_viewsGate) _views.TryGetValue(tabId, out view);
        if (view is null) return false;
        var sw = Stopwatch.StartNew();
        try
        {
            var ok = await view.ResumeAsync(ct).ConfigureAwait(false);
            LastResumeLatency = sw.Elapsed;
            return ok;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Resume failed for tab {Id}", tabId);
            return false;
        }
    }

    public async Task<bool> GhostAsync(Guid tabId, CancellationToken ct = default)
    {
        IBrowserView? view;
        lock (_viewsGate)
        {
            if (!_views.TryGetValue(tabId, out view)) return false;
            _views.Remove(tabId);
        }
        if (view is null) return false;
        try
        {
            var ctx = await view.ExtractPageContextAsync(ct).ConfigureAwait(false);
            _ui.Post(() => _tabService.UpdatePageContext(tabId, ctx));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Ghost pre-save failed for tab {Id}", tabId);
        }
        try
        {
            await view.GhostAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ghost dispose raised for tab {Id}", tabId);
        }
        try { await view.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "Ghost final dispose raised for tab {Id}", tabId); }
        _log.LogInformation("Renderer ghosted for tab {Id}", tabId);
        return true;
    }

    /// <summary>
    /// Restore a Ghosted tab to a fully Live, navigated state. Creates a
    /// fresh renderer, navigates to the persisted URL, awaits navigation
    /// completion, then restores scroll. Returns true when the page is
    /// usable (or when there is nothing to navigate, e.g. encomm://).
    /// </summary>
    public async Task<bool> RestoreGhostTabAsync(TabRecord tab, CancellationToken ct = default)
    {
        if (tab is null) throw new ArgumentNullException(nameof(tab));
        var sw = Stopwatch.StartNew();
        try
        {
            var view = await GetOrCreateAsync(tab, ct).ConfigureAwait(false);
            if (view is null) return false;

            if (string.IsNullOrEmpty(tab.Url) || tab.Url.StartsWith("encomm://", StringComparison.OrdinalIgnoreCase))
            {
                _tabService.SetRendererState(tab, TabRendererStateKind.Live);
                LastRestoreLatency = sw.Elapsed;
                return true;
            }
            // Subscribe BEFORE navigating: a fast (cached/redirected) load
            // can complete before a post-navigate subscription attaches,
            // which previously produced false "did not complete" warnings.
            var navigationWait = AwaitNavigationAsync(view, ct);
            var result = await view.NavigateAsync(tab.Url, ct).ConfigureAwait(false);
            if (!result.Accepted)
            {
                _log.LogWarning("Restore navigate failed for {Id}: {Reason}", tab.Id, result.Reason);
                return false;
            }
            // Await the navigation-completion event so scroll restoration
            // lands on the loaded document instead of about:blank.
            var completed = await navigationWait.ConfigureAwait(false);
            if (completed is null)
            {
                _log.LogWarning("Restore navigation did not complete for {Id} (watchdog/cancelled)", tab.Id);
            }
            else if (!completed.Success)
            {
                _log.LogWarning("Restore navigation reported failure for {Id}: url={Url} error={Error}",
                    tab.Id, completed.Url, completed.ErrorMessage);
            }
            try
            {
                await view.SetScrollAsync(tab.ScrollX, tab.ScrollY, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Restore scroll failed for {Id}", tab.Id);
            }
            _tabService.SetRendererState(tab, TabRendererStateKind.Live);
            LastRestoreLatency = sw.Elapsed;
            _log.LogInformation("Tab {Id} restored in {Ms}ms", tab.Id, LastRestoreLatency.TotalMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Restore failed for tab {Id}: {Stack}", tab.Id, ex.ToString());
            return false;
        }
    }

    /// <summary>
    /// Subscribes to the next main-frame NavigationCompleted and returns
    /// its payload. Null means the 20s watchdog or cancellation fired
    /// first. Callers must subscribe BEFORE starting navigation: fast
    /// loads can complete before a post-navigate subscription attaches.
    /// </summary>
    private static Task<NavigationCompletedEventArgs?> AwaitNavigationAsync(IBrowserView view, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<NavigationCompletedEventArgs?>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<NavigationCompletedEventArgs>? handler = null;
        handler = (_, e) =>
        {
            // Fresh views abort their initial about:blank load when the
            // real navigation starts (ConnectionAborted). That completion
            // is expected noise — keep waiting for the real document.
            // (Restore/ensure only navigate http(s) URLs, so ignoring
            // about:blank here can never skip the awaited load.)
            if (e.Url is not null && e.Url.Contains("about:blank", StringComparison.OrdinalIgnoreCase)) return;
            if (handler is not null) view.NavigationCompleted -= handler;
            tcs.TrySetResult(e);
        };
        view.NavigationCompleted += handler;
        var reg = ct.Register(() =>
        {
            if (handler is not null) view.NavigationCompleted -= handler;
            tcs.TrySetResult(null);
        });
        // Navigation watchdog: a page that never completes must not hang
        // restoration forever.
        var watchdog = Task.Delay(TimeSpan.FromSeconds(20), ct).ContinueWith(_ =>
        {
            if (handler is not null) view.NavigationCompleted -= handler;
            tcs.TrySetResult(null);
        }, TaskScheduler.Default);
        _ = watchdog.ContinueWith(_ => reg.Dispose(), TaskScheduler.Default);
        return tcs.Task;
    }

    /// <summary>
    /// Ensure the active tab's renderer shows its stored URL. Called after
    /// selecting a tab, restoring a session, or switching workspaces. For
    /// encomm:// URLs the native new-tab surface is shown instead of
    /// navigating WebView2 (which cannot resolve that scheme).
    /// </summary>
    public async Task<bool> EnsureTabContentAsync(TabRecord tab, CancellationToken ct = default)
    {
        if (tab is null) throw new ArgumentNullException(nameof(tab));
        if (string.IsNullOrEmpty(tab.Url) || tab.Url.StartsWith("encomm://", StringComparison.OrdinalIgnoreCase))
        {
            // Native new-tab surface; nothing to navigate.
            return true;
        }
        var view = await GetOrCreateAsync(tab, ct).ConfigureAwait(false);
        if (view is null) return false;
        var current = view.CurrentUrl ?? "";
        if (string.IsNullOrEmpty(current) || current.Contains("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            var navigationWait = AwaitNavigationAsync(view, ct);
            var result = await view.NavigateAsync(tab.Url, ct).ConfigureAwait(false);
            if (!result.Accepted)
            {
                _log.LogWarning("EnsureTabContent navigate failed for {Id}: {Reason}", tab.Id, result.Reason);
                return false;
            }
            await navigationWait.ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// Returns true when total process-tree memory is over the configured
    /// threshold (Adaptive preset). The lifecycle uses this to Ghost
    /// unprotected tabs earlier.
    /// </summary>
    public bool IsUnderMemoryPressure(Func<IReadOnlyList<WebViewProcessInfo>>? processSource = null)
    {
        if (MemoryPressureThresholdBytes <= 0) return false;
        try
        {
            var infos = processSource is not null ? processSource() : GetWebViewProcessInfos();
            long total = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
            foreach (var p in infos) total += p.WorkingSet64;
            return total >= MemoryPressureThresholdBytes;
        }
        catch
        {
            return false;
        }
    }

    public async Task<int> GhostAllAsync(CancellationToken ct = default)
    {
        List<IBrowserView> toDispose;
        lock (_viewsGate)
        {
            toDispose = _views.Values.ToList();
            _views.Clear();
        }
        foreach (var v in toDispose)
        {
            try { await v.GhostAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "GhostAll: ghost raised"); }
            try { await v.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "GhostAll: dispose raised"); }
        }
        if (toDispose.Count > 0)
            _log.LogInformation("All {Count} renderers ghosted.", toDispose.Count);
        return toDispose.Count;
    }

    public async Task<bool> DropViewAsync(Guid tabId, CancellationToken ct = default)
    {
        IBrowserView? view;
        lock (_viewsGate)
        {
            if (!_views.TryGetValue(tabId, out view)) return false;
            _views.Remove(tabId);
        }
        if (view is null) return false;
        try { await view.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "DropView dispose raised for {Id}", tabId); }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await GhostAllAsync().ConfigureAwait(false);
    }
}
