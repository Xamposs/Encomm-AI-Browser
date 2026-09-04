using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using Windows.System;
using Encomm.Browser.App.Services;
using Encomm.Browser.App.ViewModels;
using Encomm.Browser.App.Controls;
using Encomm.Browser.App.Dialogs;
using Encomm.Browser.Settings;
using Encomm.Browser.AI;

namespace Encomm.Browser.App;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        Title = "Encomm AI Browser";
        // Bind accelerator keys
        this.Content.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.L, Modifiers = VirtualKeyModifiers.Control });
        this.Content.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.T, Modifiers = VirtualKeyModifiers.Control });
        this.Content.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.W, Modifiers = VirtualKeyModifiers.Control });
        this.Content.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.T, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift });
        this.Content.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.R, Modifiers = VirtualKeyModifiers.Control });
        this.Content.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Left, Modifiers = VirtualKeyModifiers.Menu });
        this.Content.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Right, Modifiers = VirtualKeyModifiers.Menu });
        this.Content.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.F12 });
        this.Content.PreviewKeyDown += OnPreviewKeyDown;
        AddressBox.Focus(FocusState.Programmatic);
    }

    public void OnAddressKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ViewModel.NavigateCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        var shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        var alt = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        switch (e.Key)
        {
            case VirtualKey.L when ctrl:
                AddressBox.Focus(FocusState.Programmatic);
                AddressBox.SelectAll();
                e.Handled = true;
                break;
            case VirtualKey.T when ctrl && shift:
                ViewModel.ReopenClosedCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.T when ctrl:
                ViewModel.NewTabCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.W when ctrl:
                ViewModel.CloseActiveTabCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.R when ctrl:
                ViewModel.ReloadCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.Left when alt:
                ViewModel.BackCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.Right when alt:
                ViewModel.ForwardCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.F12:
                var tab = ViewModel.ActiveTab;
                if (tab is not null)
                {
                    var registry = App.Services.GetRequiredService<BrowserEngineRegistry>();
                    var view = registry.GetOrCreate(tab);
                    if (view is not null) _ = view.OpenDevToolsAsync();
                }
                e.Handled = true;
                break;
        }
    }

    private void OnTabCloseClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Guid id)
        {
            var tab = ViewModel.Tabs.FirstOrDefault(t => t.Id == id);
            if (tab is not null) ViewModel.Tabs.Remove(tab);
        }
    }

    private void OnMenuClicked(object sender, RoutedEventArgs e) { /* menu opens via flyout */ }

    private void OnToggleDeveloperMode(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleModeCommand.Execute(null);
        if (sender is ToggleMenuFlyoutItem t) t.IsChecked = ViewModel.Mode == "Developer";
    }

    private async void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog
        {
            XamlRoot = this.Content.XamlRoot
        };
        await dlg.ShowAsync();
    }

    private async void OnOpenMemoryPanel(object sender, RoutedEventArgs e)
    {
        var dlg = new MemoryPanelDialog
        {
            XamlRoot = this.Content.XamlRoot
        };
        await dlg.ShowAsync();
    }

    private async void OnOpenAiPanel(object sender, RoutedEventArgs e)
    {
        var dlg = new AICommandDialog
        {
            XamlRoot = this.Content.XamlRoot
        };
        await dlg.ShowAsync();
    }
}