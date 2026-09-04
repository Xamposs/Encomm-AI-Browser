using System.Runtime.Versioning;
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

    public WebView2Engine(IRequestBlocker blocker)
    {
        _blocker = blocker;
        try { RuntimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { RuntimeVersion = null; }
    }

    public async Task<IBrowserView> CreateViewAsync(Guid tabId, CancellationToken ct = default)
    {
        var env = await CreateEnvironmentAsync().ConfigureAwait(false);
        var view = new WebView2BrowserView(tabId, env, _blocker);
        return view;
    }

    private static Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var userData = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Encomm", "Encomm-AI-Browser", "UserData");
        System.IO.Directory.CreateDirectory(userData);

        // The Microsoft.Web.WebView2 .NET projection that ships with
        // WinAppSDK 2.2 has the 3-arg CreateAsync available at runtime
        // but the C#/WinRT projection metadata (which the C# compiler
        // consumes) has a partial surface. The reliable call is the
        // 1-arg form (userDataFolder). We try the 1-arg first and
        // fall back to the 3-arg via reflection.
        var t = typeof(CoreWebView2Environment);
        var m1 = t.GetMethod("CreateAsync", new[] { typeof(string) });
        if (m1 is not null)
        {
            return (Task<CoreWebView2Environment>)m1.Invoke(null, new object?[] { userData })!;
        }
        var m3 = t.GetMethod("CreateAsync", new[] { typeof(string), typeof(string), typeof(CoreWebView2EnvironmentOptions) });
        if (m3 is not null)
        {
            return (Task<CoreWebView2Environment>)m3.Invoke(null, new object?[] { null, userData, null })!;
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
    private Microsoft.UI.Xaml.Controls.WebView2? _control;
    private ViewLifecycleState _state = ViewLifecycleState.None;
    private bool _disposed;
    private string _lastUrl = "";
    private string _lastTitle = "";
    private bool _isLoading;
    private bool _shieldFilterInstalled;

    public WebView2BrowserView(Guid tabId, CoreWebView2Environment environment, IRequestBlocker blocker)
    {
        _tabId = tabId;
        _environment = environment;
        _blocker = blocker;
    }

    public Guid Id => _tabId;
    public ViewLifecycleState State => _state;
    public string CurrentUrl => _control?.Source?.ToString() ?? _lastUrl;
    public string CurrentTitle => _control?.CoreWebView2?.DocumentTitle ?? _lastTitle;
    public string? CurrentFaviconUrl { get; private set; }
    public bool CanGoBack => _control?.CanGoBack ?? false;
    public bool CanGoForward => _control?.CanGoForward ?? false;
    public bool IsLoading => _isLoading;
    public object HostElement => _control ?? throw new InvalidOperationException("WebView2 control not yet initialized.");

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
    /// Build the WinUI WebView2 control. Must be called on the UI thread
    /// before binding this view to a host element.
    /// </summary>
    public Microsoft.UI.Xaml.Controls.WebView2 Initialize()
    {
        if (_control is not null) return _control;
        _control = new Microsoft.UI.Xaml.Controls.WebView2
        {
            MinWidth = 1,
            MinHeight = 1,
        };
        return _control;
    }

    /// <summary>
    /// Ensure the underlying CoreWebView2 is initialized. Awaits the WinUI
    /// control's `EnsureCoreWebView2Async`, wires events, and installs
    /// Shield filters BEFORE the first navigation.
    /// </summary>
    public async Task EnsureCoreAsync(CancellationToken ct = default)
    {
        if (_control is null) throw new InvalidOperationException("Call Initialize() first.");
        if (_state >= ViewLifecycleState.Live) return;
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
            // Add the resource filter BEFORE the first NavigationStarting event
            // fires. If we wait for NavigationCompleted, subresources of the
            // initial document may bypass Shield.
            _control.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            _shieldFilterInstalled = true;
        }
        catch { /* will retry on first navigation */ }
    }

    private void WireEvents()
    {
        if (_control is null) return;
        var cv = _control.CoreWebView2;
        if (cv is null) return;

        cv.Settings.AreDevToolsEnabled = true;
        cv.Settings.AreDefaultContextMenusEnabled = true;
        cv.Settings.IsZoomControlEnabled = true;

        cv.NewWindowRequested += (s, args) =>
        {
            args.Handled = true;
            NewWindowRequested?.Invoke(this, new NewWindowRequestEventArgs
            {
                Url = args.Uri,
                IsUserInitiated = args.IsUserInitiated,
                OpenInPlace = _ => { },
                OpenInNewTab = () => { },
                Decline = () => { }
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
                Allow = () =>
                {
                    try { args.State = CoreWebView2PermissionState.Allow; } catch { }
                },
                Deny = () =>
                {
                    try { args.State = CoreWebView2PermissionState.Deny; } catch { }
                }
            });
        };
        cv.DownloadStarting += (s, args) =>
        {
            args.Handled = true;
            var dl = args.DownloadOperation;
            // Real download path: stage into %USERPROFILE%\Documents\Encomm\Downloads
            // unless the user later overrides this through the UI.
            var downloadsDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Encomm", "Downloads");
            try { System.IO.Directory.CreateDirectory(downloadsDir); } catch { }
            // ResultFilePath is read-only on the *download operation*; we can set
            // Handled and assign ResultFilePath on the *args* if the API allows,
            // otherwise the download is left to the default temp location.
            var suggested = dl.ResultFilePath ?? System.IO.Path.Combine(downloadsDir, "download.bin");
            DownloadRequested?.Invoke(this, new DownloadEventArgs
            {
                SuggestedFileName = suggested,
                Url = dl.Uri,
                Accept = _ =>
                {
                    // Phase 2: expose a "Save As" dialog. For now we accept
                    // the default path; the download will be left to WebView2.
                },
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
            AudioStateChanged?.Invoke(this, new AudioEventArgs { Playing = cv.IsDocumentPlayingAudio, Muted = cv.IsMuted });
        cv.NavigationStarting += (s, args) =>
        {
            // Make sure the filter is in place before the navigation fires.
            InstallShieldFilter();
            NavigationStarting?.Invoke(this, new NavigationStartingEventArgs
            {
                Url = args.Uri,
                IsUserInitiated = args.IsUserInitiated,
                IsMainFrame = true
            });
        };
        cv.NavigationCompleted += (s, args) =>
        {
            _isLoading = false;
            _lastUrl = cv.Source;
            NavigationCompleted?.Invoke(this, new NavigationCompletedEventArgs
            {
                Url = cv.Source,
                HttpStatus = (int)args.HttpStatusCode,
                Success = args.IsSuccess,
                ErrorMessage = args.IsSuccess ? null : args.WebErrorStatus.ToString()
            });
            LoadingStateChanged?.Invoke(this, new LoadingStateEventArgs { IsLoading = false });
        };
        cv.SourceChanged += (s, args) =>
        {
            _isLoading = !cv.Source.Contains("about:blank", StringComparison.OrdinalIgnoreCase);
            LoadingStateChanged?.Invoke(this, new LoadingStateEventArgs { IsLoading = _isLoading });
        };
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

    public Task NavigateAsync(string url, CancellationToken ct = default)
    {
        if (_control?.CoreWebView2 is null) return Task.CompletedTask;
        try
        {
            _control.CoreWebView2.Navigate(url);
            _isLoading = true;
            LoadingStateChanged?.Invoke(this, new LoadingStateEventArgs { IsLoading = true });
        }
        catch { }
        return Task.CompletedTask;
    }

    public Task<string?> ResolveUrlAsync(string userInput, CancellationToken ct = default)
        => Task.FromResult<string?>(WebView2Omnibox.Resolve(userInput));

    public Task ReloadAsync(CancellationToken ct = default)
    {
        try { _control?.CoreWebView2?.Reload(); } catch { }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        try { _control?.CoreWebView2?.Stop(); } catch { }
        return Task.CompletedTask;
    }

    public Task GoBackAsync(CancellationToken ct = default)
    {
        try { if (_control?.CanGoBack == true) _control.GoBack(); } catch { }
        return Task.CompletedTask;
    }

    public Task GoForwardAsync(CancellationToken ct = default)
    {
        try { if (_control?.CanGoForward == true) _control.GoForward(); } catch { }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Awaitable navigation completion. The returned task completes when the
    /// next NavigationCompleted event fires (or fails). Used by Ghost
    /// restoration to navigate without busy-waiting.
    /// </summary>
    public Task AwaitNavigationCompletedAsync(int timeoutMs = 15000, CancellationToken ct = default)
    {
        if (_control?.CoreWebView2 is null)
            return Task.FromResult(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<NavigationCompletedEventArgs>? handler = null;
        handler = (s, e) =>
        {
            if (handler is not null) NavigationCompleted -= handler;
            tcs.TrySetResult(e.Success);
        };
        NavigationCompleted += handler;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        cts.Token.Register(() =>
        {
            if (handler is not null) NavigationCompleted -= handler;
            tcs.TrySetResult(false);
        });
        return tcs.Task;
    }

    /// <summary>
    /// Bring a Ghosted or Warmed renderer back to Live. For a Ghosted
    /// renderer the caller must have already created a new control via
    /// Initialize(). For a Warmed renderer, this calls CoreWebView2.Resume().
    /// </summary>
    public async Task WakeAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        if (_control is null) return;
        if (_state == ViewLifecycleState.Ghost || _state == ViewLifecycleState.None)
        {
            await EnsureCoreAsync(ct).ConfigureAwait(false);
            return;
        }
        if (_state == ViewLifecycleState.Warm)
        {
            try
            {
                if (_control?.CoreWebView2 is { IsSuspended: true } cv)
                {
                    cv.Resume();
                }
                _state = ViewLifecycleState.Live;
            }
            catch
            {
                // Resume can fail if the controller was already destroyed.
                // Fall back to recreating the control.
                _state = ViewLifecycleState.None;
                if (_control is not null)
                {
                    try { _control.Close(); } catch { }
                }
                _control = null;
            }
        }
    }

    /// <summary>
    /// ACTUAL suspension via CoreWebView2.TrySuspendAsync. The control is
    /// also hidden (Visibility = Collapsed) which is a requirement of
    /// TrySuspendAsync per the SDK docs. On success transitions to Warm.
    /// </summary>
    public async Task SuspendAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        if (_state != ViewLifecycleState.Live) return;
        if (_control?.CoreWebView2 is null) return;
        var cv = _control.CoreWebView2;
        // TrySuspendAsync requires the host to not be visible.
        var prevVisibility = _control.Visibility;
        _control.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        try
        {
            var ok = await cv.TrySuspendAsync();
            if (ok)
            {
                _state = ViewLifecycleState.Warm;
                LoadingStateChanged?.Invoke(this, new LoadingStateEventArgs { IsLoading = false });
            }
            else
            {
                // Failure: restore visibility and stay Live.
                _control.Visibility = prevVisibility;
            }
        }
        catch
        {
            _control.Visibility = prevVisibility;
        }
    }

    /// <summary>
    /// ACTUAL ghosting — close the WebView2 control and release the
    /// CoreWebView2. The control reference is dropped so the GC and
    /// underlying Chromium process can free the memory.
    /// </summary>
    public Task GhostAsync(CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;
        try
        {
            if (_control is not null)
            {
                try { _control.Close(); } catch { }
                _control = null;
            }
        }
        catch { }
        _shieldFilterInstalled = false;
        _state = ViewLifecycleState.Ghost;
        LoadingStateChanged?.Invoke(this, new LoadingStateEventArgs { IsLoading = false });
        return Task.CompletedTask;
    }

    public async Task<PageContext> ExtractPageContextAsync(CancellationToken ct = default)
    {
        if (_control?.CoreWebView2 is null) return new PageContext(_lastUrl, _lastTitle, null, null, null);
        var title = _control.CoreWebView2.DocumentTitle;
        var url = _control.CoreWebView2.Source;
        try
        {
            // NOTE: ExecuteScriptAsync returns the script result as a JSON-encoded
            // string. We want a plain object back, so we return the object
            // directly (not via JSON.stringify) and let the host JSON-parse it.
            var script = "(() => { try { return { d: document.contentDescription || (document.querySelector('meta[name=description]')||{}).content || '', sel: (window.getSelection && window.getSelection().toString()) || '', ex: (document.body && (document.body.innerText||'').slice(0, 2000)) || '' }; } catch(e) { return null; } })();";
            var result = await _control.CoreWebView2.ExecuteScriptAsync(script);
            return PageContextParser.Parse(result, url, title);
        }
        catch
        {
            return new PageContext(url, title, null, null, null);
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
        try { _control?.CoreWebView2?.OpenDevToolsWindow(); } catch { }
        return Task.CompletedTask;
    }

    public Task SetZoomAsync(double zoom, CancellationToken ct = default) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_control is not null) _control.Close(); } catch { }
        _control = null;
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
    public static string? Resolve(string input) => Encomm.Browser.Engine.Abstractions.OmniboxResolver.Resolve(input, SearchProviderSettings.CurrentUrl);
}

/// <summary>
/// Parses a WebView2 ExecuteScriptAsync result into a PageContext.
///
/// ExecuteScriptAsync always returns a JSON-encoded string. The script in
/// this engine adapter returns a plain object — so the result is
/// <c>"{\"d\":\"...\",\"sel\":\"...\",\"ex\":\"...\"}"</c> (the object, JSON-encoded).
/// We unwrap the outer JSON string and parse the inner object.
///
/// A literal <c>"null"</c> result (the script returned null) becomes an
/// empty PageContext. A literal <c>"undefined"</c> also becomes empty.
/// </summary>
public static class PageContextParser
{
    public static PageContext Parse(string? jsonResult, string url, string title)
    {
        if (string.IsNullOrEmpty(jsonResult) || jsonResult == "null" || jsonResult == "undefined")
            return new PageContext(url, title, null, null, null);

        // Step 1: unwrap the outer JSON-encoded string.
        string? inner;
        try
        {
            using var outer = System.Text.Json.JsonDocument.Parse(jsonResult);
            if (outer.RootElement.ValueKind != System.Text.Json.JsonValueKind.String)
                return new PageContext(url, title, null, null, null);
            inner = outer.RootElement.GetString();
        }
        catch { return new PageContext(url, title, null, null, null); }

        if (string.IsNullOrEmpty(inner)) return new PageContext(url, title, null, null, null);

        // Step 2: parse the inner object.
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(inner);
            var root = doc.RootElement;
            var d = root.TryGetProperty("d", out var dv) ? dv.GetString() : null;
            var sel = root.TryGetProperty("sel", out var sv) ? sv.GetString() : null;
            var ex = root.TryGetProperty("ex", out var ev) ? ev.GetString() : null;
            return new PageContext(url, title, TextSanitizer.TrimExcerpt(d),
                TextSanitizer.TrimSelection(sel),
                TextSanitizer.TrimExcerpt(ex));
        }
        catch
        {
            return new PageContext(url, title, null, null, null);
        }
    }
}