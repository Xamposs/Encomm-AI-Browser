using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Encomm.Browser.Engine.Abstractions;
using Encomm.Browser.AI;
using Encomm.Browser.App.Services;
using Encomm.Browser.Core.Storage;

namespace Encomm.Browser.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly WorkspaceService _workspaces;
    private readonly TabService _tabs;
    private readonly BrowserRuntime _runtime;
    private readonly AIService _ai;
    private readonly SettingsService _settingsService;
    private readonly ILogger<MainViewModel> _log;

    [ObservableProperty] private WorkspaceRecord? _activeWorkspace;
    [ObservableProperty] private TabRecord? _activeTab;
    [ObservableProperty] private string _addressBarText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _title = "Encomm AI Browser";
    [ObservableProperty] private string _mode = "Everyday";
    [ObservableProperty] private bool _showDeveloperSurfaces;
    [ObservableProperty] private string _aiCommandText = "";
    [ObservableProperty] private string _aiCommandResult = "";
    [ObservableProperty] private string _selectedWorkspaceName = "Personal";

    public System.Collections.ObjectModel.ObservableCollection<WorkspaceRecord> Workspaces => _workspaces.Workspaces;
    public System.Collections.ObjectModel.ObservableCollection<TabRecord> Tabs => _tabs.Tabs;
    public System.Collections.ObjectModel.ObservableCollection<AiCommandDescriptor> AiCommands { get; } = new();

    public MainViewModel(
        WorkspaceService workspaces,
        TabService tabs,
        BrowserRuntime runtime,
        AIService ai,
        SettingsService settingsService,
        ILogger<MainViewModel> log)
    {
        _workspaces = workspaces;
        _tabs = tabs;
        _runtime = runtime;
        _ai = ai;
        _settingsService = settingsService;
        _log = log;
        ActiveWorkspace = workspaces.ActiveWorkspace;
        SelectedWorkspaceName = ActiveWorkspace?.Name ?? "Personal";
        ActiveTab = tabs.ActiveTab;
        Mode = _settingsService.Current.Mode;
        ShowDeveloperSurfaces = Mode == "Developer";
        AddressBarText = ActiveTab?.Url ?? "";

        _workspaces.ActiveWorkspaceChanged += (_, _) =>
        {
            ActiveWorkspace = workspaces.ActiveWorkspace;
            SelectedWorkspaceName = ActiveWorkspace?.Name ?? "Personal";
            // Ghost every renderer in the previous workspace.
            _ = _runtime.GhostAllAsync();
            _tabs.LoadForWorkspace(ActiveWorkspace!.Id);
            ActiveTab = _tabs.ActiveTab;
            AddressBarText = ActiveTab?.Url ?? "";
        };

        _tabs.TabOpened += (_, t) => { ActiveTab = _tabs.ActiveTab; AddressBarText = ActiveTab?.Url ?? ""; };
        _tabs.TabClosed += (_, t) =>
        {
            // Renderer is already dropped before TabService.Close was called
            // (by the UI close-button handler). As a safety net, drop it here
            // too in case some other path triggered TabClosed.
            _ = _runtime.GhostAsync(t.Id);
            ActiveTab = _tabs.ActiveTab;
            AddressBarText = ActiveTab?.Url ?? "";
        };
        _tabs.ActiveTabChanged += (_, t) =>
        {
            ActiveTab = t;
            AddressBarText = t?.Url ?? "";
            Title = string.IsNullOrEmpty(t?.Title) ? "Encomm AI Browser" : t!.Title;
        };

        AiCommands.Add(new AiCommandDescriptor("Summarize this page", "summarize-page"));
        AiCommands.Add(new AiCommandDescriptor("Explain selection", "explain-selection"));
        AiCommands.Add(new AiCommandDescriptor("Ask about this page", "ask-page"));
        AiCommands.Add(new AiCommandDescriptor("Compare tabs in this workspace", "compare-workspace"));
        AiCommands.Add(new AiCommandDescriptor("Organize workspace", "organize-workspace"));
        AiCommands.Add(new AiCommandDescriptor("Extract key information", "extract-info"));
    }

    [RelayCommand]
    public async Task NavigateAsync()
    {
        if (string.IsNullOrWhiteSpace(AddressBarText)) return;
        if (ActiveTab is null) return;
        var resolved = Encomm.Browser.Engine.Abstractions.OmniboxResolver.Resolve(
            AddressBarText, _settingsService.Current.SearchProviderUrl);
        if (string.IsNullOrEmpty(resolved)) return;
        ActiveTab = ActiveTab with { Url = resolved!, LastInteractionUtc = DateTimeOffset.UtcNow };
        _tabs.SetActive(ActiveTab);
        try
        {
            var view = await _runtime.GetOrCreateAsync(ActiveTab);
            await view.NavigateAsync(resolved!);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Navigate failed for tab {Id}", ActiveTab.Id);
        }
    }

    [RelayCommand]
    public async Task NewTabAsync()
    {
        await _tabs.OpenNewAsync(_settingsService.Current.NewTabUrl, true);
    }

    [RelayCommand]
    public async Task ReopenClosedAsync()
    {
        await _tabs.ReopenRecentlyClosedAsync();
    }

    [RelayCommand]
    public async Task CloseActiveTabAsync()
    {
        if (ActiveTab is null) return;
        var tab = ActiveTab;
        // Drop the renderer FIRST (the real ghost), then remove the logical tab.
        await _runtime.GhostAsync(tab.Id);
        _tabs.Close(tab);
    }

    [RelayCommand]
    public void DuplicateActiveTab()
    {
        if (ActiveTab is null) return;
        _tabs.Duplicate(ActiveTab);
    }

    [RelayCommand]
    public void PinActiveTab()
    {
        if (ActiveTab is null) return;
        _tabs.TogglePin(ActiveTab);
        ActiveTab = _tabs.ActiveTab;
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        if (ActiveTab is null) return;
        if (!_runtime.HasView(ActiveTab.Id))
        {
            // Renderer doesn't exist (Ghost). Bring it back, then reload.
            var view = await _runtime.WakeAsync(ActiveTab);
            if (view is not null) await view.ReloadAsync();
        }
        else
        {
            var view = await _runtime.GetOrCreateAsync(ActiveTab);
            await view.ReloadAsync();
        }
    }

    [RelayCommand]
    public async Task StopAsync()
    {
        if (ActiveTab is null) return;
        if (!_runtime.HasView(ActiveTab.Id)) return;
        var view = await _runtime.GetOrCreateAsync(ActiveTab);
        await view.StopAsync();
    }

    [RelayCommand]
    public async Task BackAsync()
    {
        if (ActiveTab is null) return;
        if (!_runtime.HasView(ActiveTab.Id)) return;
        var view = await _runtime.GetOrCreateAsync(ActiveTab);
        await view.GoBackAsync();
    }

    [RelayCommand]
    public async Task ForwardAsync()
    {
        if (ActiveTab is null) return;
        if (!_runtime.HasView(ActiveTab.Id)) return;
        var view = await _runtime.GetOrCreateAsync(ActiveTab);
        await view.GoForwardAsync();
    }

    [RelayCommand]
    public async Task SelectTabAsync(TabRecord tab)
    {
        if (tab is null) return;
        _tabs.SetActive(tab);
        // Materialize a renderer for the newly-active tab (lazy).
        try
        {
            await _runtime.GetOrCreateAsync(tab);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Materialize renderer for tab {Id} failed", tab.Id);
        }
    }

    [RelayCommand]
    public void SwitchWorkspace(WorkspaceRecord workspace)
    {
        if (workspace is null) return;
        _workspaces.SwitchTo(workspace.Id);
        SelectedWorkspaceName = workspace.Name;
    }

    [RelayCommand]
    public async Task RunAiCommandAsync(AiCommandDescriptor descriptor)
    {
        if (descriptor is null) return;
        AiCommandResult = "Working...";
        var result = await _ai.RunAsync(descriptor.Id, ActiveTab, _tabs.Tabs.ToList());
        AiCommandResult = result;
    }

    [RelayCommand]
    public void ToggleMode()
    {
        Mode = Mode == "Everyday" ? "Developer" : "Everyday";
        _settingsService.Current.Mode = Mode;
        _settingsService.Save();
        ShowDeveloperSurfaces = Mode == "Developer";
    }

    [RelayCommand]
    public async Task GhostActiveTabAsync()
    {
        if (ActiveTab is null) return;
        await _runtime.GhostAsync(ActiveTab.Id);
        _tabs.SetRendererState(ActiveTab, TabRendererStateKind.Ghost);
    }

    [RelayCommand]
    public async Task SleepActiveTabAsync()
    {
        if (ActiveTab is null) return;
        var ok = await _runtime.SuspendAsync(ActiveTab.Id);
        if (ok) _tabs.SetRendererState(ActiveTab, TabRendererStateKind.Warm);
    }

    [RelayCommand]
    public void ToggleKeepAwakeActiveTab()
    {
        if (ActiveTab is null) return;
        _tabs.ToggleKeepAwake(ActiveTab);
        ActiveTab = _tabs.ActiveTab;
    }
}

public sealed record AiCommandDescriptor(string Label, string Id);