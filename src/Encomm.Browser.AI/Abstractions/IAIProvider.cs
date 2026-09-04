namespace Encomm.Browser.AI;

/// <summary>One chat message. Engine-agnostic.</summary>
public sealed record ChatMessage(string Role, string Content);

public sealed record ChatRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    double Temperature = 0.2,
    int? MaxTokens = null,
    IReadOnlyDictionary<string, string>? CustomHeaders = null);

public sealed record ChatResponse(string Content, string? ModelUsed, IReadOnlyDictionary<string, string>? Metadata);

public sealed record EmbeddingRequest(string Model, IReadOnlyList<string> Inputs);
public sealed record EmbeddingResponse(IReadOnlyList<float[]> Vectors, string? ModelUsed);

/// <summary>
/// Abstraction over an AI text provider. Implementations are required
/// to be safe to instantiate lazily and must never block startup.
/// </summary>
public interface IChatProvider
{
    string ProviderId { get; }
    Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default);
}

/// <summary>Abstraction over an embedding provider.</summary>
public interface IEmbeddingProvider
{
    string ProviderId { get; }
    Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default);
}

/// <summary>
/// A combined provider that exposes both chat and embedding.
/// </summary>
public interface IAIProvider
{
    string ProviderId { get; }
    IChatProvider Chat { get; }
    IEmbeddingProvider? Embeddings { get; }
    Task<bool> TestConnectionAsync(CancellationToken ct = default);
}

/// <summary>
/// Routes between the configured provider and the local mock when no
/// provider is configured.
/// </summary>
public interface IModelRouter
{
    bool IsConfigured { get; }
    IChatProvider Chat { get; }
    IEmbeddingProvider? Embeddings { get; }
    Task<bool> TestConnectionAsync(CancellationToken ct = default);
}

/// <summary>
/// Strongly-typed configuration for the AI subsystem. The secret itself
/// lives in the secure store; this record contains only safe metadata.
/// </summary>
public sealed class AIProviderConfiguration
{
    public string ProviderId { get; set; } = "mock";
    public string? BaseUrl { get; set; }
    public string? Model { get; set; }
    public IReadOnlyDictionary<string, string> CustomHeaders { get; set; } = new Dictionary<string, string>();
    public bool HasSecret { get; set; }
}