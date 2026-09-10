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
using Encomm.Browser.Shield;
using Encomm.Browser.Core.Storage;
using System;
using System.IO;

namespace Encomm.Browser.App;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    private bool _syncingTabSelection;
    private Flyout? _tabSearchFlyout;

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            ViewModel = App.Services.GetRequiredService<MainViewModel>();
            Title = "Encomm AI Browser";
            SetupTitleBar();
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
            App.DevMode.ShowBadges = ViewModel.ShowDeveloperSurfaces;
            App.DevMode.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(App.DevMode.ShowBadges) && !DispatcherQueue.HasThreadAccess)
                    return;
                try
                {
                    if (DispatcherQueue.HasThreadAccess) SyncDevStrip();
                    else DispatcherQueue.TryEnqueue(SyncDevStrip);
                }
                catch { }
            };
            SyncDevStrip();
            DeveloperModeItem.IsChecked = ViewModel.Mode == "Developer";
            SyncWorkspaceButton();
            SyncTabSelection();
            ViewModel.EnsureStarted();
            SyncTabSelection();
            App.Services.GetRequiredService<TabService>().ActiveTabChanged += (_, _) => SyncTabSelection();
            App.Services.GetRequiredService<WorkspaceService>().ActiveWorkspaceChanged += (_, _) => SyncWorkspaceButton();
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
    /// Integrated title bar: content extends under the caption buttons
    /// (which keep working natively — drag, snap, min/max/close,
    /// accessibility are all real Windows behavior, not reimplemented).
    /// </summary>
    private void SetupTitleBar()
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarGrid);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
            var tb = AppWindow.TitleBar;
            if (tb is not null)
            {
                tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                tb.ButtonForegroundColor = Microsoft.UI.Colors.Gray;
            }
        }
        catch { /* title-bar customization is cosmetic */ }
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
            App.ApplyDialogTheme(dlg);
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
            App.ApplyDialogTheme(dlg);
            var result = await dlg.ShowAsync();
            return result == ContentDialogResult.Primary ? suggestedFileName : null;
        }
        catch
        {
            return null;
        }
    }

    // -- Tab strip selection (ListView selection IS the active tab) --

    private void SyncTabSelection()
    {
        try
        {
            var active = ViewModel.ActiveTab;
            if (active is null) return;
            if (TabList.SelectedItem is TabRecord sel && sel.Id == active.Id) return;
            _syncingTabSelection = true;
            try
            {
                TabList.SelectedItem = ViewModel.Tabs.FirstOrDefault(t => t.Id == active.Id);
            }
            finally { _syncingTabSelection = false; }
        }
        catch { }
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTabSelection) return;
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is TabRecord tab)
        {
            // Canonical select path (Ghost→restore, Warm→resume).
            _ = ViewModel.SelectTabCommand.ExecuteAsync(tab);
        }
    }

    /// <summary>
    /// Per-item visuals without XAML value converters (the 1.7 toolchain
    /// cannot instantiate new converter types in markup, and Window
    /// roots break converter-lookup codegen). Initial letter, pin/mute
    /// glyphs, developer state badge, and the Everyday Ghost whisper
    /// are applied here from the item record + App.DevMode.
    /// </summary>
    private void OnTabContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (e.Item is not TabRecord tab) return;
        if (e.ItemContainer is not ListViewItem container) return;
        ApplyTabVisuals(container, tab);
    }

    private static void ApplyTabVisuals(DependencyObject root, TabRecord tab)
    {
        try
        {
            var dev = App.DevMode.ShowBadges;
            SetTagText(root, "Initial", Encomm.Browser.UI.TabItemHelper.Initial(tab.Title));
            SetTagVisibility(root, "PinGlyph", tab.Pinned);
            SetTagVisibility(root, "MuteGlyph", tab.Muted);
            var badge = FindByTag(root, "StateBadge");
            var badgeText = FindByTag(root, "StateBadgeText") as TextBlock;
            var hint = FindByTag(root, "GhostHint");
            if (badge is not null)
                badge.Visibility = dev ? Visibility.Visible : Visibility.Collapsed;
            if (badgeText is not null)
                badgeText.Text = tab.RendererState.ToString().ToUpperInvariant();
            if (hint is not null)
                hint.Visibility = (!dev && tab.RendererState == TabRendererStateKind.Ghost)
                    ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
    }

    private void RefreshTabContainers()
    {
        try
        {
            foreach (var item in TabList.Items)
            {
                if (TabList.ContainerFromItem(item) is ListViewItem container
                    && item is TabRecord tab)
                    ApplyTabVisuals(container, tab);
            }
        }
        catch { }
    }

    private static FrameworkElement? FindByTag(DependencyObject root, string tag)
    {
        try
        {
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe && Equals(fe.Tag, tag)) return fe;
                var found = FindByTag(child, tag);
                if (found is not null) return found;
            }
        }
        catch { }
        return null;
    }

    private static void SetTagText(DependencyObject root, string tag, string text)
    {
        if (FindByTag(root, tag) is TextBlock tb) tb.Text = text;
    }

    private static void SetTagVisibility(DependencyObject root, string tag, bool visible)
    {
        var el = FindByTag(root, tag);
        if (el is not null) el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTabRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        FrameworkElement? anchor = sender as FrameworkElement;
        Guid id = Guid.Empty;
        if (anchor?.Tag is Guid g) id = g;
        else if (TabList.SelectedItem is Encomm.Browser.Core.Storage.TabRecord sel) id = sel.Id;
        if (id == Guid.Empty || anchor is null) return;
        var tabs = App.Services.GetRequiredService<TabService>();
        var tab = ViewModel.Tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null) return;
        var flyout = new MenuFlyout();
        AddTabMenuItem(flyout, tab.Pinned ? "Unpin" : "Pin", () => tabs.TogglePin(tab));
        AddTabMenuItem(flyout, tab.Muted ? "Unmute" : "Mute", () => tabs.ToggleMute(tab));
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddTabMenuItem(flyout, "Duplicate", () => tabs.Duplicate(tab));
        AddTabMenuItem(flyout, "Sleep", () => _ = SleepTabAsync(tab));
        AddTabMenuItem(flyout, "Ghost", () => _ = GhostTabAsync(tab));
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddTabMenuItem(flyout, "Close", () => _ = ViewModel.CloseTabCommand.ExecuteAsync(tab));
        AddTabMenuItem(flyout, "Close Others", () => _ = CloseOtherTabsAsync(tab));
        flyout.ShowAt(anchor, e.GetPosition(anchor));
    }

    private static void AddTabMenuItem(MenuFlyout flyout, string text, Action run)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => { try { run(); } catch { } };
        flyout.Items.Add(item);
    }

    private async Task SleepTabAsync(Encomm.Browser.Core.Storage.TabRecord tab)
    {
        try
        {
            var runtime = App.Services.GetRequiredService<BrowserRuntime>();
            var tabs = App.Services.GetRequiredService<TabService>();
            if (await runtime.SuspendAsync(tab.Id)) tabs.SetRendererState(tab, TabRendererStateKind.Warm);
        }
        catch { }
    }

    private async Task GhostTabAsync(Encomm.Browser.Core.Storage.TabRecord tab)
    {
        try
        {
            var runtime = App.Services.GetRequiredService<BrowserRuntime>();
            var tabs = App.Services.GetRequiredService<TabService>();
            await runtime.GhostAsync(tab.Id);
            tabs.SetRendererState(tab, TabRendererStateKind.Ghost);
        }
        catch { }
    }

    private async Task CloseOtherTabsAsync(Encomm.Browser.Core.Storage.TabRecord keep)
    {
        foreach (var t in ViewModel.Tabs.Where(t => t.Id != keep.Id).ToList())
        {
            try { await ViewModel.CloseTabCommand.ExecuteAsync(t); } catch { }
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

    private void SyncDevStrip()
    {
        try
        {
            DevStrip.Visibility = App.DevMode.ShowBadges ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
    }

    private void OnToggleDeveloperMode(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleModeCommand.Execute(null);
        App.DevMode.ShowBadges = ViewModel.ShowDeveloperSurfaces;
        RefreshTabContainers();
        if (sender is ToggleMenuFlyoutItem t) t.IsChecked = ViewModel.Mode == "Developer";
    }

    private void OnOpenDevTools(object sender, RoutedEventArgs e) => OnPreviewDevTools();

    private async void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog
        {
            XamlRoot = this.Content.XamlRoot
        };
        App.ApplyDialogTheme(dlg);
        await dlg.ShowAsync();
    }

    private async void OnOpenMemoryPanel(object sender, RoutedEventArgs e)
    {
        var dlg = new MemoryPanelDialog
        {
            XamlRoot = this.Content.XamlRoot
        };
        App.ApplyDialogTheme(dlg);
        await dlg.ShowAsync();
    }

    private async void OnOpenAiPanel(object sender, RoutedEventArgs e)
    {
        var dlg = new AICommandDialog
        {
            XamlRoot = this.Content.XamlRoot
        };
        App.ApplyDialogTheme(dlg);
        await dlg.ShowAsync();
    }

    // -- Brand slot ---------------------------------------------------

    private void OnBrandClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.NewTabCommand.Execute(null);
        AddressBox.Focus(FocusState.Programmatic);
    }

    private void OnBrandImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        // Official PNG not placed yet (see Assets/Brand/README.md):
        // collapse the image so the gradient-E fallback shows.
        try { if (sender is Image img) img.Visibility = Visibility.Collapsed; } catch { }
    }

    // -- Omnibox focus ring (neutral border, cyan accent on focus) ----

    private void OnOmniboxFocus(object sender, RoutedEventArgs e)
    {
        try { OmniboxBorder.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EncommFocusRingBrush"]; } catch { }
    }

    private void OnOmniboxBlur(object sender, RoutedEventArgs e)
    {
        try { OmniboxBorder.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EncommBorderBrush"]; } catch { }
    }

    private void OnIdentityFlyoutOpening(object sender, object e)
    {
        try
        {
            var url = ViewModel.ActiveTab?.Url ?? "";
            var secure = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            IdentityText.Text = string.IsNullOrEmpty(url)
                ? "No page loaded."
                : (secure ? "Connection is secure.\n" : "Connection is not secure.\n") + url +
                  "\n\nSite permissions are managed in Settings.";
        }
        catch { }
    }

    // -- Workspace switcher -------------------------------------------

    private void SyncWorkspaceButton()
    {
        try
        {
            var ws = App.Services.GetRequiredService<WorkspaceService>();
            var name = ws.ActiveWorkspace?.Name ?? "Personal";
            WorkspaceName.Text = name;
            WorkspaceInitials.Text = TabVisualMapper.WorkspaceInitials(name);
        }
        catch { }
    }

    private void OnWorkspaceFlyoutOpening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout) return;
        flyout.Items.Clear();
        var ws = App.Services.GetRequiredService<WorkspaceService>();
        foreach (var w in ws.Workspaces.ToList())
        {
            var item = new MenuFlyoutItem { Text = w.Name, Tag = w.Id };
            if (w.Id == ws.ActiveWorkspace?.Id) item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            item.Click += (_, _) =>
            {
                var rec = ws.Workspaces.FirstOrDefault(x => x.Id == w.Id);
                if (rec is not null) ViewModel.SwitchWorkspaceCommand.Execute(rec);
            };
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddWorkspaceMenuItem(flyout, "New workspace...", () => _ = CreateWorkspaceAsync());
        AddWorkspaceMenuItem(flyout, "Rename current...", () => _ = RenameWorkspaceAsync());
        AddWorkspaceMenuItem(flyout, "Delete current", () => _ = DeleteWorkspaceAsync());
    }

    private static void AddWorkspaceMenuItem(MenuFlyout flyout, string text, Func<Task> run)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => { _ = run(); };
        flyout.Items.Add(item);
    }

    private async Task CreateWorkspaceAsync()
    {
        var ws = App.Services.GetRequiredService<WorkspaceService>();
        var name = await PromptTextAsync("New workspace", "Name", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        var rec = ws.Create(name.Trim());
        ViewModel.SwitchWorkspaceCommand.Execute(rec);
    }

    private async Task RenameWorkspaceAsync()
    {
        var ws = App.Services.GetRequiredService<WorkspaceService>();
        var cur = ws.ActiveWorkspace;
        if (cur is null) return;
        var name = await PromptTextAsync("Rename workspace", "Name", cur.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        try { ws.Rename(cur.Id, name.Trim()); } catch { }
        SyncWorkspaceButton();
    }

    private async Task DeleteWorkspaceAsync()
    {
        var ws = App.Services.GetRequiredService<WorkspaceService>();
        var cur = ws.ActiveWorkspace;
        if (cur is null || ws.Workspaces.Count <= 1) return;
        var confirm = new ContentDialog
        {
            Title = "Delete workspace",
            Content = $"Delete '{cur.Name}'? Its tabs stay in the database but leave this workspace.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot
        };
        App.ApplyDialogTheme(confirm);
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            ws.Delete(cur.Id);
            var personal = ws.Workspaces.FirstOrDefault();
            if (personal is not null) ViewModel.SwitchWorkspaceCommand.Execute(personal);
        }
        catch { }
        SyncWorkspaceButton();
    }

    private async Task<string?> PromptTextAsync(string title, string placeholder, string initial)
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = initial };
        var dlg = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot
        };
        App.ApplyDialogTheme(dlg);
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;
        return box.Text;
    }

    // -- Shield flyout -------------------------------------------------

    private void OnShieldFlyoutOpening(object sender, object e)
    {
        try
        {
            var settings = App.Services.GetRequiredService<SettingsService>();
            var blocker = App.Services.GetRequiredService<IRequestBlocker>();
            var stats = blocker.Stats;
            ShieldToggle.IsOn = settings.Current.ShieldEnabled;
            ShieldStatusText.Text = settings.Current.ShieldEnabled
                ? "Protection is on. Trackers and ad requests are blocked before they load."
                : "Protection is off.";
            ShieldBlockedText.Text = $"{stats.BlockedRequests} requests blocked this session.";
        }
        catch { }
    }

    private void OnShieldToggled(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = App.Services.GetRequiredService<SettingsService>();
            settings.Current.ShieldEnabled = ShieldToggle.IsOn;
            settings.Save();
            var blocker = App.Services.GetRequiredService<IRequestBlocker>();
            if (blocker is RequestBlocker concrete) concrete.Enabled = ShieldToggle.IsOn;
        }
        catch { }
    }

    // -- Tab search (metadata only; never allocates renderers) --------

    private void OnTabSearchOpening(object sender, object e)
    {
        _tabSearchFlyout = sender as Flyout;
        TabSearchBox.Text = "";
        RefreshTabSearch("");
        try { TabSearchBox.Focus(FocusState.Programmatic); } catch { }
    }

    private void OnTabSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshTabSearch((sender as TextBox)?.Text ?? "");
    }

    private void RefreshTabSearch(string filter)
    {
        try
        {
            var items = ViewModel.Tabs
                .Where(t => string.IsNullOrWhiteSpace(filter)
                    || (t.Title ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || (t.Url ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Take(100)
                .ToList();
            TabSearchList.ItemsSource = items;
        }
        catch { }
    }

    private void OnTabSearchSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is Encomm.Browser.Core.Storage.TabRecord tab)
        {
            _ = ViewModel.SelectTabCommand.ExecuteAsync(tab);
            try { _tabSearchFlyout?.Hide(); } catch { }
        }
        try { TabSearchList.SelectedItem = null; } catch { }
    }
}
