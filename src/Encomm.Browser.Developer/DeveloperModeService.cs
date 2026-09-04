namespace Encomm.Browser.Developer;

/// <summary>
/// Process-wide toggle and access surface for Developer Mode. The UI
/// queries <see cref="IsEnabled"/> to decide whether to show Developer
/// surfaces, and the host uses <see cref="Diagnostics"/> to expose
/// extra capabilities.
/// </summary>
public sealed class DeveloperModeService
{
    public bool IsEnabled { get; private set; }
    public DeveloperDiagnostics Diagnostics { get; } = new();

    public void Enable() => IsEnabled = true;
    public void Disable() => IsEnabled = false;
    public void Toggle() => IsEnabled = !IsEnabled;
}

public sealed class DeveloperDiagnostics
{
    public bool ShowMemoryPanel { get; set; }
    public bool ShowDevToolsOnF12 { get; set; } = true;
    public bool ShowRendererState { get; set; } = true;
    public bool ShowShieldDiagnostics { get; set; } = true;
    public bool ShowAIDiagnostics { get; set; }
    public bool VerboseLogging { get; set; }
}