using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        // (it cannot resolve that scheme). Show guidance instead.
        if (string.IsNullOrEmpty(tab.Url) || tab.Url.StartsWith("encomm://", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(HostState.Live, "New tab — type a URL or search above and press Enter.");
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
                SetStatus(HostState.Error, "Renderer not ready. Engine may still be initializing.");
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
            SetStatus(HostState.Error, "Renderer error: " + ex.GetType().Name);
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
            RootGrid.Children.Add(fe);
            _attachedElement = fe;
            _attachedTabId = tab.Id;
            SubscribeLoading(view);
            SetStatus(HostState.Live, tab.Url ?? "");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to attach renderer for tab {Id}", tab.Id);
            SetStatus(HostState.Error, "Renderer error: " + ex.GetType().Name);
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
        // Remove only renderer elements; keep the StatusOverlay.
        for (int i = RootGrid.Children.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(RootGrid.Children[i], StatusOverlay))
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
        if (StatusOverlay is null) return;
        StatusOverlay.Text = text;
        // Show the overlay while loading, on error, or for the native
        // new-tab guidance. Hide it once a live page is attached.
        StatusOverlay.Visibility = state == HostState.Live && _attachedElement is not null
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}

public enum HostState
{
    Empty,
    Loading,
    Live,
    Error
}
