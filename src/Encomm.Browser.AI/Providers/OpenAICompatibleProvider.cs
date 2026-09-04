using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Encomm.Browser.AI;

/// <summary>
/// OpenAI-compatible chat provider. Works with OpenAI, OpenRouter,
/// LM Studio, llama.cpp servers, vLLM and most local gateways.
/// </summary>
public sealed class OpenAICompatibleProvider : IAIProvider
{
    public string ProviderId => "openai-compatible";

    public IChatProvider Chat { get; }
    public IEmbeddingProvider? Embeddings { get; }

    private readonly Func<Task<string?>> _secretAccessor;
    private readonly AIProviderConfiguration _config;
    private readonly HttpClient _http;

    public OpenAICompatibleProvider(
        AIProviderConfiguration config,
        Func<Task<string?>> secretAccessor,
        HttpClient? httpClient = null)
    {
        _config = config;
        _secretAccessor = secretAccessor;
        _http = httpClient ?? new HttpClient();
        Chat = new OpenAIChat(this);
        Embeddings = new OpenAIEmbeddings(this);
    }

    internal AIProviderConfiguration Config => _config;
    internal HttpClient Http => _http;

    internal async Task<string?> GetSecretAsync(CancellationToken ct)
    {
        var s = await _secretAccessor().ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var baseUrl = _config.BaseUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl)) return false;
            var secret = await GetSecretAsync(ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
            if (!string.IsNullOrEmpty(secret))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            foreach (var kv in _config.CustomHeaders)
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private sealed class OpenAIChat : IChatProvider
    {
        private readonly OpenAICompatibleProvider _owner;
        public OpenAIChat(OpenAICompatibleProvider owner) { _owner = owner; }
        public string ProviderId => "openai-compatible";

        public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            var baseUrl = _owner.Config.BaseUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl))
                throw new InvalidOperationException("AI provider base URL not configured.");
            var secret = await _owner.GetSecretAsync(ct);
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
            if (!string.IsNullOrEmpty(secret))
                httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            foreach (var kv in _owner.Config.CustomHeaders)
                httpReq.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            var payload = new
            {
                model = string.IsNullOrEmpty(request.Model) ? _owner.Config.Model : request.Model,
                messages = request.Messages.Select(m => new { role = m.Role, content = m.Content }),
                temperature = request.Temperature,
                max_tokens = request.MaxTokens,
                stream = false
            };
            httpReq.Content = JsonContent.Create(payload);
            using var resp = await _owner.Http.SendAsync(httpReq, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException($"AI provider returned {(int)resp.StatusCode}: {Truncate(err, 200)}");
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            string? content = null;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
                    content = c.GetString();
            }
            string? modelUsed = root.TryGetProperty("model", out var m) ? m.GetString() : _owner.Config.Model;
            return new ChatResponse(content ?? "", modelUsed, null);
        }
    }

    private sealed class OpenAIEmbeddings : IEmbeddingProvider
    {
        private readonly OpenAICompatibleProvider _owner;
        public OpenAIEmbeddings(OpenAICompatibleProvider owner) { _owner = owner; }
        public string ProviderId => "openai-compatible";

        public async Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default)
        {
            var baseUrl = _owner.Config.BaseUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl))
                throw new InvalidOperationException("AI provider base URL not configured.");
            var secret = await _owner.GetSecretAsync(ct);
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/embeddings");
            if (!string.IsNullOrEmpty(secret))
                httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            foreach (var kv in _owner.Config.CustomHeaders)
                httpReq.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            httpReq.Content = JsonContent.Create(new { model = request.Model, input = request.Inputs });
            using var resp = await _owner.Http.SendAsync(httpReq, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Embeddings request failed: {(int)resp.StatusCode}");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var data = doc.RootElement.GetProperty("data");
            var vectors = new List<float[]>(data.GetArrayLength());
            foreach (var item in data.EnumerateArray())
            {
                var arr = item.GetProperty("embedding").EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();
                vectors.Add(arr);
            }
            return new EmbeddingResponse(vectors, request.Model);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";
}

/// <summary>Deterministic, dependency-free provider used for tests and offline UX demos.</summary>
public sealed class MockAIProvider : IAIProvider
{
    public string ProviderId => "mock";
    public IChatProvider Chat { get; } = new MockChat();
    public IEmbeddingProvider Embeddings { get; } = new MockEmbeddings();

    public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);

    private sealed class MockChat : IChatProvider
    {
        public string ProviderId => "mock";
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            var last = request.Messages.LastOrDefault()?.Content ?? "";
            var excerpt = last.Length <= 600 ? last : last.Substring(0, 600);
            var msg = "(offline mock) No AI provider configured. " +
                      "Set an OpenAI-compatible provider in Settings to enable real responses.\n\n" +
                      "Received prompt preview:\n" + excerpt;
            return Task.FromResult(new ChatResponse(msg, "mock", null));
        }
    }

    private sealed class MockEmbeddings : IEmbeddingProvider
    {
        public string ProviderId => "mock";
        public Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default)
        {
            var list = new List<float[]>();
            foreach (var s in request.Inputs)
            {
                var arr = new float[8];
                unchecked
                {
                    int hash = 17;
                    foreach (var ch in s) hash = hash * 31 + ch;
                    for (int i = 0; i < arr.Length; i++) arr[i] = ((hash >> i) & 0xFF) / 255f;
                }
                list.Add(arr);
            }
            return Task.FromResult(new EmbeddingResponse(list, "mock"));
        }
    }
}

/// <summary>
/// Router that prefers the configured provider and falls back to the
/// mock. Returns mock when AI is not configured at all so the UI never
/// crashes.
/// </summary>
public sealed class DefaultModelRouter : IModelRouter
{
    private readonly IAIProvider _provider;
    public DefaultModelRouter(IAIProvider provider) { _provider = provider; }
    public bool IsConfigured => _provider.ProviderId != "mock";
    public IChatProvider Chat => _provider.Chat;
    public IEmbeddingProvider? Embeddings => _provider.Embeddings;
    public Task<bool> TestConnectionAsync(CancellationToken ct = default) => _provider.TestConnectionAsync(ct);
}