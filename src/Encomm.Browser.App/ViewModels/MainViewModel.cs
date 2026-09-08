using System.Collections.ObjectModel;
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
    private readonly Action<Action> _onUi;

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

    public ObservableCollection<WorkspaceRecord> Workspaces => _workspaces.Workspaces;
    public ObservableCollection<TabRecord> Tabs => _tabs.Tabs;
    public ObservableCollection<AiCommandDescriptor> AiCommands { get; } = new();

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
        // Marshal all UI mutations through the WinUI dispatcher so the
        // BackgroundRuntime event callbacks (which fire on renderer threads)
        // can land safely on the UI thread.
        _onUi = a => runtime.Ui.Post(a);

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
            _ = _runtime.GhostAllAsync();
            _tabs.LoadForWorkspace(ActiveWorkspace!.Id);
            ActiveTab = _tabs.ActiveTab;
            AddressBarText = ActiveTab?.Url ?? "";
        };

        _tabs.TabOpened += (_, _) => { ActiveTab = _tabs.ActiveTab; AddressBarText = ActiveTab?.Url ?? ""; };
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

    // -- Commands ----------------------------------------------------

    [RelayCommand]
    public async Task NavigateAsync()
    {
        if (string.IsNullOrWhiteSpace(AddressBarText)) return;
        if (ActiveTab is null) return;
        var resolved = OmniboxResolver.Resolve(
            AddressBarText, _settingsService.Current.SearchProviderUrl);
        if (string.IsNullOrEmpty(resolved)) return;
        ActiveTab = ActiveTab with { Url = resolved!, LastInteractionUtc = DateTimeOffset.UtcNow };
        _tabs.SetActive(ActiveTab);
        try
        {
            var view = await _runtime.GetOrCreateAsync(ActiveTab);
            if (view is null)
            {
                _log.LogWarning("Navigate: renderer not ready for tab {Id}", ActiveTab.Id);
                return;
            }
            var result = await view.NavigateAsync(resolved!);
            if (!result.Accepted)
            {
                _log.LogWarning("Navigate rejected: {Reason}", result.Reason);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Navigate failed for tab {Id}", ActiveTab.Id);
        }
    }

    [RelayCommand]
    public Task NewTabAsync() => _tabs.OpenNewAsync(_settingsService.Current.NewTabUrl, true);

    [RelayCommand]
    public Task ReopenClosedAsync() => _tabs.ReopenRecentlyClosedAsync();

    /// <summary>
    /// Canonical close operation for ANY tab (active or background).
    /// Drops the renderer, records "recently closed", deletes the logical
    /// tab, and materializes the next active tab's renderer. This is the
    /// ONLY close path; UI must not mutate Tabs directly.
    /// </summary>
    [RelayCommand]
    public async Task CloseTabAsync(TabRecord tab)
    {
        if (tab is null) return;
        try
        {
            var view = await _runtime.GetOrCreateAsync(tab);
            if (view is not null)
            {
                try
                {
                    var ctx = await view.ExtractPageContextAsync();
                    _tabs.UpdatePageContext(tab.Id, ctx);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Close pre-save failed for tab {Id}", tab.Id);
                }
                await _runtime.GhostAsync(tab.Id);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Close renderer teardown failed for tab {Id}", tab.Id);
        }
        _tabs.Close(tab);
        var nextActive = _tabs.ActiveTab;
        if (nextActive is not null)
        {
            try { await _runtime.EnsureTabContentAsync(nextActive); } catch { }
        }
    }

    [RelayCommand]
    public async Task CloseActiveTabAsync()
    {
        if (ActiveTab is null) return;
        await CloseTabAsync(ActiveTab);
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
        var view = await _runtime.GetOrCreateAsync(ActiveTab);
        if (view is not null) await view.ReloadAsync();
    }

    [RelayCommand]
    public async Task StopAsync()
    {
        if (ActiveTab is null) return;
        if (!_runtime.HasView(ActiveTab.Id)) return;
        var view = await _runtime.GetOrCreateAsync(ActiveTab);
        if (view is not null) await view.StopAsync();
    }

    [RelayCommand]
    public async Task BackAsync()
    {
        if (ActiveTab is null) return;
        if (!_runtime.HasView(ActiveTab.Id)) return;
        var view = await _runtime.GetOrCreateAsync(ActiveTab);
        if (view is not null) await view.GoBackAsync();
    }

    [RelayCommand]
    public async Task ForwardAsync()
    {
        if (ActiveTab is null) return;
        if (!_runtime.HasView(ActiveTab.Id)) return;
        var view = await _runtime.GetOrCreateAsync(ActiveTab);
        if (view is not null) await view.GoForwardAsync();
    }

    /// <summary>
    /// Canonical tab-selection. Updates the logical active tab, then
    /// ensures a live renderer with the right lifecycle transition:
    /// Live → attach; Warm → Resume; Ghost → full restore + navigate.
    /// </summary>
    [RelayCommand]
    public async Task SelectTabAsync(TabRecord tab)
    {
        if (tab is null) return;
        _tabs.SetActive(tab);
        ActiveTab = _tabs.ActiveTab;
        try
        {
            if (tab.RendererState == TabRendererStateKind.Ghost)
            {
                await _runtime.RestoreGhostTabAsync(tab);
            }
            else if (tab.RendererState == TabRendererStateKind.Warm)
            {
                var resumed = await _runtime.ResumeAsync(tab.Id);
                if (resumed) _tabs.SetRendererState(tab, TabRendererStateKind.Live);
                await _runtime.EnsureTabContentAsync(tab);
            }
            else
            {
                await _runtime.EnsureTabContentAsync(tab);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SelectTab renderer ensure failed for {Id}", tab.Id);
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
