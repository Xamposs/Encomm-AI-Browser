using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Encomm.Browser.App.Services;
using Encomm.Browser.App.ViewModels;
using Encomm.Browser.Core;
using Encomm.Browser.Core.Storage;
using Encomm.Browser.Settings;
using Encomm.Browser.Shield;
using Encomm.Browser.Memory;
using Encomm.Browser.AI;
using Encomm.Browser.Security;
using Encomm.Browser.Developer;

namespace Encomm.Browser.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private Window? _mainWindow;

    public App()
    {
        InitializeComponent();
        Services = BuildServices();
        // Wire ambient workspace accessor
        WorkspaceContextAccessor.Current = Services.GetRequiredService<WorkspaceService>();
    }

    public Window? MainWindowForTheme => _mainWindow;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        _mainWindow.Activate();
        // Auto-start lifecycle ticking
        var lifecycle = Services.GetRequiredService<TabLifecycleManager>();
        var timer = new System.Timers.Timer(60_000) { AutoReset = true };
        timer.Elapsed += (_, _) => { try { lifecycle.Tick(); } catch { } };
        timer.Start();
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.AddProvider(new LocalFileLoggerProvider(
                Path.Combine(BrowserPaths.Default().LogsDirectory, "encomm.log")));
        });

        services.AddSingleton(BrowserPaths.Default());
        services.AddSingleton<SqliteStore>(sp => new SqliteStore(BrowserPaths.Default().DatabaseFile));
        services.AddSingleton<ISecretStore, DpapiSqliteSecretStore>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<SettingsService>();

        services.AddSingleton<BrowserPersistenceService>();
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<TabService>();
        services.AddSingleton<TabLifecycleManager>();
        services.AddSingleton<IFilterRuleProvider>(_ => new FilterRuleProvider());
        services.AddSingleton<IRequestBlocker>(sp =>
        {
            var blocker = new RequestBlocker(sp.GetRequiredService<IFilterRuleProvider>());
            blocker.Enabled = sp.GetRequiredService<SettingsService>().Current.ShieldEnabled;
            return blocker;
        });
        services.AddSingleton<BrowserEngineRegistry>();
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
}