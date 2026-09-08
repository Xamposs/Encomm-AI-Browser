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
    public async Task<IBrowserView> CreateViewAsync(Guid tabId, CancellationToken ct = default)
    {
        var env = await CreateEnvironmentAsync(ct).ConfigureAwait(false);
        SetEnvironment(env);
        var view = new WebView2BrowserView(tabId, env, _blocker, _ui);
        await view.InitializeAsync(ct).ConfigureAwait(false);
        return view;
    }

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync(CancellationToken ct = default)
    {
        var userData = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Encomm", "Encomm-AI-Browser", "UserData");
        System.IO.Directory.CreateDirectory(userData);
        // The C# projection surface for CoreWebView2Environment.CreateAsync
        // varies across the WebView2 package versions bundled with
        // WinAppSDK releases. Resolve the overload at runtime so one
        // binary works across the 1.0.29xx–1.0.37xx family: prefer the
        // explicit 3-arg form, fall back to the 1-arg user-data form.
        var t = typeof(CoreWebView2Environment);
        var m3 = t.GetMethod("CreateAsync", new[] { typeof(string), typeof(string), typeof(CoreWebView2EnvironmentOptions) });
        if (m3 is not null)
        {
            var task = (System.Threading.Tasks.Task<CoreWebView2Environment>)m3.Invoke(null, new object?[] { null, userData, null })!;
            return await task.ConfigureAwait(false);
        }
        var m1 = t.GetMethod("CreateAsync", new[] { typeof(string) });
        if (m1 is not null)
        {
            var task = (System.Threading.Tasks.Task<CoreWebView2Environment>)m1.Invoke(null, new object?[] { userData })!;
            return await task.ConfigureAwait(false);
        }
        throw new InvalidOperationException("CoreWebView2Environment.CreateAsync overload not found.");
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
    public string CurrentUrl => _control?.Source?.ToString() ?? _lastUrl;
    public string CurrentTitle => _control?.CoreWebView2?.DocumentTitle ?? _lastTitle;
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
    /// </summary>
    public async Task<bool> SuspendAsync(CancellationToken ct = default)
    {
        if (_disposed) return false;
        if (_state != ViewLifecycleState.Live) return false;
        if (_control?.CoreWebView2 is null) return false;
        // TrySuspendAsync requires the host to not be visible. All UI
        // property access is marshaled through the injected dispatcher.
        var prevVisibility = await _ui.RunAsync(() => Task.FromResult(_control.Visibility)).ConfigureAwait(false);
        await _ui.RunAsync(() => { _control.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed; return Task.FromResult(true); }).ConfigureAwait(false);
        try
        {
            var ok = await _control.CoreWebView2.TrySuspendAsync();
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
    /// visibility and calls CoreWebView2.Resume(). Returns true when the
    /// renderer is Live afterwards.
    /// </summary>
    public async Task<bool> ResumeAsync(CancellationToken ct = default)
    {
        if (_disposed) return false;
        if (_state != ViewLifecycleState.Warm) return _state == ViewLifecycleState.Live;
        if (_control?.CoreWebView2 is null) return false;
        try
        {
            if (_control.CoreWebView2.IsSuspended)
            {
                _control.CoreWebView2.Resume();
            }
            await _ui.RunAsync(() => { _control.Visibility = Microsoft.UI.Xaml.Visibility.Visible; return Task.FromResult(true); }).ConfigureAwait(false);
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
        if (_control?.CoreWebView2 is null) return false;
        if (_state != ViewLifecycleState.Live && _state != ViewLifecycleState.Warm) return false;
        try
        {
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
    }

    /// <summary>
    /// Ghost the renderer (any non-Ghost → Ghost). Closes the WebView2
    /// control and releases the underlying CoreWebView2. The control
    /// reference is dropped so the GC and underlying Chromium process can
    /// free the memory.
    /// </summary>
    public Task<bool> GhostAsync(CancellationToken ct = default)
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
    }

    public Task<NavigationResult> NavigateAsync(string url, CancellationToken ct = default)
    {
        if (_state == ViewLifecycleState.Ghost || _state == ViewLifecycleState.None)
        {
            return Task.FromResult(new NavigationResult(false, "renderer is not initialized"));
        }
        if (_control?.CoreWebView2 is null)
        {
            return Task.FromResult(new NavigationResult(false, "CoreWebView2 not available"));
        }
        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.FromResult(new NavigationResult(false, "empty URL"));
        }
        try
        {
            _control.CoreWebView2.Navigate(url);
            return Task.FromResult(new NavigationResult(true));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new NavigationResult(false, ex.GetType().Name + ": " + ex.Message));
        }
    }

    public Task<string?> ResolveUrlAsync(string userInput, CancellationToken ct = default)
        => Task.FromResult<string?>(WebView2Omnibox.Resolve(userInput));

    public Task ReloadAsync(CancellationToken ct = default)
    {
        if (_control?.CoreWebView2 is not null)
        {
            try { _control.CoreWebView2.Reload(); } catch { }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (_control?.CoreWebView2 is not null)
        {
            try { _control.CoreWebView2.Stop(); } catch { }
        }
        return Task.CompletedTask;
    }

    public Task GoBackAsync(CancellationToken ct = default)
    {
        if (_control?.CanGoBack == true)
        {
            try { _control.GoBack(); } catch { }
        }
        return Task.CompletedTask;
    }

    public Task GoForwardAsync(CancellationToken ct = default)
    {
        if (_control?.CanGoForward == true)
        {
            try { _control.GoForward(); } catch { }
        }
        return Task.CompletedTask;
    }

    public async Task<(double X, double Y)> GetScrollAsync(CancellationToken ct = default)
    {
        if (_control?.CoreWebView2 is null) return (0, 0);
        try
        {
            const string script = "(() => { try { return { x: window.scrollX || 0, y: window.scrollY || 0 }; } catch(e) { return null; } })();";
            var result = await _control.CoreWebView2.ExecuteScriptAsync(script);
            return ParseScroll(result);
        }
        catch { return (0, 0); }
    }

    public async Task SetScrollAsync(double x, double y, CancellationToken ct = default)
    {
        if (_control?.CoreWebView2 is null) return;
        try
        {
            var sx = x.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var sy = y.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var script = $"(() => {{ try {{ window.scrollTo({sx}, {sy}); return true; }} catch(e) {{ return false; }} }})();";
            await _control.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch { }
    }

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

    public async Task<PageContext> ExtractPageContextAsync(CancellationToken ct = default)
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
            return PageContextParser.Parse(result, url, title);
        }
        catch
        {
            return new PageContext(url, title, _cachedDescription, _cachedSelection, _cachedBody, CurrentFaviconUrl, _scrollX, _scrollY);
        }
    }

    public async Task<byte[]?> CapturePreviewAsync(int maxWidth, int maxHeight, CancellationToken ct = default)
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
    }

    public Task OpenDevToolsAsync(CancellationToken ct = default)
    {
        if (_control?.CoreWebView2 is not null)
        {
            try { _control.CoreWebView2.OpenDevToolsWindow(); } catch { }
        }
        return Task.CompletedTask;
    }

    public Task SetZoomAsync(double zoom, CancellationToken ct = default) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_control is not null) _control.Close(); } catch { }
        _control = null!;
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
