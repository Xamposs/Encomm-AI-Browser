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
using System;
using System.IO;

namespace Encomm.Browser.App;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        try
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
        catch (Exception ex)
        {
            try
            {
                var path = Path.Combine(Encomm.Browser.Core.BrowserPaths.Default().LogsDirectory, "encomm.mainwindow-fatal.txt");
                File.WriteAllText(path, ex.ToString());
            }
            catch { }
            throw;
        }
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
                if (ViewModel.ActiveTab is not null)
                {
                    var runtime = App.Services.GetRequiredService<BrowserRuntime>();
                    if (runtime.HasView(ViewModel.ActiveTab.Id))
                    {
                        _ = runtime.GetOrCreateAsync(ViewModel.ActiveTab).ContinueWith(t =>
                        {
                            if (t.Result is { } v) _ = v.OpenDevToolsAsync();
                        });
                    }
                }
                e.Handled = true;
                break;
        }
    }

    private async void OnTabCloseClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Guid id)
        {
            var tab = ViewModel.Tabs.FirstOrDefault(t => t.Id == id);
            if (tab is null) return;
            // Destroy the renderer FIRST, then close the logical tab.
            var runtime = App.Services.GetRequiredService<BrowserRuntime>();
            await runtime.GhostAsync(tab.Id);
            ViewModel.Tabs.Remove(tab); // removes from ObservableCollection
            // TabService.Close also fires; it is idempotent for already-removed tabs.
            // The ViewModel above is the source of truth for the UI list.
            App.Services.GetRequiredService<TabService>().Close(tab);
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