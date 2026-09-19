using Encomm.Browser.Core.Storage;

namespace Encomm.Browser.App.Services;

/// <summary>What a command looks at. Drives the bounded context budget.</summary>
public enum AICommandScope
{
    /// <summary>Only the active page.</summary>
    Page = 0,
    /// <summary>The tabs in the current workspace.</summary>
    Workspace = 1
}

/// <summary>
/// One user-facing ENCOMM AI action. Labels are Everyday-Mode language:
/// no tokens, no models, no provider names.
/// </summary>
public sealed record AICommandDefinition(
    string Id,
    string Label,
    AICommandScope Scope,
    bool RequiresQuestion = false);

/// <summary>Everything one AI action needs. Immutable and UI-free.</summary>
public sealed record AICommandRequest(
    string CommandId,
    TabRecord? ActiveTab,
    IReadOnlyList<TabRecord> WorkspaceTabs,
    string? Question = null);
