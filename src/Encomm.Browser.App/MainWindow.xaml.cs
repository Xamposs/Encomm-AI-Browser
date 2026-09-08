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
using Encomm.Browser.Core.Storage;
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
            WireRuntimePrompts();
            // Bind accelerator keys. Each accelerator invokes its command
            // directly (relying on PreviewKeyDown alone is unreliable:
            // focus navigation and the accelerator system can swallow
            // keys such as Tab before preview handlers run).
            AddAccelerator(Windows.System.VirtualKey.L, VirtualKeyModifiers.Control, () => { AddressBox.Focus(FocusState.Programmatic); AddressBox.SelectAll(); });
            AddAccelerator(Windows.System.VirtualKey.T, VirtualKeyModifiers.Control, () => ViewModel.NewTabCommand.Execute(null));
            AddAccelerator(Windows.System.VirtualKey.W, VirtualKeyModifiers.Control, () => ViewModel.CloseActiveTabCommand.Execute(null));
            AddAccelerator(Windows.System.VirtualKey.T, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => ViewModel.ReopenClosedCommand.Execute(null));
            AddAccelerator(Windows.System.VirtualKey.R, VirtualKeyModifiers.Control, () => ViewModel.ReloadCommand.Execute(null));
            AddAccelerator(Windows.System.VirtualKey.Tab, VirtualKeyModifiers.Control, () => ViewModel.SelectNextTabCommand.Execute(null));
            AddAccelerator(Windows.System.VirtualKey.Tab, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => ViewModel.SelectPreviousTabCommand.Execute(null));
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

    /// <summary>
    /// Wire the runtime's permission / download / new-window prompts to
    /// this window (which owns a XamlRoot for ContentDialogs).
    /// </summary>
    private void WireRuntimePrompts()
    {
        var runtime = App.Services.GetRequiredService<BrowserRuntime>();
        runtime.PermissionPromptAsync = ShowPermissionDialogAsync;
        runtime.DownloadPromptAsync = ShowDownloadDialogAsync;
        runtime.NewWindowHandlerAsync = async (url, userInitiated) =>
        {
            // Open user-initiated popups as background tabs; decline the rest.
            if (!userInitiated) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out _)) return;
            var tabs = App.Services.GetRequiredService<TabService>();
            await tabs.OpenNewAsync(url, switchTo: false);
        };
        // Keyboard shortcuts while focus is inside page content. WebView2
        // child HWNDs bypass the XAML accelerator table, so the engine
        // forwards owned combos here via a capture-phase script bridge.
        // Enqueue onto the UI thread (safe from any thread) and claim
        // every combo we own.
        runtime.AcceleratorHandler = HandleWebAccelerator;
    }

    /// <summary>
    /// Map a forwarded content-focus accelerator to a browser command.
    /// Returns true when the combo is ours (suppresses WebView2 defaults).
    /// </summary>
    private bool HandleWebAccelerator(Encomm.Browser.Engine.Abstractions.AcceleratorKeyEventArgs e)
    {
        if (!e.KeyDown) return false;
        Action? run = (e.VirtualKey, e.Ctrl, e.Shift, e.Alt) switch
        {
            (0x4C, true, false, false) => () => { AddressBox.Focus(FocusState.Programmatic); AddressBox.SelectAll(); },
            (0x54, true, false, false) => () => ViewModel.NewTabCommand.Execute(null),
            (0x57, true, false, false) => () => ViewModel.CloseActiveTabCommand.Execute(null),
            (0x54, true, true, false) => () => ViewModel.ReopenClosedCommand.Execute(null),
            (0x52, true, false, false) => () => ViewModel.ReloadCommand.Execute(null),
            (0x09, true, false, false) => () => ViewModel.SelectNextTabCommand.Execute(null),
            (0x09, true, true, false) => () => ViewModel.SelectPreviousTabCommand.Execute(null),
            (0x25, false, false, true) => () => ViewModel.BackCommand.Execute(null),
            (0x27, false, false, true) => () => ViewModel.ForwardCommand.Execute(null),
            (0x7B, false, false, false) => () => OnPreviewDevTools(),
            _ => null,
        };
        if (run is null) return false;
        DispatcherQueue.TryEnqueue(() => { try { run(); } catch { } });
        return true;
    }

    private void OnPreviewDevTools()
    {
        if (ViewModel.ActiveTab is null) return;
        var runtime = App.Services.GetRequiredService<BrowserRuntime>();
        _ = runtime.GetOrCreateAsync(ViewModel.ActiveTab).ContinueWith(t =>
        {
            if (t.Result is { } v) _ = v.OpenDevToolsAsync();
        });
    }

    private async Task<bool> ShowPermissionDialogAsync(Encomm.Browser.Engine.Abstractions.PermissionKind kind, string origin)
    {
        try
        {
            var dlg = new ContentDialog
            {
                Title = "Permission request",
                Content = $"Allow {kind} for {origin}?\n\nSensitive permissions default to Block.",
                PrimaryButtonText = "Allow once",
                CloseButtonText = "Block",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot
            };
            var result = await dlg.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> ShowDownloadDialogAsync(string suggestedFileName, string url)
    {
        try
        {
            var dlg = new ContentDialog
            {
                Title = "Download",
                Content = $"Save {System.IO.Path.GetFileName(suggestedFileName)}?\n\nFrom: {url}\nTo: {suggestedFileName}",
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };
            var result = await dlg.ShowAsync();
            return result == ContentDialogResult.Primary ? suggestedFileName : null;
        }
        catch
        {
            return null;
        }
    }

    private void OnWorkspaceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is WorkspaceRecord ws)
        {
            ViewModel.SwitchWorkspace(ws);
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

    private void AddAccelerator(Windows.System.VirtualKey key, VirtualKeyModifiers modifiers, Action invoke)
    {
        var acc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = key, Modifiers = modifiers };
        acc.Invoked += (_, _) => invoke();
        this.Content.KeyboardAccelerators.Add(acc);
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
            case VirtualKey.Tab when ctrl && shift:
                ViewModel.SelectPreviousTabCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.Tab when ctrl:
                ViewModel.SelectNextTabCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.F12:
                OnPreviewDevTools();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Single canonical close path. Delegates to the ViewModel command so
    /// keyboard, menu, X-button, and lifecycle paths share one
    /// implementation. The X button stops here and never touches
    /// the tab collection directly.
    /// </summary>
    private void OnTabCloseClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Guid id) return;
        var tab = ViewModel.Tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null) return;
        TrySetHandled(e);
        _ = ViewModel.CloseTabCommand.ExecuteAsync(tab);
    }

    private static void TrySetHandled(Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // Use reflection to set Handled without compile-time dependency
        // on whether the WinUI projection's RoutedEventArgs exposes it.
        try
        {
            var prop = e.GetType().GetProperty("Handled");
            if (prop?.CanWrite == true) prop.SetValue(e, true);
        }
        catch { /* swallow */ }
    }

    /// <summary>
    /// Tab body click selects the tab. The X button is a separate child
    /// element that handles its own click.
    /// </summary>
    private void OnTabBodyTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Guid id)
        {
            var tab = ViewModel.Tabs.FirstOrDefault(t => t.Id == id);
            if (tab is not null)
            {
                _ = ViewModel.SelectTabCommand.ExecuteAsync(tab);
            }
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
