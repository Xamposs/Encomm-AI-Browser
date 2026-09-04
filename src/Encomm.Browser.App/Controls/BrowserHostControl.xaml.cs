using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Encomm.Browser.Engine.Abstractions;
using Encomm.Browser.Engine.WebView2;
using Encomm.Browser.App.Services;
using Encomm.Browser.Core.Storage;

namespace Encomm.Browser.App.Controls;

public sealed partial class BrowserHostControl : UserControl
{
    private readonly BrowserEngineRegistry _registry;
    private readonly TabService _tabs;
    private IBrowserView? _attached;
    private FrameworkElement? _attachedElement;

    public BrowserHostControl()
    {
        InitializeComponent();
        _registry = App.Services.GetRequiredService<BrowserEngineRegistry>();
        _tabs = App.Services.GetRequiredService<TabService>();
        _tabs.ActiveTabChanged += OnActiveTabChanged;
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => Detach();
    }

    private void OnActiveTabChanged(object? sender, TabRecord? tab)
    {
        Refresh();
    }

    private void Refresh()
    {
        var tab = _tabs.ActiveTab;
        if (tab is null)
        {
            Detach();
            RootGrid.Children.Clear();
            return;
        }
        var view = _registry.GetOrCreate(tab);
        if (view is null) return;
        if (ReferenceEquals(view, _attached)) return;
        Detach();
        if (view is WebView2BrowserView wv)
        {
            var ctrl = wv.Initialize();
            _attachedElement = ctrl;
            RootGrid.Children.Clear();
            RootGrid.Children.Add(ctrl);
            _attached = view;
        }
    }

    private void Detach()
    {
        // We intentionally keep the underlying WebView2 control alive
        // across tab switches so back/forward state is preserved per tab.
        _attached = null;
        _attachedElement = null;
    }
}