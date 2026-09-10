using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Extensions.Logging;
using Encomm.Browser.Engine.Abstractions;
using Encomm.Browser.App.Services;
using Encomm.Browser.Core.Storage;

namespace Encomm.Browser.App.Controls;

public sealed partial class BrowserHostControl : UserControl
{
    private readonly BrowserRuntime _runtime;
    private readonly TabService _tabs;
    private readonly ILogger<BrowserHostControl> _log;
    private Microsoft.UI.Xaml.FrameworkElement? _attachedElement;
    private Guid _attachedTabId;

    public BrowserHostControl()
    {
        InitializeComponent();
        _runtime = App.Services.GetRequiredService<BrowserRuntime>();
        _tabs = App.Services.GetRequiredService<TabService>();
        _log = App.Services.GetRequiredService<ILogger<BrowserHostControl>>();
        _tabs.ActiveTabChanged += OnActiveTabChanged;
        Loaded += (_, _) => Refresh();
    }

    private void OnActiveTabChanged(object? sender, TabRecord? tab) => Refresh();

    private async void Refresh()
    {
        var tab = _tabs.ActiveTab;
        if (tab is null)
        {
            Detach();
            SetStatus(HostState.Empty, "No tab selected");
            return;
        }
        if (tab.Id == _attachedTabId && _attachedElement is not null)
        {
            return;
        }
        Detach();

        // Native new-tab surface: encomm:// URLs never reach WebView2
        // (it cannot resolve that scheme) and must NOT allocate a
        // renderer. Show the native panel instead.
        if (string.IsNullOrEmpty(tab.Url) || tab.Url.StartsWith("encomm://", StringComparison.OrdinalIgnoreCase))
        {
            DispatcherQueue.TryEnqueue(() => ShowNewTab());
            _attachedTabId = tab.Id;
            return;
        }

        SetStatus(HostState.Loading, "Initializing renderer for " + tab.Url);
        try
        {
            IBrowserView? view;
            if (tab.RendererState == TabRendererStateKind.Ghost)
            {
                var restored = await _runtime.RestoreGhostTabAsync(tab);
                if (!restored)
                {
                    _log.LogWarning("Ghost restore for tab {Id} did not complete", tab.Id);
                    SetStatus(HostState.Error, "Could not restore this tab. Retry by reselecting it.");
                    return;
                }
                view = await _runtime.GetOrCreateAsync(tab);
            }
            else if (tab.RendererState == TabRendererStateKind.Warm)
            {
                var resumed = await _runtime.ResumeAsync(tab.Id);
                if (resumed) _tabs.SetRendererState(tab, TabRendererStateKind.Live);
                view = await _runtime.GetOrCreateAsync(tab);
                if (view is not null) await _runtime.EnsureTabContentAsync(tab);
            }
            else
            {
                view = await _runtime.GetOrCreateAsync(tab);
                if (view is not null) await _runtime.EnsureTabContentAsync(tab);
            }
            if (view is null)
            {
                SetStatus(HostState.Error, "Renderer not ready. The engine may still be starting.");
                return;
            }
            // Visual-tree work must run on the UI thread: the awaits above
            // may have resumed on a threadpool thread.
            var capturedView = view;
            var capturedTab = tab;
            DispatcherQueue.TryEnqueue(() => Attach(capturedView, capturedTab));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to materialize renderer for tab {Id}", tab.Id);
            SetStatus(HostState.Error, "Couldn't open this page. Reselect the tab to retry.");
        }
    }

    private void Attach(IBrowserView view, TabRecord tab)
    {
        try
        {
            if (view.HostElement is not Microsoft.UI.Xaml.FrameworkElement fe)
            {
                SetStatus(HostState.Error, "Renderer reported no host element.");
                return;
            }
            Detach();
            HideNewTab();
            RootGrid.Children.Add(fe);
            _attachedElement = fe;
            _attachedTabId = tab.Id;
            SubscribeLoading(view);
            SetStatus(HostState.Live, tab.Url ?? "");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to attach renderer for tab {Id}", tab.Id);
            SetStatus(HostState.Error, "Couldn't display this page. Details are in the log.");
        }
    }

    private void SubscribeLoading(IBrowserView view)
    {
        view.LoadingStateChanged += (_, e) => DispatcherQueue.TryEnqueue(() =>
        {
            if (e.IsLoading) SetStatus(HostState.Loading, "Loading…");
            else if (_attachedElement is not null) SetStatus(HostState.Live, _tabs.ActiveTab?.Url ?? "");
        });
        view.RenderError += (_, e) => DispatcherQueue.TryEnqueue(() => SetStatus(HostState.Error, e.Message));
    }

    private void Detach()
    {
        // Remove only renderer elements; keep NewTabPanel + StatusToast.
        for (int i = RootGrid.Children.Count - 1; i >= 0; i--)
        {
            var child = RootGrid.Children[i];
            if (!ReferenceEquals(child, StatusToast) && !ReferenceEquals(child, NewTabPanel))
                RootGrid.Children.RemoveAt(i);
        }
        _attachedElement = null;
        _attachedTabId = Guid.Empty;
    }

    private void SetStatus(HostState state, string text)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SetStatus(state, text));
            return;
        }
        if (StatusOverlay is null || StatusToast is null) return;
        StatusOverlay.Text = text;
        // Show the toast while loading or on error. Hide it once a live
        // page is attached.
        StatusToast.Visibility = state == HostState.Live && _attachedElement is not null
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // -- Native New Tab surface (no renderer) --------------------------

    private void ShowNewTab()
    {
        try
        {
            Detach();
            NewTabBox.Text = "";
            RefreshWorkspaceList();
            NewTabPanel.Visibility = Visibility.Visible;
            // Hide the transient toast: an empty pill would linger
            // otherwise (no renderer is attached on purpose).
            StatusToast.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private void HideNewTab()
    {
        try { NewTabPanel.Visibility = Visibility.Collapsed; } catch { }
    }

    private void OnLogoFailed(object sender, ExceptionRoutedEventArgs e)
    {
        // Official PNG not placed yet: hide the image; the ENCOMM text
        // lockup above remains as the calm fallback.
        try { NewTabLogo.Visibility = Visibility.Collapsed; } catch { }
    }

    private void RefreshWorkspaceList()
    {
        try
        {
            var ws = App.Services.GetRequiredService<WorkspaceService>();
            WorkspaceList.ItemsSource = ws.Workspaces.ToList();
        }
        catch { }
    }

    private void OnWorkspaceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is WorkspaceRecord rec)
        {
            try
            {
                var vm = App.Services.GetRequiredService<ViewModels.MainViewModel>();
                vm.SwitchWorkspaceCommand.Execute(rec);
            }
            catch { }
        }
        try { WorkspaceList.SelectedItem = null; } catch { }
    }

    private void OnNewTabKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _ = NavigateNewTabAsync(NewTabBox.Text);
        }
    }

    private void OnNewTabSearch(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _ = NavigateNewTabAsync(NewTabBox.Text);
    }

    private async Task NavigateNewTabAsync(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;
        try
        {
            var settings = App.Services.GetRequiredService<SettingsService>();
            var resolved = OmniboxResolver.Resolve(input, settings.Current.SearchProviderUrl);
            if (string.IsNullOrEmpty(resolved)) return;
            var tab = _tabs.ActiveTab;
            if (tab is null) return;
            var updated = tab with { Url = resolved, LastInteractionUtc = DateTimeOffset.UtcNow };
            _tabs.SetActive(updated);
            var view = await _runtime.GetOrCreateAsync(updated);
            if (view is null) return;
            await view.NavigateAsync(resolved);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "New Tab navigation failed");
            SetStatus(HostState.Error, "Couldn't open that address.");
        }
    }

    private async void OnNewTabAsk(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            var dlg = new Dialogs.AICommandDialog { XamlRoot = this.Content.XamlRoot };
            App.ApplyDialogTheme(dlg);
            await dlg.ShowAsync();
        }
        catch { }
    }
}

public enum HostState
{
    Empty,
    Loading,
    Live,
    Error
}
