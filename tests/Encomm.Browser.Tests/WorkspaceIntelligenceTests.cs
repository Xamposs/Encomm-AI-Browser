using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Encomm.Browser.AI;
using Encomm.Browser.App.Services;
using Encomm.Browser.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encomm.Browser.Tests;

/// <summary>
/// Phase 3B — Workspace Intelligence MVP.
///
/// These tests protect the product promises that make ENCOMM AI different
/// from a chatbot sidebar: bounded context, honest disclosure, real source
/// traceability, and a browser that stays usable with AI unconfigured.
/// </summary>
public class WorkspaceIntelligenceTests
{
    private static TabContextInput Tab(
        string url,
        bool active = false,
        string? body = null,
        string? selection = null,
        string? description = null,
        DateTimeOffset? lastInteraction = null,
        string title = "Tab")
        => new(Guid.NewGuid(), title, url, active,
            lastInteraction ?? DateTimeOffset.UtcNow, description, body, selection);

    // ---- Context builder ---------------------------------------------

    [Fact]
    public void Context_ranks_the_active_tab_first()
    {
        var recent = Tab("https://other.example/", lastInteraction: DateTimeOffset.UtcNow);
        var active = Tab("https://active.example/", active: true,
            lastInteraction: DateTimeOffset.UtcNow.AddHours(-5));

        var built = AIContextBuilder.Build(new[] { recent, active });

        Assert.Equal(2, built.Sources.Count);
        Assert.Equal("S1", built.Sources[0].Label);
        Assert.Equal(active.TabId, built.Sources[0].TabId);
        Assert.Contains("(active tab)", built.RenderedText);
    }

    [Fact]
    public void Context_never_sends_native_or_blank_tabs()
    {
        var built = AIContextBuilder.Build(new[]
        {
            Tab("encomm://newtab"),
            Tab("about:blank"),
            Tab("")
        });

        Assert.True(built.IsEmpty);
        Assert.Equal(0, built.TabsConsidered);
        Assert.Empty(built.Sources);
    }

    [Fact]
    public void Context_caps_the_number_of_contributing_tabs_and_discloses_omissions()
    {
        var tabs = Enumerable.Range(0, 30).Select(i => Tab($"https://example.com/{i}")).ToList();

        var built = AIContextBuilder.Build(tabs, new AIContextLimits(MaxTabs: 24));

        Assert.Equal(24, built.Sources.Count);
        Assert.Equal(30, built.TabsConsidered);
        Assert.Equal(6, built.TabsOmitted);
        Assert.True(built.Truncated);
    }

    [Fact]
    public void Context_enforces_the_per_tab_character_budget()
    {
        var body = new string('x', 500);
        var built = AIContextBuilder.Build(
            new[] { Tab("https://example.com/", body: body) },
            new AIContextLimits(MaxCharsPerTab: 100));

        var source = Assert.Single(built.Sources);
        Assert.Equal(100, source.CharsUsed);
        Assert.True(source.ContentOmitted);
        Assert.True(built.Truncated);
    }

    [Fact]
    public void Context_enforces_the_total_budget_and_drops_what_does_not_fit()
    {
        var body = new string('y', 8);
        var tabs = Enumerable.Range(0, 3)
            .Select(i => Tab($"https://example.com/{i}", body: body))
            .ToList();

        var built = AIContextBuilder.Build(tabs, new AIContextLimits(MaxCharsPerTab: 100, MaxTotalChars: 10));

        Assert.Equal(2, built.Sources.Count);
        Assert.Equal(8, built.Sources[0].CharsUsed);
        Assert.Equal(2, built.Sources[1].CharsUsed);
        Assert.True(built.Sources[1].ContentOmitted);
        Assert.Equal(1, built.TabsOmitted);
    }

    [Fact]
    public void Context_prefers_the_user_selection_over_the_page_body()
    {
        var tab = Tab("https://example.com/", body: "BODY TEXT", selection: "SELECTED TEXT");

        var built = AIContextBuilder.Build(new[] { tab });
        var source = Assert.Single(built.Sources);

        Assert.Equal(AIContextKind.Selection, source.Kind);
        Assert.Contains("SELECTED TEXT", built.RenderedText);
        Assert.DoesNotContain("BODY TEXT", built.RenderedText);
    }

    [Fact]
    public void Context_labels_metadata_only_sources_instead_of_pretending_it_read_them()
    {
        var built = AIContextBuilder.Build(new[] { Tab("https://example.com/", title: "Cold tab") });
        var source = Assert.Single(built.Sources);

        Assert.Equal(AIContextKind.TabMetadata, source.Kind);
        Assert.Equal(0, source.CharsUsed);
        Assert.Contains("metadata only", built.RenderedText);
        Assert.Contains("Cold tab", built.RenderedText);
    }

    // ---- Structured results and traceability --------------------------

    private static SourceLabelIndex IndexOf(params (string Label, string Url)[] sources) =>
        SourceLabelIndex.From(sources
            .Select(s => new ContextSource(s.Label, Guid.NewGuid(), "T" + s.Label, s.Url,
                AIContextKind.PageContent, 10, false))
            .ToList());

    [Fact]
    public void Parser_reads_plain_json_into_items_facts_and_uncertainties()
    {
        var json = """
        {"title":"Comparison","summary":"Two options.","items":[
          {"label":"Option A","detail":"cheaper","facts":{"price":"999","memory":"16GB"},"sourceLabels":["S1"]}
        ],"uncertainties":["shipping cost unknown"]}
        """;

        var result = StructuredResultParser.Parse(json, "Compare tabs");

        Assert.Equal(AIResultStatus.Ok, result.Status);
        Assert.Equal("Comparison", result.Title);
        Assert.Equal("Two options.", result.Summary);
        var item = Assert.Single(result.Items);
        Assert.Equal("Option A", item.Label);
        Assert.Equal("999", item.Facts["price"]);
        Assert.Equal("16GB", item.Facts["memory"]);
        Assert.Equal("shipping cost unknown", Assert.Single(result.Uncertainties));
    }

    [Fact]
    public void Parser_reads_json_wrapped_in_prose_and_code_fences()
    {
        var raw = "Here you go:\n```json\n{\"summary\":\"fenced\",\"items\":[]}\n```\nHope that helps.";

        var result = StructuredResultParser.Parse(raw, "Summarize this page");

        Assert.Equal("fenced", result.Summary);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void Parser_resolves_citations_and_drops_invented_sources()
    {
        var index = IndexOf(("S1", "https://a.example/"), ("S2", "https://b.example/"));
        var sources = index.ResolveSources(new[] { "S1", "S2" });
        var json = """
        {"summary":"s","items":[
          {"label":"A","sourceLabels":["S1","S9","[S2]"]},
          {"label":"B","sourceLabels":["S99"]}
        ]}
        """;

        var result = StructuredResultParser.Parse(json, "Compare tabs", index, sources: sources);

        Assert.Equal(2, result.Items[0].SourceLabels.Count);
        Assert.Equal(2, result.Items[0].SourceTabIds.Count);
        Assert.Equal(sources[0].TabId, result.Items[0].SourceTabIds[0]);
        Assert.Empty(result.Items[1].SourceLabels);
        Assert.Empty(result.Items[1].SourceTabIds);
    }

    [Fact]
    public void Parser_falls_back_to_text_for_unstructured_output()
    {
        var result = StructuredResultParser.Parse("Just a plain sentence, no JSON.", "Summarize this page");

        Assert.Equal(AIResultStatus.Ok, result.Status);
        Assert.Equal("Just a plain sentence, no JSON.", result.Summary);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void Parser_handles_empty_output_without_throwing()
    {
        var result = StructuredResultParser.Parse("   ", "Summarize this page");

        Assert.Equal(AIResultStatus.Ok, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
    }

    [Fact]
    public void Parser_ignores_braces_inside_string_values()
    {
        var json = StructuredResultParser.ExtractJsonObject("{\"summary\":\"a } inside\",\"items\":[]} tail");

        Assert.Equal("{\"summary\":\"a } inside\",\"items\":[]}", json);
    }

    [Fact]
    public void Result_plain_text_keeps_the_result_traceable()
    {
        var index = IndexOf(("S1", "https://a.example/"));
        var sources = index.ResolveSources(new[] { "S1" });
        var result = StructuredResultParser.Parse(
            "{\"summary\":\"s\",\"items\":[{\"label\":\"A\",\"sourceLabels\":[\"S1\"]}]}",
            "Compare tabs", index, sources: sources);

        var text = result.ToPlainText();

        Assert.Contains("Sources:", text);
        Assert.Contains("https://a.example/", text);
        Assert.Contains("S1", text);
    }

    [Fact]
    public void Unconfigured_wording_stays_free_of_provider_jargon()
    {
        var result = AIResult.NotConfigured("Summarize this page");

        Assert.Equal(AIResultStatus.NotConfigured, result.Status);
        Assert.Contains("not configured", result.Summary);
        Assert.Contains("Settings", result.Summary);
        Assert.DoesNotContain("openai", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("renderer", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ---- AIService over the fake engine (no WebView2) -----------------

    [Fact]
    public async Task AIService_reports_unconfigured_without_touching_the_provider()
    {
        using var ctx = SharedContext.New();
        var live = ctx.AddLiveTab("https://example.com/");
        // FakeRouter throws if anything asks for a chat provider, which
        // proves the unconfigured path never reaches a model.
        var ai = new AIService(new FakeRouter(), new FakeSecretStore(), ctx.Runtime,
            NullLogger<AIService>.Instance);

        var result = await ai.RunAsync(new AICommandRequest("summarize-page", live, new[] { live }));

        Assert.Equal(AIResultStatus.NotConfigured, result.Status);
        Assert.False(result.IsUsable);
        Assert.Contains("not configured", result.Summary);
    }

    [Fact]
    public async Task AIService_builds_a_bounded_context_and_lists_every_source()
    {
        using var ctx = SharedContext.New();
        var ghostA = ctx.AddGhostTab("https://a.example/");
        var ghostB = ctx.AddGhostTab("https://b.example/");
        var live = ctx.AddLiveTab("https://active.example/");
        var router = new RecordingRouter(
            "{\"summary\":\"ok\",\"items\":[{\"label\":\"A\",\"sourceLabels\":[\"S1\"]}]}");
        var ai = new AIService(router, new FakeSecretStore(), ctx.Runtime, NullLogger<AIService>.Instance);

        var result = await ai.RunAsync(new AICommandRequest(
            "summarize-workspace", live, new[] { ghostA, ghostB, live }));

        Assert.Equal(AIResultStatus.Ok, result.Status);
        Assert.Equal(3, result.Sources.Count);
        Assert.Equal(live.Id, result.Sources[0].TabId);
        Assert.Equal(AIContextKind.TabMetadata, result.Sources[1].Kind);
        Assert.Equal(AIContextKind.TabMetadata, result.Sources[2].Kind);
        Assert.Single(router.Requests);

        // Traceability: the cited label resolves back to the real tab.
        var item = Assert.Single(result.Items);
        Assert.Equal(live.Id, Assert.Single(item.SourceTabIds));
    }

    [Fact]
    public async Task AIService_never_wakes_cold_tabs_just_to_build_context()
    {
        using var ctx = SharedContext.New();
        var ghost = ctx.AddGhostTab("https://cold.example/");
        var live = ctx.AddLiveTab("https://active.example/");
        var router = new RecordingRouter("{\"summary\":\"ok\"}");
        var ai = new AIService(router, new FakeSecretStore(), ctx.Runtime, NullLogger<AIService>.Instance);

        await ai.RunAsync(new AICommandRequest("summarize-workspace", live, new[] { ghost, live }));

        // No page extraction, no restore, no renderer allocation for the
        // cold tab: AI must not change the memory lifecycle.
        Assert.DoesNotContain("context", ctx.ViewFor(ghost.Id).CallLog);
        Assert.False(ctx.Runtime.HasView(ghost.Id));
    }

    [Fact]
    public async Task AIService_page_scope_uses_only_the_active_tab()
    {
        using var ctx = SharedContext.New();
        var other = ctx.AddLiveTab("https://other.example/");
        var live = ctx.AddLiveTab("https://active.example/");
        var router = new RecordingRouter("{\"summary\":\"ok\"}");
        var ai = new AIService(router, new FakeSecretStore(), ctx.Runtime, NullLogger<AIService>.Instance);

        var result = await ai.RunAsync(new AICommandRequest("summarize-page", live, new[] { other, live }));

        Assert.Single(result.Sources);
        Assert.Equal(live.Id, result.Sources[0].TabId);
    }

    [Fact]
    public async Task AIService_bounds_the_question_and_marks_sources_untrusted()
    {
        using var ctx = SharedContext.New();
        var live = ctx.AddLiveTab("https://example.com/");
        var router = new RecordingRouter("{\"summary\":\"s\"}");
        var ai = new AIService(router, new FakeSecretStore(), ctx.Runtime, NullLogger<AIService>.Instance);

        await ai.RunAsync(new AICommandRequest(
            "ask-page", live, new[] { live }, "what is this?\u0007\u0000 and that?"));

        var request = Assert.Single(router.Requests);
        Assert.Equal("system", request.Messages[0].Role);
        Assert.Contains("untrusted", request.Messages[0].Content);
        Assert.Contains("ENCOMM", request.Messages[0].Content);

        var user = request.Messages[1].Content;
        Assert.Contains("what is this? and that?", user);
        Assert.DoesNotContain('\u0007', user);
        Assert.DoesNotContain('\u0000', user);
        // The whole prompt stays inside the context budget.
        Assert.True(user.Length < 20000, $"prompt was {user.Length} chars");
    }

    [Fact]
    public async Task AIService_reports_no_sources_for_a_native_new_tab()
    {
        using var ctx = SharedContext.New();
        var native = ctx.AddLiveTab("encomm://newtab");
        var router = new RecordingRouter("{\"summary\":\"s\"}");
        var ai = new AIService(router, new FakeSecretStore(), ctx.Runtime, NullLogger<AIService>.Instance);

        var result = await ai.RunAsync(new AICommandRequest("summarize-page", native, new[] { native }));

        Assert.Equal(AIResultStatus.NoSources, result.Status);
        Assert.Empty(router.Requests);
    }

    [Fact]
    public async Task AIService_never_throws_when_the_provider_fails()
    {
        using var ctx = SharedContext.New();
        var live = ctx.AddLiveTab("https://example.com/");
        var ai = new AIService(new FailingRouter(), new FakeSecretStore(), ctx.Runtime,
            NullLogger<AIService>.Instance);

        var result = await ai.RunAsync(new AICommandRequest("summarize-page", live, new[] { live }));

        Assert.Equal(AIResultStatus.Failed, result.Status);
        Assert.Contains("could not complete", result.Summary);
        Assert.NotNull(result.Diagnostic);
        Assert.Empty(result.Items);
    }
}

/// <summary>Configured test router that records every request it receives.</summary>
internal sealed class RecordingRouter : IModelRouter
{
    public RecordingRouter(string reply) => Chat = new RecordingChat(this, reply);

    public bool IsConfigured => true;
    public IChatProvider Chat { get; }
    public IEmbeddingProvider? Embeddings => null;
    public List<ChatRequest> Requests { get; } = new();

    public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);

    private sealed class RecordingChat : IChatProvider
    {
        private readonly RecordingRouter _owner;
        private readonly string _reply;
        public RecordingChat(RecordingRouter owner, string reply) { _owner = owner; _reply = reply; }
        public string ProviderId => "test-recording";

        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            _owner.Requests.Add(request);
            return Task.FromResult(new ChatResponse(_reply, "test-recording", null));
        }
    }
}

/// <summary>Configured test router whose transport always fails.</summary>
internal sealed class FailingRouter : IModelRouter
{
    public bool IsConfigured => true;
    public IChatProvider Chat { get; } = new FailingChat();
    public IEmbeddingProvider? Embeddings => null;
    public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(false);

    private sealed class FailingChat : IChatProvider
    {
        public string ProviderId => "test-failing";

        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default) =>
            throw new System.Net.Http.HttpRequestException("simulated transport failure");
    }
}