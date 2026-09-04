using Xunit;
using Encomm.Browser.AI;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Encomm.Browser.Tests;

public class AIProviderTests
{
    [Fact]
    public async Task Mock_provider_returns_offline_marker()
    {
        var p = new MockAIProvider();
        var resp = await p.Chat.ChatAsync(new ChatRequest(
            Model: "mock",
            Messages: new[] { new ChatMessage("user", "hello world") }));
        Assert.NotNull(resp.Content);
        Assert.Contains("No AI provider configured", resp.Content);
    }

    [Fact]
    public async Task Mock_provider_embeddings_returns_vectors()
    {
        var p = new MockAIProvider();
        var resp = await p.Embeddings!.EmbedAsync(new EmbeddingRequest("mock", new[] { "alpha", "beta" }));
        Assert.Equal(2, resp.Vectors.Count);
        foreach (var v in resp.Vectors) Assert.Equal(8, v.Length);
    }

    [Fact]
    public async Task Mock_provider_test_returns_true()
    {
        var p = new MockAIProvider();
        Assert.True(await p.TestConnectionAsync());
    }

    [Fact]
    public void DefaultModelRouter_reports_unconfigured_for_mock()
    {
        var r = new DefaultModelRouter(new MockAIProvider());
        Assert.False(r.IsConfigured);
        Assert.Equal("mock", r.Chat.ProviderId);
    }
}