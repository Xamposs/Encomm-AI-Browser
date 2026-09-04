using Encomm.Browser.Settings;

namespace Encomm.Browser.App.Services;

public sealed class SettingsService
{
    private readonly SettingsStore _store;
    public BrowserSettings Current { get; private set; }

    public SettingsService(SettingsStore store)
    {
        _store = store;
        Current = _store.Load();
    }

    public void Save()
    {
        _store.Save(Current);
        Encomm.Browser.Engine.WebView2.SearchProviderSettings.CurrentUrl = Current.SearchProviderUrl;
    }

    public void SetAI(AIProviderSettings settings)
    {
        Current.AI = settings;
        Save();
    }
}