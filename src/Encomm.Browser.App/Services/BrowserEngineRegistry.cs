using Microsoft.Extensions.Logging;
using Encomm.Browser.Engine.Abstractions;
using Encomm.Browser.Engine.WebView2;
using Encomm.Browser.App.Services;
using Encomm.Browser.Core.Storage;
using Encomm.Browser.Shield;

namespace Encomm.Browser.App.Services;

/// <summary>
/// Owns the per-tab engine views. The renderer abstraction is invisible
/// to UI / Tabs / Workspaces; this registry is the only place that
/// actually keeps a WebView2 control alive.
/// </summary>
public sealed class BrowserEngineRegistry : IDisposable
{
    private readonly IRequestBlocker _blocker;
    private readonly ILogger<BrowserEngineRegistry> _log;
    private readonly Dictionary<Guid, IBrowserView> _views = new();
    private readonly object _initGate = new();
    private IBrowserEngine? _engine;
    private bool _initStarted;
    private bool _disposed;

    public BrowserEngineRegistry(IRequestBlocker blocker, ILogger<BrowserEngineRegistry> log)
    {
        _blocker = blocker;
        _log = log;
    }

    private IBrowserEngine EnsureEngine()
    {
        if (_engine is not null) return _engine;
        lock (_initGate)
        {
            if (_engine is not null) return _engine;
            if (!_initStarted)
            {
                _initStarted = true;
                _ = Task.Run(InitializeAsync);
            }
            return _engine ?? (IBrowserEngine)new NotReadyEngine();
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var userData = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Encomm", "Encomm-AI-Browser", "UserData");
            System.IO.Directory.CreateDirectory(userData);

            // The CreateAsync overloads on CoreWebView2Environment vary between
            // SDK versions. We probe by reflection for whichever signatures
            // are available in this binary.
            var env = await TryCreateEnvironmentAsync(userData).ConfigureAwait(false);
            if (env is null)
            {
                _log.LogWarning("CoreWebView2Environment.CreateAsync could not be located; per-view defaults will be used.");
                lock (_initGate) _engine = new LazyPassThroughEngine();
                return;
            }
            lock (_initGate) _engine = new WebView2Engine(env, _blocker);
            _log.LogInformation("Engine initialized: {Id}, runtime {Runtime}", _engine.EngineId, _engine.RuntimeVersion);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to initialize WebView2 environment");
        }
    }

    private static async Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment?> TryCreateEnvironmentAsync(string userData)
    {
        var t = typeof(Microsoft.Web.WebView2.Core.CoreWebView2Environment);
        var methods = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(m => m.Name == "CreateAsync")
            .ToList();
        // Try signatures in this order: 3-arg, 2-arg, 1-arg, 0-arg.
        foreach (var sig in new[] { new[] { typeof(string), typeof(string), typeof(Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions) },
                                       new[] { typeof(string), typeof(string) },
                                       new[] { typeof(string) },
                                       Array.Empty<Type>() })
        {
            var match = methods.FirstOrDefault(m => m.GetParameters().Length == sig.Length
                && m.GetParameters().Zip(sig, (p, s) => s == typeof(object) || p.ParameterType == s || p.ParameterType.IsAssignableFrom(s)).All(b => b));
            if (match is null) continue;
            object?[] args = sig.Length switch
            {
                3 => new object?[] { null, userData, Activator.CreateInstance(sig[2]) },
                2 => new object?[] { null, userData },
                1 => new object?[] { userData },
                _ => Array.Empty<object?>()
            };
            try
            {
                var task = (System.Threading.Tasks.Task)match.Invoke(null, args)!;
                await task.ConfigureAwait(false);
                var resultProp = task.GetType().GetProperty("Result");
                return resultProp?.GetValue(task) as Microsoft.Web.WebView2.Core.CoreWebView2Environment;
            }
            catch (System.Reflection.TargetInvocationException tex) when (tex.InnerException is MissingMethodException)
            {
                continue;
            }
            catch
            {
                continue;
            }
        }
        return null;
    }

    public string EngineId => _engine?.EngineId ?? "initializing";
    public string? RuntimeVersion => _engine?.RuntimeVersion;

    public IBrowserView? GetOrCreate(TabRecord tab)
    {
        var engine = EnsureEngine();
        if (engine is NotReadyEngine) return null;
        if (_views.TryGetValue(tab.Id, out var existing)) return existing;
        var view = engine.CreateViewAsync(tab.Id).GetAwaiter().GetResult();
        if (view is WebView2BrowserView wv) wv.Initialize();
        _views[tab.Id] = view;
        return view;
    }

    public void DropView(Guid tabId)
    {
        if (!_views.TryGetValue(tabId, out var v)) return;
        try { v.GhostAsync().GetAwaiter().GetResult(); } catch { }
        try { v.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        _views.Remove(tabId);
    }

    public void DropAll()
    {
        foreach (var kv in _views.ToList()) DropView(kv.Key);
    }

    public IReadOnlyDictionary<Guid, IBrowserView> All => _views;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DropAll();
        try { _engine?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
    }

    private sealed class NotReadyEngine : IBrowserEngine
    {
        public string EngineId => "initializing";
        public string? RuntimeVersion => null;
        public Task<IBrowserView> CreateViewAsync(Guid tabId, CancellationToken ct = default)
            => Task.FromResult<IBrowserView>(new NotReadyView(tabId));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LazyPassThroughEngine : IBrowserEngine
    {
        public string EngineId => "webview2";
        public string? RuntimeVersion => Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
        public Task<IBrowserView> CreateViewAsync(Guid tabId, CancellationToken ct = default)
            => Task.FromResult<IBrowserView>(new NotReadyView(tabId));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NotReadyView : IBrowserView
    {
        public NotReadyView(Guid id) { Id = id; }
        public Guid Id { get; }
        public ViewLifecycleState State => ViewLifecycleState.None;
        public string CurrentUrl => "";
        public string CurrentTitle => "";
        public string? CurrentFaviconUrl => null;
        public bool CanGoBack => false;
        public bool CanGoForward => false;
        public bool IsLoading => false;
        public object HostElement => throw new InvalidOperationException("Engine not yet initialized");
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
        public Task NavigateAsync(string url, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> ResolveUrlAsync(string userInput, CancellationToken ct = default) => Task.FromResult<string?>(userInput);
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task GoBackAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task GoForwardAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WakeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SuspendAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task GhostAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<PageContext> ExtractPageContextAsync(CancellationToken ct = default) => Task.FromResult(new PageContext("", "", null, null, null));
        public Task<byte[]?> CapturePreviewAsync(int maxWidth, int maxHeight, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task OpenDevToolsAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SetZoomAsync(double zoom, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}