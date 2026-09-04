using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Encomm.Browser.Engine.Abstractions;
using Encomm.Browser.App.Services;
using Encomm.Browser.Core.Storage;

namespace Encomm.Browser.App.Controls;

public sealed partial class BrowserHostControl : UserControl
{
    private readonly BrowserRuntime _runtime;
    private readonly TabService _tabs;
    private Microsoft.UI.Xaml.FrameworkElement? _attachedElement;
    private Guid _attachedTabId;

    public BrowserHostControl()
    {
        InitializeComponent();
        _runtime = App.Services.GetRequiredService<BrowserRuntime>();
        _tabs = App.Services.GetRequiredService<TabService>();
        _tabs.ActiveTabChanged += OnActiveTabChanged;
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => Detach();
    }

    private void OnActiveTabChanged(object? sender, TabRecord? tab) => Refresh();

    private async void Refresh()
    {
        try
        {
            var tab = _tabs.ActiveTab;
            if (tab is null) { Detach(); return; }
            if (tab.Id == _attachedTabId && _attachedElement is not null) return;

            // Ensure a renderer exists for the active tab.
            var view = await _runtime.GetOrCreateAsync(tab);
            if (view.HostElement is Microsoft.UI.Xaml.FrameworkElement fe && fe != _attachedElement)
            {
                RootGrid.Children.Clear();
                RootGrid.Children.Add(fe);
                _attachedElement = fe;
                _attachedTabId = tab.Id;
            }
        }
        catch (Exception)
        {
            // Renderer may not be ready yet (engine still initializing).
            // Leave the host empty; the next Refresh will try again.
        }
    }

    private void Detach()
    {
        // We intentionally do NOT destroy the renderer here — only the
        // active tab's renderer is hosted in the visual tree. Other tabs
        // are kept alive in BrowserRuntime.Views until lifecycle demotes
        // them.
        RootGrid.Children.Clear();
        _attachedElement = null;
        _attachedTabId = Guid.Empty;
    }
}