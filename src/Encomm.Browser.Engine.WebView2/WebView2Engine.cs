using System.Runtime.Versioning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;
using Encomm.Browser.Engine.Abstractions;
using Encomm.Browser.Shield;
using Encomm.Browser.Security;

namespace Encomm.Browser.Engine.WebView2;

[SupportedOSPlatform("windows")]
public sealed class WebView2Engine : IBrowserEngine
{
    public string EngineId => "webview2";
    public string? RuntimeVersion { get; }

    private readonly IRequestBlocker _blocker;
    private readonly IUiDispatcher _ui;
    private CoreWebView2Environment? _environment;
    private readonly object _envGate = new();
    private Task<CoreWebView2Environment>? _envTask;

    public WebView2Engine(IRequestBlocker blocker, IUiDispatcher ui)
    {
        _blocker = blocker;
        _ui = ui;
        try { RuntimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { RuntimeVersion = null; }
    }

    internal void SetEnvironment(CoreWebView2Environment env)
    {
        lock (_envGate) _environment = env;
    }

    public IReadOnlyList<WebViewProcessInfo> GetWebViewProcessInfos()
    {
        CoreWebView2Environment? env;
        lock (_envGate) env = _environment;
        if (env is null) return Array.Empty<WebViewProcessInfo>();
        try
        {
            var infos = env.GetProcessInfos();
            var list = new List<WebViewProcessInfo>(infos.Count);
            foreach (var p in infos)
            {
                var kind = p.Kind switch
                {
                    CoreWebView2ProcessKind.Browser => WebViewProcessKind.Browser,
                    CoreWebView2ProcessKind.Renderer => WebViewProcessKind.Renderer,
                    CoreWebView2ProcessKind.Gpu => WebViewProcessKind.Gpu,
                    _ => WebViewProcessKind.Utility
                };
                list.Add(new WebViewProcessInfo(p.ProcessId, kind, TryGetWorkingSet(p.ProcessId)));
            }
            return list;
        }
        catch
        {
            return Array.Empty<WebViewProcessInfo>();
        }
    }

    private static long TryGetWorkingSet(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.Refresh();
            return p.WorkingSet64;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Create the CoreWebView2 environment, then build a fully-initialized
    /// <see cref="IBrowserView"/> ready to be hosted and navigated.
    /// </summary>
    /// <summary>
    /// Create a fully-initialized view. The shared CoreWebView2Environment
    /// is created once and reused: one environment per process keeps all
    /// tabs in a single WebView2 browser process tree.
    /// </summary>
    public async Task<IBrowserView> CreateViewAsync(Guid tabId, CancellationToken ct = default)
    {
        var env = await GetEnvironmentAsync().ConfigureAwait(false);
        SetEnvironment(env);
        var view = new WebView2BrowserView(tabId, env, _blocker, _ui);
        await view.InitializeAsync(ct).ConfigureAwait(false);
        return view;
    }

    private Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        lock (_envGate)
        {
            if (_envTask is null) _envTask = CreateEnvironmentAsync();
            return _envTask;
        }
    }

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync(CancellationToken ct = default)
    {
        var userData = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Encomm", "Encomm-AI-Browser", "UserData");
        System.IO.Directory.CreateDirectory(userData);
        // The WinAppSDK-bundled .NET projection of CoreWebView2Environment
        // exposes CreateAsync as a parameterless method returning
        // Windows.Foundation.IAsyncOperation<CoreWebView2Environment>
        // (all three ABI parameters have defaults). The user-data folder
        // is supplied through the documented WEBVIEW2_USER_DATA_FOLDER
        // environment override, which CreateAsync honors.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userData);
        var t = typeof(CoreWebView2Environment);
        var m0 = t.GetMethod("CreateAsync", Type.EmptyTypes);
        if (m0 is null)
            throw new InvalidOperationException("CoreWebView2Environment.CreateAsync overload not found.");
        var op = m0.Invoke(null, null)!;
        // Await the IAsyncOperation without depending on the WinRT awaiter
        // extensions: block on a TaskCompletionSource via the Completed handler.
        var tcs = new TaskCompletionSource<CoreWebView2Environment>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asyncOp = (Windows.Foundation.IAsyncOperation<CoreWebView2Environment>)op;
        asyncOp.Completed = (_, status) =>
        {
            try
            {
                if (status == Windows.Foundation.AsyncStatus.Completed)
                    tcs.TrySetResult(asyncOp.GetResults());
                else if (status == Windows.Foundation.AsyncStatus.Canceled)
                    tcs.TrySetCanceled(ct);
                else
                    tcs.TrySetException(new InvalidOperationException("CoreWebView2Environment creation failed: " + status));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        };
        using (ct.Register(() => tcs.TrySetCanceled(ct))) { }
        return await tcs.Task.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[SupportedOSPlatform("windows")]
public sealed class WebView2BrowserView : IBrowserView
{
    private readonly Guid _tabId;
    private readonly CoreWebView2Environment _environment;
    private readonly IRequestBlocker _blocker;
    private readonly IUiDispatcher _ui;
    private Microsoft.UI.Xaml.Controls.WebView2 _control = null!;
    private ViewLifecycleState _state = ViewLifecycleState.None;
    private bool _shieldFilterInstalled;
    private bool _disposed;
    private string _lastUrl = "";
    private string _lastTitle = "";
    private bool _isLoading;
    // Page-context cache for Ghost metadata preservation.
    private string? _cachedDescription;
    private string? _cachedSelection;
    private string? _cachedBody;
    private double _scrollX;
    private double _scrollY;

    public WebView2BrowserView(Guid tabId, CoreWebView2Environment environment, IRequestBlocker blocker, IUiDispatcher ui)
    {
        _tabId = tabId;
        _environment = environment;
        _blocker = blocker;
        _ui = ui;
        // The WinUI control MUST be created on the UI thread that owns
        // its XamlRoot. We assume the caller (BrowserRuntime) is invoking
        // us on the UI thread. The control creation is lightweight.
        _control = new Microsoft.UI.Xaml.Controls.WebView2
        {
            MinWidth = 1,
            MinHeight = 1,
        };
    }

    public Guid Id => _tabId;
    public ViewLifecycleState State => _state;
    /// <summary>
    /// Thread-safe reads: the XAML control has UI-thread affinity, so
    /// off-thread callers get the last cached value (maintained on the
    /// UI thread by navigation/title events).
    /// </summary>
    public string CurrentUrl => _ui.HasThreadAccess
        ? (_control?.Source?.ToString() ?? _lastUrl)
        : _lastUrl;
    public string CurrentTitle => _ui.HasThreadAccess
        ? (_control?.CoreWebView2?.DocumentTitle ?? _lastTitle)
        : _lastTitle;
    public string? CurrentFaviconUrl { get; private set; }
    public bool CanGoBack => _control?.CanGoBack ?? false;
    public bool CanGoForward => _control?.CanGoForward ?? false;
    public bool IsLoading => _isLoading;
    public bool IsDocumentPlayingAudio => _control?.CoreWebView2?.IsDocumentPlayingAudio ?? false;

    public object HostElement => _control ?? throw new InvalidOperationException("WebView2 control missing.");

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

    /// <summary>
    /// Capture-phase shortcut bridge. Runs on every document (main frame)
    /// so owned browser combos reach the App layer even when page content
    /// has keyboard focus. The guard flag keeps re-navigation idempotent.
    /// </summary>
    private const string AcceleratorBridgeScript = """
        (function(){
          if (window.__encommAccel) return; window.__encommAccel = true;
          function owned(e){
            var k = e.key || '';
            if (e.ctrlKey && !e.altKey && !e.metaKey){
              var lk = k.toLowerCase();
              if (lk==='t'||lk==='w'||lk==='r'||lk==='l'||k==='Tab') return true;
            }
            if (e.altKey && !e.ctrlKey && !e.metaKey && (k==='ArrowLeft'||k==='ArrowRight')) return true;
            if (!e.ctrlKey && !e.altKey && !e.metaKey && k==='F12') return true;
            return false;
          }
          window.addEventListener('keydown', function(e){
            if (!owned(e)) return;
            try {
              e.preventDefault(); e.stopPropagation();
              if (window.chrome && window.chrome.webview){
                window.chrome.webview.postMessage(JSON.stringify({
                  kind:'encomm-accelerator', key:e.key,
                  ctrl:!!e.ctrlKey, shift:!!e.shiftKey, alt:!!e.altKey
                }));
              }
            } catch(_){}
          }, true);
        })();
        """;

    private static void InstallAcceleratorBridge(CoreWebView2 cv)
    {
        try { _ = cv.AddScriptToExecuteOnDocumentCreatedAsync(AcceleratorBridgeScript); }
        catch { /* bridge is best-effort; XAML accelerators still cover chrome focus */ }
    }

    private void OnWebMessageForAccelerator(CoreWebView2WebMessageReceivedEventArgs args)
    {
        string? json = null;
        try { json = args.TryGetWebMessageAsString(); } catch { return; }
        if (string.IsNullOrEmpty(json) || !json.Contains("encomm-accelerator")) return;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("key", out var kp)) return;
            var vk = MapAcceleratorKey(kp.GetString());
            if (vk is null) return;
            var ctrl = root.TryGetProperty("ctrl", out var c) && c.GetBoolean();
            var shift = root.TryGetProperty("shift", out var s) && s.GetBoolean();
            var alt = root.TryGetProperty("alt", out var a) && a.GetBoolean();
            var forwarded = new AcceleratorKeyEventArgs
            {
                VirtualKey = vk.Value,
                Ctrl = ctrl,
                Shift = shift,
                Alt = alt,
                KeyDown = true,
            };
            AcceleratorKeyPressed?.Invoke(this, forwarded);
        }
        catch { /* malformed bridge message: ignore */ }
    }

    private static uint? MapAcceleratorKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (key.Length == 1)
        {
            var upper = char.ToUpperInvariant(key[0]);
            if (upper is >= 'A' and <= 'Z') return upper;
            return null;
        }
        return key switch
        {
            "Tab" => 0x09,
            "F12" => 0x7B,
            "ArrowLeft" => 0x25,
            "ArrowRight" => 0x27,
            _ => null,
        };
    }

    /// <summary>
    /// Initialize the WinUI WebView2 control, ensure CoreWebView2, wire
    /// events, and install the Shield resource filter. This is invoked
    /// from the engine adapter immediately after construction. The
    /// returned task completes only after the underlying WebView2 is
    /// ready to navigate and host.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_state == ViewLifecycleState.Live) return;
        _state = ViewLifecycleState.Initializing;
        await _control.EnsureCoreWebView2Async(_environment);
        WireEvents();
        InstallShieldFilter();
        _state = ViewLifecycleState.Live;
    }

    private void InstallShieldFilter()
    {
        if (_shieldFilterInstalled) return;
        if (_control?.CoreWebView2 is null) return;
        try
        {
            _control.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            _shieldFilterInstalled = true;
        }
        catch
        {
            // Will retry on the first NavigationStarting event.
        }
    }

    private void WireEvents()
    {
        var cv = _control.CoreWebView2;
        if (cv is null) return;

        cv.Settings.AreDevToolsEnabled = true;
        cv.Settings.AreDefaultContextMenusEnabled = true;
        cv.Settings.IsZoomControlEnabled = true;

        cv.NewWindowRequested += (s, args) =>
        {
            // Default policy: open user-initiated new-window requests in a
            // new Encomm tab via the event. Non-user-initiated popups are
            // declined to prevent unsolicited popup abuse.
            var url = args.Uri;
            var isUser = args.IsUserInitiated;
            args.Handled = true;
            NewWindowRequested?.Invoke(this, new NewWindowRequestEventArgs
            {
                Url = url,
                IsUserInitiated = isUser,
                OpenInNewTab = () => { /* the App layer will open a tab */ },
                Decline = () => { /* args.Handled=true already cancels */ }
            });
        };

        // Browser shortcuts must work while focus is inside page
        // content: the WebView2 child HWND receives keyboard input
        // directly, bypassing the XAML accelerator table (and
        // CoreWebView2 itself exposes no key event — AcceleratorKeyPressed
        // lives on CoreWebView2Controller, unreachable from WinUI). So a
        // tiny capture-phase script forwards owned combos via postMessage,
        // with preventDefault suppressing WebView2 defaults (e.g. Ctrl+R).
        cv.WebMessageReceived += (s, args) => OnWebMessageForAccelerator(args);
        InstallAcceleratorBridge(cv);

        cv.PermissionRequested += (s, args) =>
        {
            var kind = args.PermissionKind switch
            {
                CoreWebView2PermissionKind.Camera => PermissionKind.Camera,
                CoreWebView2PermissionKind.Microphone => PermissionKind.Microphone,
                CoreWebView2PermissionKind.Geolocation => PermissionKind.Geolocation,
                CoreWebView2PermissionKind.Notifications => PermissionKind.Notifications,
                CoreWebView2PermissionKind.ClipboardRead => PermissionKind.ClipboardRead,
                _ => PermissionKind.Other
            };
            args.Handled = true;
            PermissionRequested?.Invoke(this, new PermissionRequestEventArgs
            {
                Kind = kind,
                Origin = cv.Source,
                // Default conservative policy: deny sensitive permissions
                // unless the App layer decides otherwise. The App layer's
                // permission UI calls Allow() or Deny() to set the result.
                Allow = () => { try { args.State = CoreWebView2PermissionState.Allow; } catch { } },
                Deny = () => { try { args.State = CoreWebView2PermissionState.Deny; } catch { } }
            });
        };

        cv.DownloadStarting += (s, args) =>
        {
            args.Handled = true;
            var dl = args.DownloadOperation;
            var downloadsDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Encomm", "Downloads");
            try { System.IO.Directory.CreateDirectory(downloadsDir); } catch { }
            // In WinAppSDK 1.7 / WebView2 1.0.2903.40 the ResultFilePath
            // property of CoreWebView2DownloadOperation is read-only. We
            // present a default save path to the user; the App layer's
            // download UI is responsible for actually persisting the file
            // (the WebView2 default location is used for now).
            var defaultName = string.IsNullOrEmpty(dl.ResultFilePath)
                ? System.IO.Path.Combine(downloadsDir, System.IO.Path.GetFileName(dl.Uri))
                : dl.ResultFilePath;
            DownloadRequested?.Invoke(this, new DownloadEventArgs
            {
                SuggestedFileName = defaultName,
                Url = dl.Uri,
                Accept = _ => { /* accept default location */ },
                Decline = () => { args.Cancel = true; }
            });
        };

        cv.WebResourceRequested += (s, args) =>
        {
            try
            {
                var uri = args.Request.Uri;
                var category = CategoryFromContext(args.ResourceContext);
                var decision = _blocker.ShouldBlock(uri, category);
                if (!decision.Allow)
                {
                    var empty = new InMemoryRandomAccessStream();
                    args.Response = cv.Environment.CreateWebResourceResponse(
                        empty, 403, "Blocked by Shield", "Content-Type: text/plain");
                    ResourceBlocked?.Invoke(this, new ResourceBlockedEventArgs
                    {
                        Url = uri,
                        Category = category,
                        Reason = decision.Reason ?? "blocked"
                    });
                }
            }
            catch (Exception ex)
            {
                RenderError?.Invoke(this, new RenderErrorEventArgs { FailedUrl = null, Message = "Shield error", Exception = ex });
            }
        };

        cv.DocumentTitleChanged += (s, args) =>
        {
            _lastTitle = cv.DocumentTitle;
            TitleChanged?.Invoke(this, new TitleChangedEventArgs { Title = cv.DocumentTitle });
        };

        cv.FaviconChanged += (s, args) =>
        {
            try
            {
                var uri = cv.FaviconUri;
                CurrentFaviconUrl = string.IsNullOrEmpty(uri) ? null : uri;
                if (!string.IsNullOrEmpty(uri))
                    FaviconChanged?.Invoke(this, new FaviconChangedEventArgs { Url = uri });
            }
            catch { }
        };

        cv.IsDocumentPlayingAudioChanged += (s, args) =>
            AudioStateChanged?.Invoke(this, new AudioEventArgs
            {
                Playing = cv.IsDocumentPlayingAudio,
                Muted = cv.IsMuted
            });

        cv.NavigationStarting += (s, args) =>
        {
            InstallShieldFilter();
            NavigationStarting?.Invoke(this, new NavigationStartingEventArgs
            {
                Url = args.Uri,
                IsUserInitiated = args.IsUserInitiated,
                IsMainFrame = true
            });
            _isLoading = true;
            LoadingStateChanged?.Invoke(this, new LoadingStateEventArgs { IsLoading = true });
        };

        cv.NavigationCompleted += (s, args) =>
        {
            _isLoading = false;
            _lastUrl = cv.Source;
            _cachedSelection = null;
            _cachedBody = null;
            _cachedDescription = null;
            NavigationCompleted?.Invoke(this, new NavigationCompletedEventArgs
            {
                Url = cv.Source,
                HttpStatus = (int)args.HttpStatusCode,
                Success = args.IsSuccess,
                ErrorMessage = args.IsSuccess ? null : args.WebErrorStatus.ToString()
            });
            LoadingStateChanged?.Invoke(this, new LoadingStateEventArgs { IsLoading = false });
            _ = CaptureScrollAsync();
        };

        cv.SourceChanged += (s, args) =>
        {
            _isLoading = !cv.Source.Contains("about:blank", StringComparison.OrdinalIgnoreCase);
            LoadingStateChanged?.Invoke(this, new LoadingStateEventArgs { IsLoading = _isLoading });
        };
    }

    private async Task CaptureScrollAsync()
    {
        try
        {
            var (x, y) = await GetScrollAsync();
            _scrollX = x;
            _scrollY = y;
        }
        catch { }
    }

    private static ResourceCategory CategoryFromContext(CoreWebView2WebResourceContext ctx) => ctx switch
    {
        CoreWebView2WebResourceContext.Document => ResourceCategory.Document,
        CoreWebView2WebResourceContext.Script => ResourceCategory.Script,
        CoreWebView2WebResourceContext.Stylesheet => ResourceCategory.Style,
        CoreWebView2WebResourceContext.Image => ResourceCategory.Image,
        CoreWebView2WebResourceContext.Font => ResourceCategory.Font,
        CoreWebView2WebResourceContext.Media => ResourceCategory.Media,
        CoreWebView2WebResourceContext.XmlHttpRequest => ResourceCategory.Xhr,
        CoreWebView2WebResourceContext.Fetch => ResourceCategory.Fetch,
        _ => ResourceCategory.Other
    };

    /// <summary>
    /// Suspend the renderer (Live → Warm). ACTUAL suspension via
    /// CoreWebView2.TrySuspendAsync. The control is hidden first because
    /// TrySuspendAsync requires the host to not be visible. On success
    /// transitions to Warm and returns true.
    ///
    /// Thread-safe: every touch of the XAML control / CoreWebView2 is
    /// marshaled through the injected dispatcher, so lifecycle timers
    /// and background callers may call from any thread.
    /// </summary>
    public async Task<bool> SuspendAsync(CancellationToken ct = default)
    {
        if (_disposed) return false;
        if (_state != ViewLifecycleState.Live) return false;
        // Every touch of the XAML control (including the null-guard on
        // .CoreWebView2, which is itself a UI-thread-affine getter) must
        // happen on the UI thread: callers include pool-thread lifecycle
        // timers. Setup (guard + collapse) runs dispatched as one unit.
        var prevVisibility = Microsoft.UI.Xaml.Visibility.Visible;
        try
        {
            var setup = await _ui.RunAsync(() =>
            {
                try
                {
                    if (_disposed || _control?.CoreWebView2 is null)
                        return Task.FromResult((false, Microsoft.UI.Xaml.Visibility.Visible));
                    var prev = _control.Visibility;
                    _control.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    return Task.FromResult((true, prev));
                }
                catch { return Task.FromResult((false, Microsoft.UI.Xaml.Visibility.Visible)); }
            }).ConfigureAwait(false);
            if (!setup.Item1) return false;
            prevVisibility = setup.Item2;
        }
        catch { return false; }
        bool ok;
        try
        {
            ok = await _ui.RunAsync(async () =>
            {
                try
                {
                    if (_control?.CoreWebView2 is null) return false;
                    return await _control.CoreWebView2.TrySuspendAsync();
                }
                catch { return false; }
            }).ConfigureAwait(false);
            if (ok)
            {
                _state = ViewLifecycleState.Warm;
                LoadingStateChanged?.Invoke(this, new LoadingStateEventArgs { IsLoading = false });
            }
            else
            {
                var v = prevVisibility;
                await _ui.RunAsync(() => { _control.Visibility = v; return Task.FromResult(true); }).ConfigureAwait(false);
            }
            return ok;
        }
        catch
        {
            var v = prevVisibility;
            await _ui.RunAsync(() => { _control.Visibility = v; return Task.FromResult(true); }).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Resume a suspended renderer (Warm → Live). Restores control
    /// visibility and calls CoreWebView2.Resume(). The host visibility is
    /// READ BACK after the write: Live is set (and true returned) only
    /// when the host actually reports Visible, so callers can trust
    /// "returned true ⇒ visible + Live".
    /// </summary>
    /// Thread-safe like SuspendAsync: CoreWebView2.Resume() is
    /// marshaled through the dispatcher.
    /// </summary>
    public async Task<bool> ResumeAsync(CancellationToken ct = default)
    {
        if (_disposed) return false;
        if (_state != ViewLifecycleState.Warm) return _state == ViewLifecycleState.Live;
        try
        {
            var resumed = await _ui.RunAsync(() =>
            {
                try
                {
                    if (_disposed || _control?.CoreWebView2 is null) return Task.FromResult(false);
                    if (_control.CoreWebView2.IsSuspended)
                    {
                        _control.CoreWebView2.Resume();
                    }
                    return Task.FromResult(true);
                }
                catch { return Task.FromResult(false); }
            }).ConfigureAwait(false);
            if (!resumed) return false;
            await _ui.RunAsync(() => { _control.Visibility = Microsoft.UI.Xaml.Visibility.Visible; return Task.FromResult(true); }).ConfigureAwait(false);
            var visible = await _ui.RunAsync(() => Task.FromResult(_control.Visibility)).ConfigureAwait(false);
            if (visible != Microsoft.UI.Xaml.Visibility.Visible) return false;
            _state = ViewLifecycleState.Live;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort detection of potentially unsaved form state. Runs a
    /// single bounded script that inspects inputs, textareas and
    /// contenteditable regions for user edits. Never throws.
    /// </summary>
    public async Task<bool> HasUnsavedFormStateAsync(CancellationToken ct = default)
    {
        if (_state != ViewLifecycleState.Live && _state != ViewLifecycleState.Warm) return false;
        // The control guard must run on the UI thread (callers include
        // pool-thread lifecycle timers); the script itself runs through
        // the agile CoreWebView2 object.
        return await _ui.RunAsync(async () =>
        {
            try
            {
                if (_control?.CoreWebView2 is null) return false;
                const string script = "(() => { try {"
                + " const els = document.querySelectorAll('input,textarea,select');"
                + " for (const el of els) {"
                + "   if (el.type === 'password') continue;"
                + "   const v = (el.value ?? '');"
                + "   const d = el.defaultValue ?? '';"
                + "   if (v !== d && v.length > 0) return true;"
                + "   if (el.isContentEditable && (el.innerText ?? '').length > 0) return true;"
                + " }"
                + " const edits = document.querySelectorAll('[contenteditable=\"true\"]');"
                + " for (const el of edits) { if ((el.innerText ?? '').trim().length > 0) return true; }"
                + " return false;"
                + " } catch(e) { return false; } })();";
                var result = await _control.CoreWebView2.ExecuteScriptAsync(script);
                return Security.PageContextParser.ParseBoolResult(result);
            }
            catch { return false; }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Ghost the renderer (any non-Ghost → Ghost). Closes the WebView2
    /// control and releases the underlying CoreWebView2. The control
    /// reference is dropped so the GC and underlying Chromium process can
    /// free the memory.
    /// </summary>
    public Task<bool> GhostAsync(CancellationToken ct = default) =>
        _ui.RunAsync(() =>
        {
            if (_disposed) return Task.FromResult(false);
            try
            {
                if (_control is not null)
                {
                    try { _control.Close(); } catch { }
                    _control = null!;
                }
            }
            catch { }
            _shieldFilterInstalled = false;
            _state = ViewLifecycleState.Ghost;
            LoadingStateChanged?.Invoke(this, new LoadingStateEventArgs { IsLoading = false });
            return Task.FromResult(true);
        });

    public Task<NavigationResult> NavigateAsync(string url, CancellationToken ct = default) =>
        _ui.RunAsync(() =>
        {
            if (_state == ViewLifecycleState.Ghost || _state == ViewLifecycleState.None)
                return Task.FromResult(new NavigationResult(false, "renderer is not initialized"));
            if (_control?.CoreWebView2 is null)
                return Task.FromResult(new NavigationResult(false, "CoreWebView2 not available"));
            if (string.IsNullOrWhiteSpace(url))
                return Task.FromResult(new NavigationResult(false, "empty URL"));
            try
            {
                _control.CoreWebView2.Navigate(url);
                return Task.FromResult(new NavigationResult(true));
            }
            catch (Exception ex)
            {
                return Task.FromResult(new NavigationResult(false, ex.GetType().Name + ": " + ex.Message));
            }
        });

    public Task<string?> ResolveUrlAsync(string userInput, CancellationToken ct = default)
        => Task.FromResult<string?>(WebView2Omnibox.Resolve(userInput));

    public Task ReloadAsync(CancellationToken ct = default) =>
        _ui.RunAsync(() =>
        {
            if (_control?.CoreWebView2 is not null)
            {
                try { _control.CoreWebView2.Reload(); } catch { }
            }
            return Task.FromResult(true);
        });

    public Task StopAsync(CancellationToken ct = default) =>
        _ui.RunAsync(() =>
        {
            if (_control?.CoreWebView2 is not null)
            {
                try { _control.CoreWebView2.Stop(); } catch { }
            }
            return Task.FromResult(true);
        });

    public Task GoBackAsync(CancellationToken ct = default) =>
        _ui.RunAsync(() =>
        {
            if (_control?.CanGoBack == true)
            {
                try { _control.GoBack(); } catch { }
            }
            return Task.FromResult(true);
        });

    public Task GoForwardAsync(CancellationToken ct = default) =>
        _ui.RunAsync(() =>
        {
            if (_control?.CanGoForward == true)
            {
                try { _control.GoForward(); } catch { }
            }
            return Task.FromResult(true);
        });

    public Task<(double X, double Y)> GetScrollAsync(CancellationToken ct = default) =>
        _ui.RunAsync(async () =>
        {
            if (_control?.CoreWebView2 is null) return (0, 0);
            try
            {
                const string script = "(() => { try { return { x: window.scrollX || 0, y: window.scrollY || 0 }; } catch(e) { return null; } })();";
                var result = await _control.CoreWebView2.ExecuteScriptAsync(script);
                return ParseScroll(result);
            }
            catch { return (0, 0); }
        });

    public Task SetScrollAsync(double x, double y, CancellationToken ct = default) =>
        _ui.RunAsync(async () =>
        {
            if (_control?.CoreWebView2 is null) return true;
            try
            {
                var sx = x.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var sy = y.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var script = $"(() => {{ try {{ window.scrollTo({sx}, {sy}); return true; }} catch(e) {{ return false; }} }})();";
                await _control.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch { }
            return true;
        });

    private static (double X, double Y) ParseScroll(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "null" || json == "undefined") return (0, 0);
        try
        {
            using var outer = System.Text.Json.JsonDocument.Parse(json);
            if (outer.RootElement.ValueKind != System.Text.Json.JsonValueKind.String) return (0, 0);
            var inner = outer.RootElement.GetString();
            if (string.IsNullOrEmpty(inner)) return (0, 0);
            using var doc = System.Text.Json.JsonDocument.Parse(inner);
            var x = doc.RootElement.TryGetProperty("x", out var xv) ? xv.GetDouble() : 0;
            var y = doc.RootElement.TryGetProperty("y", out var yv) ? yv.GetDouble() : 0;
            return (x, y);
        }
        catch { return (0, 0); }
    }

    public Task<PageContext> ExtractPageContextAsync(CancellationToken ct = default) =>
        _ui.RunAsync(async () =>
        {
            if (_control?.CoreWebView2 is null)
            {
                return new PageContext(_lastUrl, _lastTitle, _cachedDescription, _cachedSelection, _cachedBody, CurrentFaviconUrl, _scrollX, _scrollY);
            }
            var title = _control.CoreWebView2.DocumentTitle;
            var url = _control.CoreWebView2.Source;
            try
            {
                // We pass back a plain object (not JSON.stringify) so we don't
                // need to unwrap the outer string in ParseContext. The script
                // also captures scroll position so the saved metadata is complete.
                const string script = "(() => { try { return { d: document.contentDescription || (document.querySelector('meta[name=description]')||{}).content || '', sel: (window.getSelection && window.getSelection().toString()) || '', ex: (document.body && (document.body.innerText||'').slice(0, 2000)) || '', x: window.scrollX || 0, y: window.scrollY || 0 }; } catch(e) { return null; } })();";
                var result = await _control.CoreWebView2.ExecuteScriptAsync(script);
                return Security.PageContextParser.Parse(result, url, title);
            }
            catch
            {
                return new PageContext(url, title, _cachedDescription, _cachedSelection, _cachedBody, CurrentFaviconUrl, _scrollX, _scrollY);
            }
        });

    public Task<byte[]?> CapturePreviewAsync(int maxWidth, int maxHeight, CancellationToken ct = default) =>
        _ui.RunAsync<byte[]?>(async () =>
        {
            if (_control?.CoreWebView2 is null) return null;
            try
            {
                var stream = new InMemoryRandomAccessStream();
                await _control.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                using var input = stream.AsStreamForRead();
                using var ms = new System.IO.MemoryStream();
                await input.CopyToAsync(ms, ct);
                return ms.ToArray();
            }
            catch { return null; }
        });

    public Task OpenDevToolsAsync(CancellationToken ct = default) =>
        _ui.RunAsync(() =>
        {
            if (_control?.CoreWebView2 is not null)
            {
                try { _control.CoreWebView2.OpenDevToolsWindow(); } catch { }
            }
            return Task.FromResult(true);
        });

    public Task SetZoomAsync(double zoom, CancellationToken ct = default) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _ui.RunAsync(() => { try { _control?.Close(); } catch { } _control = null!; return Task.FromResult(true); }).ConfigureAwait(false); }
        catch { }
        await Task.CompletedTask;
    }
}

/// <summary>Search provider URL template. Used by the WebView2 view's ResolveUrlAsync.</summary>
public static class SearchProviderSettings
{
    public static string CurrentUrl { get; set; } = "https://duckduckgo.com/?q={q}";
}

internal static class WebView2Omnibox
{
    public static string? Resolve(string input) => OmniboxResolver.Resolve(input, SearchProviderSettings.CurrentUrl);
}
