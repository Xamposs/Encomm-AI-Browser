namespace Encomm.Browser.Engine.Abstractions;

/// <summary>
/// The renderer-process state for a browser view. The product never
/// references the engine SDK directly; it only manipulates these states.
/// </summary>
public enum ViewLifecycleState
{
    /// <summary>No view has been instantiated yet.</summary>
    None,
    /// <summary>Renderer object exists but CoreWebView2 not yet initialized.</summary>
    Initializing,
    /// <summary>Fully alive renderer.</summary>
    Live,
    /// <summary>Renderer suspended where the engine supports it.</summary>
    Warm,
    /// <summary>Renderer destroyed. Only metadata kept.</summary>
    Ghost
}

/// <summary>Resource category, used by Shield to apply blocking rules.</summary>
public enum ResourceCategory
{
    Document,
    Script,
    Style,
    Image,
    Font,
    Media,
    Xhr,
    Fetch,
    WebSocket,
    Other
}

/// <summary>Result of a Shield blocking decision.</summary>
public sealed record BlockDecision(bool Allow, string? Reason = null);

/// <summary>Lightweight, engine-agnostic page context.</summary>
public sealed record PageContext(
    string Url,
    string Title,
    string? Description,
    string? SelectedText,
    string? BodyExcerpt,
    string? FaviconUrl = null,
    double ScrollX = 0,
    double ScrollY = 0);

/// <summary>Permissions the page may request.</summary>
public enum PermissionKind
{
    Camera,
    Microphone,
    Geolocation,
    Notifications,
    ClipboardRead,
    ClipboardWrite,
    Other
}

/// <summary>
/// Result of a navigation attempt. Returned by every navigation call so
/// the caller can react to failures. A navigation is NEVER silently
/// dropped; if the renderer is not ready or the URL cannot be parsed,
/// the call reports the reason in <see cref="Reason"/>.
/// </summary>
public sealed record NavigationResult(bool Accepted, string? Reason = null);

/// <summary>Lifecycle events surfaced from the engine.</summary>
public interface IBrowserView : IAsyncDisposable
{
    Guid Id { get; }
    ViewLifecycleState State { get; }

    string CurrentUrl { get; }
    string CurrentTitle { get; }
    string? CurrentFaviconUrl { get; }

    bool CanGoBack { get; }
    bool CanGoForward { get; }
    bool IsLoading { get; }
    bool IsDocumentPlayingAudio { get; }

    /// <summary>
    /// The WinUI element that should be parented in the browser host tree.
    /// Throws InvalidOperationException if the renderer has not yet
    /// completed initialization; callers should not ask for the host
    /// element until <see cref="BrowserRuntime.GetOrCreateAsync"/>
    /// completes successfully.
    /// </summary>
    object HostElement { get; }

    event EventHandler<NavigationStartingEventArgs>? NavigationStarting;
    event EventHandler<NavigationCompletedEventArgs>? NavigationCompleted;
    event EventHandler<TitleChangedEventArgs>? TitleChanged;
    event EventHandler<FaviconChangedEventArgs>? FaviconChanged;
    event EventHandler<LoadingStateEventArgs>? LoadingStateChanged;
    event EventHandler<AudioEventArgs>? AudioStateChanged;
    event EventHandler<DownloadEventArgs>? DownloadRequested;
    event EventHandler<PermissionRequestEventArgs>? PermissionRequested;
    event EventHandler<NewWindowRequestEventArgs>? NewWindowRequested;
    event EventHandler<ResourceBlockedEventArgs>? ResourceBlocked;
    event EventHandler<RenderErrorEventArgs>? RenderError;

    /// <summary>
    /// Navigate the live renderer to the URL. Must be called on a renderer
    /// in the <see cref="ViewLifecycleState.Live"/> or
    /// <see cref="ViewLifecycleState.Warm"/> state.
    /// </summary>
    Task<NavigationResult> NavigateAsync(string url, CancellationToken ct = default);

    Task<string?> ResolveUrlAsync(string userInput, CancellationToken ct = default);
    Task ReloadAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task GoBackAsync(CancellationToken ct = default);
    Task GoForwardAsync(CancellationToken ct = default);

    /// <summary>
    /// Return the current scroll offset, best-effort. Returns (0, 0) when
    /// no renderer is ready or the page did not report a value.
    /// </summary>
    Task<(double X, double Y)> GetScrollAsync(CancellationToken ct = default);

    /// <summary>
    /// Set the scroll offset. Used during Ghost restore to bring the user
    /// back to where they were.
    /// </summary>
    Task SetScrollAsync(double x, double y, CancellationToken ct = default);

    /// <summary>
    /// Capture the current page context (title, description, selection,
    /// body excerpt, scroll). Used before Ghosting to preserve useful
    /// metadata.
    /// </summary>
    Task<PageContext> ExtractPageContextAsync(CancellationToken ct = default);

    Task<byte[]?> CapturePreviewAsync(int maxWidth, int maxHeight, CancellationToken ct = default);
    Task OpenDevToolsAsync(CancellationToken ct = default);
    Task SetZoomAsync(double zoom, CancellationToken ct = default);

    /// <summary>
    /// Resume a suspended renderer (Warm → Live). Restores control
    /// visibility and calls the engine resume primitive. Returns true
    /// when the renderer is Live afterwards.
    /// </summary>
    Task<bool> ResumeAsync(CancellationToken ct = default);

    /// <summary>
    /// Best-effort detection of potentially unsaved form state
    /// (changed inputs, textareas, contenteditable). Used to protect a
    /// tab from *automatic* Ghosting. Never blocks manual Ghost.
    /// </summary>
    Task<bool> HasUnsavedFormStateAsync(CancellationToken ct = default);

    /// <summary>
    /// Suspend the renderer (Live → Warm). Best-effort: returns true on
    /// success. Caller is responsible for persisting scroll / metadata
    /// before calling.
    /// </summary>
    Task<bool> SuspendAsync(CancellationToken ct = default);

    /// <summary>
    /// Destroy the renderer entirely. Transitions to Ghost. Releases
    /// the underlying WinUI element. Caller has already persisted any
    /// required metadata.
    /// </summary>
    Task<bool> GhostAsync(CancellationToken ct = default);
}

public sealed class NavigationStartingEventArgs : EventArgs
{
    public required string Url { get; init; }
    public required bool IsUserInitiated { get; init; }
    public required bool IsMainFrame { get; init; }
}

public sealed class NavigationCompletedEventArgs : EventArgs
{
    public required string Url { get; init; }
    public required int HttpStatus { get; init; }
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class TitleChangedEventArgs : EventArgs { public required string Title { get; init; } }
public sealed class FaviconChangedEventArgs : EventArgs { public required string Url { get; init; } }
public sealed class LoadingStateEventArgs : EventArgs { public required bool IsLoading { get; init; } }

public sealed class AudioEventArgs : EventArgs
{
    public required bool Playing { get; init; }
    public required bool Muted { get; init; }
}

public sealed class DownloadEventArgs : EventArgs
{
    public required string SuggestedFileName { get; init; }
    public required string Url { get; init; }
    public required Action<string> Accept;
    public required Action Decline;
}

public sealed class PermissionRequestEventArgs : EventArgs
{
    public required PermissionKind Kind { get; init; }
    public required string Origin { get; init; }
    public required Action Allow;
    public required Action Deny;
}

public sealed class NewWindowRequestEventArgs : EventArgs
{
    public required string Url { get; init; }
    public required bool IsUserInitiated { get; init; }
    public required Action OpenInNewTab;
    public required Action Decline;
}

public sealed class ResourceBlockedEventArgs : EventArgs
{
    public required string Url { get; init; }
    public required ResourceCategory Category { get; init; }
    public required string Reason { get; init; }
}

public sealed class RenderErrorEventArgs : EventArgs
{
    public required string Message { get; init; }
    public required string? FailedUrl { get; init; }
    public Exception? Exception { get; init; }
}

public enum WebViewProcessKind
{
    Browser,
    Renderer,
    Gpu,
    Utility
}

public sealed record WebViewProcessInfo(int ProcessId, WebViewProcessKind Kind, long WorkingSet64);

/// <summary>
/// Abstraction over a UI-thread dispatcher. The engine abstraction
/// stays UI-framework-agnostic, so this is just "post work to the
/// UI thread" without referencing Microsoft.UI.Dispatching.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when the current thread is the UI thread.</summary>
    bool HasThreadAccess { get; }

    /// <summary>Post synchronous work to the UI thread.</summary>
    void Post(Action action);

    /// <summary>
    /// Run async work on the UI thread and await its result.
    /// Implementations must marshal the delegate to the UI thread even
    /// when the caller is on a background thread.
    /// </summary>
    Task<T> RunAsync<T>(Func<Task<T>> func);
}

/// <summary>
/// Process-wide engine factory. Owns the shared environment / user data
/// folder, and creates per-tab views.
/// </summary>
public interface IBrowserEngine : IAsyncDisposable
{
    /// <summary>Engine identifier for diagnostics ("webview2" today).</summary>
    string EngineId { get; }

    /// <summary>The installed runtime version, where applicable.</summary>
    string? RuntimeVersion { get; }

    /// <summary>
    /// Create a view for the given tab id. The returned view must be
    /// ready to host and navigate; the engine is responsible for
    /// initializing the underlying control.
    /// </summary>
    Task<IBrowserView> CreateViewAsync(Guid tabId, CancellationToken ct = default);

    /// <summary>
    /// Return the engine's child process memory tree. Adapters that
    /// don't support process introspection return an empty list.
    /// </summary>
    IReadOnlyList<WebViewProcessInfo> GetWebViewProcessInfos();
}

/// <summary>
/// Stateless URL vs search classification. Lives in the engine
/// abstraction so it is unit-testable without the WinUI dependency.
/// </summary>
public static class OmniboxResolver
{
    public static string? Resolve(string input, string? searchProviderUrl = null)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var trimmed = input.Trim();
        if (trimmed.StartsWith("encomm://", StringComparison.OrdinalIgnoreCase)) return trimmed;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var abs) &&
            (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
            return abs.ToString();
        if (LooksLikeDomain(trimmed))
            return "https://" + trimmed;
        var provider = string.IsNullOrEmpty(searchProviderUrl) ? "https://duckduckgo.com/?q={q}" : searchProviderUrl;
        return provider.Replace("{q}", Uri.EscapeDataString(trimmed));
    }

    private static bool LooksLikeDomain(string s)
    {
        if (s.Contains(' ')) return false;
        var dot = s.IndexOf('.');
        if (dot <= 0 || dot == s.Length - 1) return false;
        foreach (var c in s) if (c == ' ' || c == '/' || c == '\\') return false;
        return true;
    }
}
