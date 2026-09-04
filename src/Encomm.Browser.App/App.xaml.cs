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
#pragma warning disable CS0649
    private Window? _mainWindow;
#pragma warning restore CS0649
    private readonly ILogger<App>? _log;

    public App()
    {
        InitializeComponent();
        Services = BuildServices();
        _log = Services.GetService<ILoggerFactory>()?.CreateLogger<App>();
        _log?.LogInformation("App starting.");
        // Wire ambient workspace accessor
        WorkspaceContextAccessor.Current = Services.GetRequiredService<WorkspaceService>();
        _log?.LogInformation("Workspace context wired.");
    }

    public Window? MainWindowForTheme => _mainWindow;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Intentionally left empty. The desktop app creates its main window
        // directly from the XAML startup callback (Program.cs). OnLaunched is
        // only used for OS activation paths we don't currently exercise.
        _log?.LogInformation("OnLaunched called (not used in desktop mode).");
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