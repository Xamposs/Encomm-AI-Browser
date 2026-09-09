using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Encomm.Browser.App.Services;
using Encomm.Browser.App.ViewModels;
using Encomm.Browser.Core;
using Encomm.Browser.Core.Storage;
using Encomm.Browser.Engine.Abstractions;
using Encomm.Browser.Settings;
using Encomm.Browser.Shield;
using Encomm.Browser.Memory;
using Encomm.Browser.AI;
using Encomm.Browser.Security;
using Encomm.Browser.Developer;
using System;

namespace Encomm.Browser.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; set; } = null!;
    public static IUiDispatcher Ui { get; private set; } = null!;
    public static DispatcherQueue? MainDispatcherQueue { get; private set; }
    public static string[]? StartupArgs { get; private set; }

    /// <summary>
    /// Benchmark profile root. Null in normal browsing (uses
    /// BrowserPaths.Default()). Set by BeginBenchProfile before any
    /// storage or engine initializes; BuildServicesStatic then builds
    /// the ENTIRE service graph (SQLite, settings, secrets) inside the
    /// isolated folder.
    /// </summary>
    public static string? ProfileRoot { get; private set; }

    public static bool IsBenchMode => ProfileRoot is not null;

    /// <summary>
    /// Phase 2C items 19-21: isolate benchmark I/O from the real user
    /// profile. Creates BenchmarkProfiles/&lt;run-id&gt;, points the
    /// WebView2 user-data override there (honored by the engine's
    /// environment creation), and purges profiles older than 7 days.
    /// Logs to the normal log file (always safe to share).
    /// </summary>
    public static void BeginBenchProfile(string logPath)
    {
        try
        {
            var root = Path.Combine(
                BrowserPaths.Default().RootDirectory, "BenchmarkProfiles");
            Directory.CreateDirectory(root);
            // Purge stale profiles; never touch the live one (just made).
            foreach (var d in Directory.GetDirectories(root))
            {
                try
                {
                    if (Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddDays(-7))
                        Directory.Delete(d, true);
                }
                catch { }
            }
            var runId = "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
                + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var dir = Path.Combine(root, runId);
            Directory.CreateDirectory(dir);
            ProfileRoot = dir;
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER",
                Path.Combine(dir, "WebView2UD"));
            try
            {
                File.AppendAllText(logPath,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [bench] Isolated profile: {dir}{Environment.NewLine}");
            }
            catch { }
        }
        catch { /* isolation is best-effort; bench still validates state */ }
    }
#pragma warning disable CS0649
    private Window? _mainWindow;
#pragma warning restore CS0649
    private readonly ILogger<App>? _log;

    public App()
    {
        InitializeComponent();
        // Program.Main builds the container before XAML starts so the
        // dispatcher can be attached. Never build it twice: a second
        // container would orphan the dispatcher wiring and every service
        // resolved from it would keep the no-op dispatcher (which causes
        // RPC_E_WRONG_THREAD as soon as a background continuation touches
        // a bound collection or WinUI object).
        if (Services is null)
        {
            Services = BuildServicesStatic();
        }
        _log = Services.GetService<ILoggerFactory>()?.CreateLogger<App>();
        _log?.LogInformation("App constructor finished.");
    }

    public Window? MainWindowForTheme => _mainWindow;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Intentionally left empty. The desktop app creates its main window
        // directly from the XAML startup callback (Program.cs).
        _log?.LogInformation("OnLaunched called.");
    }

    /// <summary>
    /// Build services. This is called from Program.Main before XAML
    /// activation so the DI graph is ready when the first window is
    /// constructed.
    /// </summary>
    public static IServiceProvider BuildServicesStatic()
    {
        var services = new ServiceCollection();
        // Bench mode builds the whole graph inside the isolated profile
        // (item 20); normal browsing uses the real user profile.
        var paths = ProfileRoot is null ? BrowserPaths.Default() : new BrowserPaths(ProfileRoot);
        services.AddLogging(b =>
        {
            b.AddProvider(new LocalFileLoggerProvider(
                Path.Combine(paths.LogsDirectory, "encomm.log")));
        });

        services.AddSingleton(paths);
        services.AddSingleton<SqliteStore>(sp => new SqliteStore(paths.DatabaseFile));
        services.AddSingleton<ISecretStore, DpapiSqliteSecretStore>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<SettingsService>();

        services.AddSingleton<BrowserPersistenceService>();
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<IUiDispatcher, NoOpUiDispatcher>();
        services.AddSingleton<TabService>(sp =>
        {
            var svc = new TabService(
                sp.GetRequiredService<BrowserPersistenceService>(),
                sp.GetRequiredService<ILogger<TabService>>(),
                sp.GetRequiredService<IUiDispatcher>());
            svc.ConfigureSearchProvider(sp.GetRequiredService<SettingsService>().Current.SearchProviderUrl);
            return svc;
        });

        services.AddSingleton<WebView2EngineFactory>();
        // BrowserRuntime is created with a dispatcher placeholder; the
        // Program.Main swaps in the real UI dispatcher after XAML creates
        // the DispatcherQueueController.
        services.AddSingleton<BrowserRuntime>(sp => new BrowserRuntime(
            sp.GetRequiredService<IRequestBlocker>(),
            sp.GetRequiredService<ILogger<BrowserRuntime>>(),
            sp.GetRequiredService<TabService>(),
            sp.GetRequiredService<WebView2EngineFactory>(),
            new NoOpUiDispatcher()));
        services.AddSingleton<TabLifecycleManager>();

        services.AddSingleton<IFilterRuleProvider>(_ => new FilterRuleProvider());
        services.AddSingleton<IRequestBlocker>(sp =>
        {
            var blocker = new RequestBlocker(sp.GetRequiredService<IFilterRuleProvider>());
            blocker.Enabled = sp.GetRequiredService<SettingsService>().Current.ShieldEnabled;
            return blocker;
        });

        services.AddSingleton<AIService>();

        services.AddSingleton<MemoryProbe>(sp =>
            new MemoryProbe(() =>
            {
                var tabs = sp.GetRequiredService<TabService>().GetStateSnapshots();
                var list = new List<Encomm.Browser.Memory.TabStateSummary>(tabs.Count);
                foreach (var t in tabs) list.Add(t);
                return list;
            }));

        services.AddSingleton<IModelRouter>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>().Current;
            if (settings.AI.ProviderId == "openai-compatible"
                && !string.IsNullOrEmpty(settings.AI.BaseUrl)
                && settings.AI.HasSecret)
            {
                var secrets = sp.GetRequiredService<ISecretStore>();
                var cfg = new AIProviderConfiguration
                {
                    ProviderId = settings.AI.ProviderId,
                    BaseUrl = settings.AI.BaseUrl,
                    Model = settings.AI.Model,
                    CustomHeaders = settings.AI.CustomHeaders,
                    HasSecret = true
                };
                var provider = new OpenAICompatibleProvider(cfg, () => Task.FromResult(secrets.GetSecret("ai.primary")));
                return new DefaultModelRouter(provider);
            }
            return new DefaultModelRouter(new MockAIProvider());
        });

        services.AddSingleton<DeveloperModeService>();
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }

    private static IServiceProvider BuildServices() => BuildServicesStatic();

    /// <summary>
    /// Called by Program.Main once the XAML DispatcherQueue exists. We
    /// replace the no-op dispatcher in BrowserRuntime with a real one
    /// so renderer-thread callbacks can land on the UI thread.
    /// </summary>
    public static void InitializeUiDispatcher(DispatcherQueue queue)
    {
        MainDispatcherQueue = queue;
        Ui = new WinUiDispatcher(queue);
        var runtime = Services.GetRequiredService<BrowserRuntime>();
        // We cannot replace the runtime — its ctor captured the no-op
        // dispatcher. Reconstruct it with the real one. This is the only
        // place we manually rewire the runtime.
        // (Implementation note: BrowserRuntime is a sealed class; we
        // expose a one-time swap via reflection-free internal API.)
        runtime.AttachUiDispatcher(Ui);
        Services.GetRequiredService<TabService>().AttachUiDispatcher(Ui);
    }

    public static void SetStartupArgs(string[] args) => StartupArgs = args;
}

/// <summary>
/// Stand-in dispatcher used during DI container build (before the XAML
/// DispatcherQueue exists). All Post() invocations are no-ops. Replaced
/// by a real WinUiDispatcher once XAML is up.
/// </summary>
internal sealed class NoOpUiDispatcher : IUiDispatcher
{
    public bool HasThreadAccess => true;
    public void Post(Action action) { action?.Invoke(); }
    public async Task<T> RunAsync<T>(Func<Task<T>> func) => await func().ConfigureAwait(false);
}
