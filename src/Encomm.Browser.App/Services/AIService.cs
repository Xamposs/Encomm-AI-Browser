using System.Text;
using Microsoft.Extensions.Logging;
using Encomm.Browser.AI;
using Encomm.Browser.Engine.Abstractions;
using Encomm.Browser.App.Services;
using Encomm.Browser.Core.Storage;
using Encomm.Browser.Settings;

namespace Encomm.Browser.App.Services;

/// <summary>
/// Routes AI actions for the user. Never throws into the UI. Always
/// returns a human-readable string result.
/// </summary>
public sealed class AIService
{
    private readonly IModelRouter _router;
    private readonly ISecretStore _secrets;
    private readonly BrowserEngineRegistry _engineRegistry;
    private readonly ILogger<AIService> _log;
    private const string SecretName = "ai.primary";

    public bool IsConfigured => _router.IsConfigured;
    public string? CurrentProviderName { get; private set; }

    public AIService(IModelRouter router, ISecretStore secrets, BrowserEngineRegistry engineRegistry, ILogger<AIService> log)
    {
        _router = router;
        _secrets = secrets;
        _engineRegistry = engineRegistry;
        _log = log;
    }

    public async Task<string> RunAsync(string commandId, TabRecord? activeTab, IReadOnlyList<TabRecord> workspaceTabs)
    {
        try
        {
            return commandId switch
            {
                "summarize-page" => await SummarizePageAsync(activeTab),
                "explain-selection" => await ExplainSelectionAsync(activeTab),
                "ask-page" => await AskAboutPageAsync(activeTab),
                "compare-workspace" => await CompareWorkspaceAsync(workspaceTabs),
                "organize-workspace" => await OrganizeWorkspaceAsync(workspaceTabs),
                "extract-info" => await ExtractInfoAsync(activeTab),
                _ => "Unknown AI command."
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AI command failed: {Id}", commandId);
            return "AI is currently unavailable. " + ex.Message;
        }
    }

    private async Task<string> SummarizePageAsync(TabRecord? tab)
    {
        var ctx = await ExtractAsync(tab);
        if (ctx is null) return "No page to summarize.";
        var prompt = $"Summarize the following page in 3 short bullet points:\n\nTitle: {ctx.Title}\nURL: {ctx.Url}\n\n{ctx.BodyExcerpt ?? ctx.Description ?? "(no content)"}";
        return await AskAsync("You summarize web pages concisely.", prompt);
    }

    private async Task<string> ExplainSelectionAsync(TabRecord? tab)
    {
        var ctx = await ExtractAsync(tab);
        if (ctx?.SelectedText is null or { Length: 0 }) return "No text selection on the active page.";
        var prompt = $"Explain the selected passage from the page '{ctx.Title}'.\n\nSelection:\n\"{ctx.SelectedText}\"";
        return await AskAsync("You explain short web selections in plain English.", prompt);
    }

    private async Task<string> AskAboutPageAsync(TabRecord? tab)
    {
        var ctx = await ExtractAsync(tab);
        if (ctx is null) return "No page to ask about.";
        return "Ask a question about the current page using the AI command bar.";
    }

    private async Task<string> CompareWorkspaceAsync(IReadOnlyList<TabRecord> tabs)
    {
        if (tabs.Count == 0) return "Workspace is empty.";
        var sb = new StringBuilder();
        foreach (var t in tabs)
            sb.AppendLine($"- {t.Title} ({t.Url})");
        var prompt = $"Compare these open tabs in 5 short bullets, grouped by similarity:\n\n{sb}";
        return await AskAsync("You compare pages in a workspace by topic.", prompt);
    }

    private async Task<string> OrganizeWorkspaceAsync(IReadOnlyList<TabRecord> tabs)
    {
        if (tabs.Count == 0) return "Workspace is empty.";
        var sb = new StringBuilder();
        foreach (var t in tabs) sb.AppendLine($"- {t.Title} | {t.Url}");
        var prompt = $"Group these open tabs into logical clusters (max 5 clusters) by topic. Each cluster gets a name and a list of tab titles.\n\n{sb}";
        return await AskAsync("You organize tabs into topic clusters.", prompt);
    }

    private async Task<string> ExtractInfoAsync(TabRecord? tab)
    {
        var ctx = await ExtractAsync(tab);
        if (ctx is null) return "No page to extract from.";
        var prompt = $"Extract the most important facts, names, numbers, dates and definitions from the page '{ctx.Title}'. Use bullet points.\n\n{ctx.BodyExcerpt ?? ctx.Description ?? "(no content)"}";
        return await AskAsync("You extract key information from web pages as bullet points.", prompt);
    }

    private async Task<string> AskAsync(string system, string user)
    {
        var req = new ChatRequest(
            Model: ModelOrDefault(),
            Messages: new[] { new ChatMessage("system", system), new ChatMessage("user", user) },
            Temperature: 0.2);
        var resp = await _router.Chat.ChatAsync(req);
        return string.IsNullOrEmpty(resp.Content) ? "(no response)" : resp.Content!;
    }

    private string ModelOrDefault() => "default";

    private async Task<PageContext?> ExtractAsync(TabRecord? tab)
    {
        if (tab is null) return null;
        var view = _engineRegistry.GetOrCreate(tab);
        if (view is null) return null;
        return await view.ExtractPageContextAsync();
    }
}