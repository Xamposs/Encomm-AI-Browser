using System.Collections.ObjectModel;
using Encomm.Browser.Tabs;
using Encomm.Browser.Workspaces;
using Encomm.Browser.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Encomm.Browser.App.Services;

public sealed class WorkspaceService
{
    private readonly BrowserPersistenceService _persistence;
    private readonly ILogger<WorkspaceService> _log;
    public ObservableCollection<WorkspaceRecord> Workspaces { get; } = new();
    public WorkspaceRecord ActiveWorkspace { get; private set; } = new(BuiltInWorkspaces.PersonalId, "Personal", 0, true, DateTimeOffset.UtcNow);

    public event EventHandler? ActiveWorkspaceChanged;

    public WorkspaceService(BrowserPersistenceService persistence, ILogger<WorkspaceService> log)
    {
        _persistence = persistence;
        _log = log;
        Reload();
    }

    public void Reload()
    {
        var stored = _persistence.LoadWorkspaces();
        Workspaces.Clear();
        if (stored.Count == 0)
        {
            var personal = new WorkspaceRecord(BuiltInWorkspaces.PersonalId, "Personal", 0, true, DateTimeOffset.UtcNow);
            Workspaces.Add(personal);
            _persistence.SaveWorkspace(personal);
        }
        else
        {
            foreach (var w in stored) Workspaces.Add(w);
        }
        var lastActive = _persistence.GetAppState("active_workspace_id");
        if (Guid.TryParse(lastActive, out var id))
        {
            var match = Workspaces.FirstOrDefault(w => w.Id == id);
            if (match is not null) ActiveWorkspace = match;
            else ActiveWorkspace = Workspaces.First();
        }
        else
        {
            ActiveWorkspace = Workspaces.First();
        }
    }

    public WorkspaceRecord Create(string name)
    {
        var ws = new WorkspaceRecord(Guid.NewGuid(), name, Workspaces.Count, false, DateTimeOffset.UtcNow);
        Workspaces.Add(ws);
        _persistence.SaveWorkspace(ws);
        return ws;
    }

    public void Rename(Guid id, string newName)
    {
        var idx = Workspaces.IndexOf(Workspaces.First(w => w.Id == id));
        if (idx < 0) return;
        var current = Workspaces[idx];
        var updated = current with { Name = newName };
        Workspaces[idx] = updated;
        _persistence.SaveWorkspace(updated);
        if (ActiveWorkspace.Id == id) ActiveWorkspace = updated;
    }

    public void Delete(Guid id)
    {
        var ws = Workspaces.FirstOrDefault(w => w.Id == id);
        if (ws is null) return;
        if (ws.BuiltIn) return; // cannot delete the built-in Personal workspace
        Workspaces.Remove(ws);
        _persistence.DeleteWorkspace(id);
        if (ActiveWorkspace.Id == id) ActiveWorkspace = Workspaces.First();
    }

    public void SwitchTo(Guid id)
    {
        var match = Workspaces.FirstOrDefault(w => w.Id == id);
        if (match is null) return;
        ActiveWorkspace = match;
        _persistence.SetAppState("active_workspace_id", id.ToString());
        ActiveWorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }
}