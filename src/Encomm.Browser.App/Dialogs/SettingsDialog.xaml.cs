using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Encomm.Browser.App.Services;
using Encomm.Browser.Settings;

namespace Encomm.Browser.App.Dialogs;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly SettingsService _settings;
    private readonly ISecretStore _secrets;
    private const string SecretName = "ai.primary";

    public SettingsDialog()
    {
        InitializeComponent();
        _settings = App.Services.GetRequiredService<SettingsService>();
        _secrets = App.Services.GetRequiredService<ISecretStore>();
        Loaded += (_, _) => Bind();
    }

    private void Bind()
    {
        var c = _settings.Current;
        ModeBox.SelectedItem = c.Mode;
        SearchUrlBox.Text = c.SearchProviderUrl;
        ThemeBox.SelectedItem = c.Theme;
        MemorySaver.IsOn = c.MemorySaverEnabled;
        PresetBox.SelectedItem = c.MemoryPreset;
        Shield.IsOn = c.ShieldEnabled;
        RestoreSession.IsOn = c.RestoreLastSession;
        ProviderBox.SelectedItem = c.AI.ProviderId;
        BaseUrlBox.Text = c.AI.BaseUrl ?? "";
        ModelBox.Text = c.AI.Model ?? "";
        if (c.AI.HasSecret) SecretBox.PlaceholderText = "API key configured (•••••) — type to replace";
    }

    private void OnCategoryChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            var target = FindName(tag) as FrameworkElement;
            target?.StartBringIntoView();
        }
    }

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var c = _settings.Current;
        c.Mode = ModeBox.SelectedItem as string ?? "Everyday";
        c.SearchProviderUrl = string.IsNullOrWhiteSpace(SearchUrlBox.Text) ? c.SearchProviderUrl : SearchUrlBox.Text.Trim();
        c.Theme = ThemeBox.SelectedItem as string ?? "System";
        c.MemorySaverEnabled = MemorySaver.IsOn;
        c.MemoryPreset = PresetBox.SelectedItem as string ?? "Balanced";
        c.ShieldEnabled = Shield.IsOn;
        c.RestoreLastSession = RestoreSession.IsOn;
        c.AI.ProviderId = ProviderBox.SelectedItem as string ?? "mock";
        c.AI.BaseUrl = string.IsNullOrWhiteSpace(BaseUrlBox.Text) ? null : BaseUrlBox.Text.Trim();
        c.AI.Model = string.IsNullOrWhiteSpace(ModelBox.Text) ? null : ModelBox.Text.Trim();
        if (!string.IsNullOrEmpty(SecretBox.Password))
        {
            _secrets.SetSecret(SecretName, SecretBox.Password);
            c.AI.HasSecret = true;
        }
        _settings.Save();
        ApplyTheme(c.Theme);
    }

    private void ApplyTheme(string theme)
    {
        // Single theme path (wires the persisted setting to XAML).
        App.ApplyTheme();
    }

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(SecretBox.Password)) { Status.Text = "Type a key first."; return; }
        _secrets.SetSecret(SecretName, SecretBox.Password);
        _settings.Current.AI.HasSecret = true;
        _settings.Save();
        SecretBox.Password = "";
        SecretBox.PlaceholderText = "API key configured (•••••) — type to replace";
        Status.Text = "Saved (encrypted via DPAPI).";
    }

    private void OnClearKey(object sender, RoutedEventArgs e)
    {
        _secrets.DeleteSecret(SecretName);
        _settings.Current.AI.HasSecret = false;
        _settings.Save();
        SecretBox.PlaceholderText = "API key (stored encrypted)";
        Status.Text = "Cleared.";
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        Status.Text = "Testing...";
        var router = App.Services.GetRequiredService<Encomm.Browser.AI.IModelRouter>();
        var ok = await router.TestConnectionAsync();
        Status.Text = ok ? "Connection OK." : "Connection failed.";
        TestResult.Text = ok ? "OK" : "FAIL";
    }
}