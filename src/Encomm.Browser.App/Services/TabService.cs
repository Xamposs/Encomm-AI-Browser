using System.Collections.ObjectModel;
using Encomm.Browser.Engine.Abstractions;
using Encomm.Browser.Tabs;
using Encomm.Browser.Memory;
using Encomm.Browser.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Encomm.Browser.App.Services;

/// <summary>
/// Central manager for LOGICAL tabs. Owns the in-memory map of tabs and
/// persists them. Does NOT own renderer state — that is
/// `BrowserRuntime`'s job. `TabService` only manipulates the `TabRecord`
/// domain shape; it never instantiates or touches `IBrowserView`.
///
/// Omnibox resolution uses the engine-independent `OmniboxResolver` so
/// creating a new tab allocates zero renderer resources.
/// </summary>
public sealed class TabService
{
    private readonly BrowserPersistenceService _persistence;
    private readonly ILogger<TabService> _log;
    private readonly Dictionary<Guid, TabRecord> _byId = new();
    private readonly LinkedList<RecentlyClosedRecord> _recentlyClosed = new();
    private const int RecentlyClosedLimit = 25;
    private string _searchProviderUrl = "https://duckduckgo.com/?q={q}";

    public ObservableCollection<TabRecord> Tabs { get; } = new();
    public TabRecord? ActiveTab { get; private set; }

    public event EventHandler<TabRecord>? TabOpened;
    public event EventHandler<TabRecord>? TabClosed;
    public event EventHandler<TabRecord?>? ActiveTabChanged;

    public TabService(BrowserPersistenceService persistence, ILogger<TabService> log)
    {
        _persistence = persistence;
        _log = log;
    }

    public void ConfigureSearchProvider(string url) => _searchProviderUrl = url;

    public void LoadForWorkspace(Guid workspaceId)
    {
        Tabs.Clear();
        _byId.Clear();
        foreach (var t in _persistence.LoadTabs(workspaceId))
        {
            Tabs.Add(t);
            _byId[t.Id] = t;
        }
        var lastActive = _persistence.GetAppState($"active_tab:{workspaceId}");
        if (Guid.TryParse(lastActive, out var id) && _byId.TryGetValue(id, out var tab))
            ActiveTab = tab;
        else if (Tabs.Count > 0)
            ActiveTab = Tabs[0];
        ActiveTabChanged?.Invoke(this, ActiveTab);
    }

    public Task<TabRecord> OpenNewAsync(string url, bool switchTo = true, CancellationToken ct = default)
    {
        // Resolve via the engine-independent OmniboxResolver. We do NOT
        // create any view here — the tab is a metadata-only logical record
        // until something actually wants to render it.
        var resolved = OmniboxResolver.Resolve(url, _searchProviderUrl);
        var final = string.IsNullOrEmpty(resolved) ? url : resolved;

        var tab = new TabRecord(
            Guid.NewGuid(), WorkspaceIdOfActive(), final, "", null,
            TabRendererStateKind.Ghost, TabLogicalStateKind.Background,
            false, false, false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Tabs.Count, null);
        Tabs.Add(tab);
        _byId[tab.Id] = tab;
        _persistence.SaveTab(tab);
        if (switchTo) SetActive(tab);
        TabOpened?.Invoke(this, tab);
        _log.LogInformation("Tab opened: {Id} url={Url}", tab.Id, tab.Url);
        return Task.FromResult(tab);
    }

    public Guid WorkspaceIdOfActive()
    {
        var svc = WorkspaceContextAccessor.Current;
        return svc?.ActiveWorkspace.Id ?? Encomm.Browser.Workspaces.BuiltInWorkspaces.PersonalId;
    }

    public void SetActive(TabRecord tab)
    {
        if (tab is null) return;
        if (ActiveTab is not null && ActiveTab.Id != tab.Id)
        {
            var prev = ActiveTab with { LogicalState = TabLogicalStateKind.Background };
            ReplaceTab(prev);
        }
        var updated = tab with
        {
            LogicalState = TabLogicalStateKind.Active,
            LastInteractionUtc = DateTimeOffset.UtcNow,
            RendererState = TabRendererStateKind.Live
        };
        ReplaceTab(updated);
        ActiveTab = updated;
        _persistence.SaveTab(updated);
        _persistence.SetAppState($"active_tab:{updated.WorkspaceId}", updated.Id.ToString());
        ActiveTabChanged?.Invoke(this, updated);
    }

    /// <summary>
    /// Close a logical tab. The renderer (if any) must be destroyed by the
    /// caller via `BrowserRuntime.GhostAsync` BEFORE calling this method;
    /// `TabService` does not touch renderers.
    /// </summary>
    public void Close(TabRecord tab)
    {
        if (tab is null) return;
        var rec = new RecentlyClosedRecord(tab.Id, tab.Url, tab.Title, tab.WorkspaceId, DateTimeOffset.UtcNow);
        _persistence.SaveRecentlyClosed(rec);
        _recentlyClosed.AddFirst(rec);
        while (_recentlyClosed.Count > RecentlyClosedLimit)
        {
            var last = _recentlyClosed.Last!;
            _persistence.DeleteRecentlyClosed(last.Value.OriginalTabId);
            _recentlyClosed.RemoveLast();
        }
        Tabs.Remove(tab);
        _byId.Remove(tab.Id);
        _persistence.DeleteTab(tab.Id);
        if (ActiveTab?.Id == tab.Id)
        {
            ActiveTab = Tabs.Count > 0 ? Tabs[^1] : null;
            _persistence.SetAppState($"active_tab:{tab.WorkspaceId}", ActiveTab?.Id.ToString() ?? Guid.Empty.ToString());
            ActiveTabChanged?.Invoke(this, ActiveTab);
        }
        TabClosed?.Invoke(this, tab);
    }

    public void MoveToWorkspace(TabRecord tab, Guid newWorkspaceId)
    {
        var updated = tab with { WorkspaceId = newWorkspaceId };
        ReplaceTab(updated);
        _persistence.SaveTab(updated);
    }

    public TabRecord Duplicate(TabRecord tab)
    {
        var dup = tab with
        {
            Id = Guid.NewGuid(),
            RendererState = TabRendererStateKind.Ghost,
            OrderIndex = Tabs.Count,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastInteractionUtc = DateTimeOffset.UtcNow
        };
        Tabs.Add(dup);
        _byId[dup.Id] = dup;
        _persistence.SaveTab(dup);
        TabOpened?.Invoke(this, dup);
        return dup;
    }

    public void TogglePin(TabRecord tab) => UpdateFlag(tab, t => t with { Pinned = !t.Pinned });
    public void ToggleMute(TabRecord tab) => UpdateFlag(tab, t => t with { Muted = !t.Muted });
    public void ToggleKeepAwake(TabRecord tab) => UpdateFlag(tab, t => t with { KeepAwake = !t.KeepAwake });

    public void SetRendererState(TabRecord tab, TabRendererStateKind state)
    {
        UpdateFlag(tab, t => t with { RendererState = state });
    }

    private void UpdateFlag(TabRecord tab, Func<TabRecord, TabRecord> mutate)
    {
        if (tab is null) return;
        var updated = mutate(tab);
        ReplaceTab(updated);
        _persistence.SaveTab(updated);
    }

    public async Task<TabRecord?> ReopenRecentlyClosedAsync(CancellationToken ct = default)
    {
        if (_recentlyClosed.Count == 0)
        {
            var stored = _persistence.LoadRecentlyClosed(1);
            if (stored.Count == 0) return null;
            return await OpenNewAsync(stored[0].Url, true, ct).ConfigureAwait(false);
        }
        var first = _recentlyClosed.First!.Value;
        _recentlyClosed.RemoveFirst();
        _persistence.DeleteRecentlyClosed(first.OriginalTabId);
        return await OpenNewAsync(first.Url, true, ct).ConfigureAwait(false);
    }

    public IReadOnlyList<TabStateSummary> GetStateSnapshots()
    {
        var list = new List<TabStateSummary>(Tabs.Count);
        foreach (var t in Tabs)
        {
            list.Add(new TabStateSummary(t.Id, (Encomm.Browser.Memory.TabRendererState)(int)t.RendererState));
        }
        return list;
    }

    private void ReplaceTab(TabRecord updated)
    {
        var idx = Tabs.IndexOf(Tabs.FirstOrDefault(t => t.Id == updated.Id)!);
        if (idx < 0) return;
        Tabs[idx] = updated;
        _byId[updated.Id] = updated;
    }

    public IReadOnlyList<RecentlyClosedRecord> RecentlyClosed
    {
        get
        {
            if (_recentlyClosed.Count == 0)
            {
                var loaded = _persistence.LoadRecentlyClosed(RecentlyClosedLimit);
                foreach (var r in loaded) _recentlyClosed.AddLast(r);
            }
            return _recentlyClosed.ToList();
        }
    }

    public IEnumerable<TabRecord> TabsInCurrentWorkspace() => Tabs;

    /// <summary>
    /// Replace the title on a tab. Used by the BrowserRuntime to surface
    /// `TitleChanged` events without holding a mutable reference.
    /// </summary>
    public void MutateTitle(Guid tabId, string newTitle)
    {
        if (!_byId.TryGetValue(tabId, out var tab)) return;
        if (tab.Title == newTitle) return;
        ReplaceTab(tab with { Title = newTitle });
        _persistence.SaveTab(_byId[tabId]);
    }

    public void MutateNavigationCompleted(Guid tabId, string url)
    {
        if (!_byId.TryGetValue(tabId, out var tab)) return;
        ReplaceTab(tab with { Url = url, LastInteractionUtc = DateTimeOffset.UtcNow });
        _persistence.SaveTab(_byId[tabId]);
    }
}

/// <summary>
/// Tiny ambient accessor so the TabService (which intentionally knows
/// nothing about UI) can still find the active workspace. The App
/// composition root sets this.
/// </summary>
public static class WorkspaceContextAccessor
{
    public static WorkspaceService? Current { get; set; }
}