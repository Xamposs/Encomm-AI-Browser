namespace Encomm.Browser.Workspaces;

/// <summary>
/// Logical grouping of tabs. Switching workspace should encourage
/// aggressive renderer release for inactive workspaces.
/// </summary>
public sealed class Workspace
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Personal";
    public int OrderIndex { get; set; }
    public bool BuiltIn { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public static class BuiltInWorkspaces
{
    public static readonly Guid PersonalId = new("11111111-1111-1111-1111-111111111111");
}