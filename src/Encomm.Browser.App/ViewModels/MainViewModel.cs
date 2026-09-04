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
    private readonly BrowserEngineRegistry _engineRegistry;
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
        BrowserEngineRegistry engineRegistry,
        AIService ai,
        SettingsService settingsService,
        ILogger<MainViewModel> log)
    {
        _workspaces = workspaces;
        _tabs = tabs;
        _engineRegistry = engineRegistry;
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
            _tabs.LoadForWorkspace(ActiveWorkspace!.Id);
            ActiveTab = _tabs.ActiveTab;
            AddressBarText = ActiveTab?.Url ?? "";
        };

        _tabs.TabOpened += (_, t) => { ActiveTab = _tabs.ActiveTab; AddressBarText = ActiveTab?.Url ?? ""; };
        _tabs.TabClosed += (_, t) => { ActiveTab = _tabs.ActiveTab; AddressBarText = ActiveTab?.Url ?? ""; };
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
        var view = await GetOrCreateViewForActiveAsync();
        if (view is null) return;
        var resolved = await view.ResolveUrlAsync(AddressBarText);
        if (string.IsNullOrEmpty(resolved)) return;
        await view.NavigateAsync(resolved!);
        // Also update the active tab record to remember the URL.
        if (ActiveTab is not null)
        {
            var updated = ActiveTab with { Url = resolved!, LastInteractionUtc = DateTimeOffset.UtcNow };
            ReplaceActiveTabRecord(updated);
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
    public void CloseActiveTab()
    {
        if (ActiveTab is null) return;
        _tabs.Close(ActiveTab);
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
        var view = await GetOrCreateViewForActiveAsync();
        if (view is not null) await view.ReloadAsync();
    }

    [RelayCommand]
    public async Task StopAsync()
    {
        var view = await GetOrCreateViewForActiveAsync();
        if (view is not null) await view.StopAsync();
    }

    [RelayCommand]
    public async Task BackAsync()
    {
        var view = await GetOrCreateViewForActiveAsync();
        if (view is not null) await view.GoBackAsync();
    }

    [RelayCommand]
    public async Task ForwardAsync()
    {
        var view = await GetOrCreateViewForActiveAsync();
        if (view is not null) await view.GoForwardAsync();
    }

    [RelayCommand]
    public void SwitchWorkspace(WorkspaceRecord workspace)
    {
        if (workspace is null) return;
        _workspaces.SwitchTo(workspace.Id);
        SelectedWorkspaceName = workspace.Name;
    }

    [RelayCommand]
    public void SelectTab(TabRecord tab)
    {
        if (tab is null) return;
        _tabs.SetActive(tab);
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

    private async Task<IBrowserView?> GetOrCreateViewForActiveAsync()
    {
        if (ActiveTab is null) return null;
        var view = _engineRegistry.GetOrCreate(ActiveTab);
        if (view is null) return null;
        if (((Encomm.Browser.Engine.WebView2.WebView2BrowserView)view).State == ViewLifecycleState.Ghost)
            await view.WakeAsync();
        return view;
    }

    private void ReplaceActiveTabRecord(TabRecord updated)
    {
        if (ActiveTab is null) return;
        var idx = _tabs.Tabs.IndexOf(ActiveTab);
        if (idx < 0) return;
        _tabs.Tabs[idx] = updated;
        ActiveTab = updated;
    }
}

public sealed record AiCommandDescriptor(string Label, string Id);