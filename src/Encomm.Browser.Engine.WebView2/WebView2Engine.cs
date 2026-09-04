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

    private readonly CoreWebView2Environment _environment;
    private readonly IRequestBlocker _blocker;

    public WebView2Engine(CoreWebView2Environment environment, IRequestBlocker blocker)
    {
        _environment = environment;
        _blocker = blocker;
        try { RuntimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { RuntimeVersion = null; }
    }

    public Task<IBrowserView> CreateViewAsync(Guid tabId, CancellationToken ct = default)
    {
        var view = new WebView2BrowserView(tabId, _environment, _blocker);
        return Task.FromResult<IBrowserView>(view);
    }

    public ValueTask DisposeAsync()
    {
        // CoreWebView2Environment doesn't expose IDisposable directly across
        // WinRT projections; the process exits cleanly when the WebView2
        // children are released. We do nothing here.
        return ValueTask.CompletedTask;
    }
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
    private bool _isLoading;

    public object HostElement => _control ?? throw new InvalidOperationException("WebView2 control not yet initialized. Call Initialize() before binding to UI.");

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

    public Microsoft.UI.Xaml.Controls.WebView2 Initialize()
    {
        if (_control is not null) return _control;
        _control = new Microsoft.UI.Xaml.Controls.WebView2
        {
            MinWidth = 1,
            MinHeight = 1,
        };
        _ = InitAsync();
        return _control;
    }

    private async Task InitAsync()
    {
        if (_control is null) return;
        try
        {
            await _control.EnsureCoreWebView2Async(_environment);
        }
        catch (Exception ex)
        {
            RenderError?.Invoke(this, new RenderErrorEventArgs { FailedUrl = null, Message = "WebView2 init failed", Exception = ex });
            return;
        }
        WireEvents();
        _state = ViewLifecycleState.Live;
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
                Allow = () => { },
                Deny = () => { }
            });
        };
        cv.DownloadStarting += (s, args) =>
        {
            args.Handled = true;
            DownloadRequested?.Invoke(this, new DownloadEventArgs
            {
                SuggestedFileName = args.DownloadOperation.ResultFilePath ?? args.DownloadOperation.Uri,
                Url = args.DownloadOperation.Uri,
                Accept = _ => { },
                Decline = () => { }
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
            try { cv.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All); } catch { }
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
        try
        {
            if (_control?.CoreWebView2 is not null)
            {
                _control.CoreWebView2.Navigate(url);
                _isLoading = true;
                LoadingStateChanged?.Invoke(this, new LoadingStateEventArgs { IsLoading = true });
            }
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

    public async Task WakeAsync(CancellationToken ct = default)
    {
        if (_state == ViewLifecycleState.Ghost || _control is null)
        {
            _control = null;
            var ctrl = Initialize();
            try { await Task.Delay(50, ct); } catch { }
            if (!string.IsNullOrEmpty(_lastUrl) && _lastUrl != "about:blank")
            {
                try { await NavigateAsync(_lastUrl, ct); } catch { }
            }
        }
        else if (_state == ViewLifecycleState.Warm)
        {
            _state = ViewLifecycleState.Live;
        }
    }

    public Task SuspendAsync(CancellationToken ct = default)
    {
        _state = ViewLifecycleState.Warm;
        LoadingStateChanged?.Invoke(this, new LoadingStateEventArgs { IsLoading = false });
        return Task.CompletedTask;
    }

    public Task GhostAsync(CancellationToken ct = default)
    {
        try { _control?.Close(); } catch { }
        _control = null;
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
            var script = "(() => { try { return JSON.stringify({ d: document.contentDescription || (document.querySelector('meta[name=description]')||{}).content || '', sel: (window.getSelection && window.getSelection().toString()) || '', ex: (document.body && (document.body.innerText||'').slice(0, 2000)) || '' }); } catch(e) { return ''; } })();";
            var result = await _control.CoreWebView2.ExecuteScriptAsync(script);
            return ParseContext(result, url, title);
        }
        catch
        {
            return new PageContext(url, title, null, null, null);
        }
    }

    private static PageContext ParseContext(string? json, string url, string title)
    {
        if (string.IsNullOrEmpty(json) || json == "null") return new PageContext(url, title, null, null, null);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
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

    public Task SetZoomAsync(double zoom, CancellationToken ct = default)
    {
        // WebView2 exposes zoom through its host control (ZoomFactor on WPF, or
        // WebView2Control.ZoomFactor on WinUI host). For Phase 1 we just no-op
        // and rely on Ctrl+/- hotkeys the WebView2 control handles natively.
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _control?.Close(); } catch { }
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