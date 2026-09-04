using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Encomm.Browser.App.Services;
using Encomm.Browser.App.ViewModels;

namespace Encomm.Browser.App.Dialogs;

public sealed partial class AICommandDialog : ContentDialog
{
    private readonly AIService _ai;
    private readonly MainViewModel _vm;

    public AICommandDialog()
    {
        InitializeComponent();
        _ai = App.Services.GetRequiredService<AIService>();
        _vm = App.Services.GetRequiredService<MainViewModel>();
        Opened += (_, _) =>
        {
            ProviderLabel.Text = _ai.IsConfigured
                ? $"AI provider: {_ai.CurrentProviderName ?? "configured"}"
                : "AI not configured — results come from offline mock provider.";
            Commands.ItemsSource = _vm.AiCommands;
        };
    }

    private async void OnRunCommand(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string id)
        {
            ResultBox.Text = "Working...";
            var result = await _ai.RunAsync(id, _vm.ActiveTab, _vm.Tabs.ToList());
            ResultBox.Text = result;
        }
    }
}