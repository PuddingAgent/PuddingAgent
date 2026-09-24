using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PuddingVectorIndex;

namespace PuddingRetrievalEvalProbe;

/// <summary>
/// How a provider was used during one phase. Snapshotted at phase boundaries so the report can state
/// index-side calls/time apart from query-side calls/time instead of blending them.
/// </summary>
internal sealed record EmbeddingCallStats(int Calls, int Texts, long ElapsedMs, int Failures, long? PromptTokens)
{
    public static EmbeddingCallStats operator -(EmbeddingCallStats after, EmbeddingCallStats before) =>
        new(
            after.Calls - before.Calls,
            after.Texts - before.Texts,
            after.ElapsedMs - before.ElapsedMs,
            after.Failures - before.Failures,
            after.PromptTokens is { } a && before.PromptTokens is { } b ? a - b : after.PromptTokens);

    public override string ToString() =>
        $"calls={Calls} texts={Texts} elapsedMs={ElapsedMs} failures={Failures} "
        + $"promptTokens={(PromptTokens?.ToString() ?? "not reported by the service")}";
}

/// <summary>
/// Thin adapter that turns a <see cref="EmbeddingRoute"/> into a live vector service over the
/// OpenAI-compatible <c>/v1/embeddings</c> endpoint (task book §2.1: 适配器放探针侧).
/// <para>
/// <b>Why a thin adapter instead of reusing the production implementation.</b> The production vector
/// implementation (<c>PuddingRuntime/Services/OpenAiEmbeddingService.cs</c>) lives in PuddingRuntime,
/// which drags in the whole agent runtime (DI composition, approval, messaging) and would put a
/// ~100-project dependency edge behind a probe that must run with the host live and must not touch it.
/// The trade-off is recorded in the U4-1b report: this adapter is re-stated at the same level of
/// detail (same protocol, same batching semantics) as a ~190-line file rather than pulling the runtime
/// in. It is also the only place in this slice that holds a baseUrl/apiKey — the component does not.
/// </para>
/// <para>
/// It counts calls/texts/time/failures because "embedding 调用次数与耗时" is a required axis and a
/// number produced by a probe's own bookkeeping is the only honest source for it.
/// </para>
/// </summary>
internal sealed class OpenAiCompatibleEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly EmbeddingRoute _route;
    private readonly int _declaredDimensions;

    private int _observedDimensions;
    private int _calls;
    private int _texts;
    private long _elapsedMs;
    private int _failures;
    private long _promptTokens;
    private bool _promptTokensReported;

    public OpenAiCompatibleEmbeddingProvider(
        EmbeddingRoute route,
        string baseUrl,
        string? apiKey,
        int declaredDimensions,
        int maxContextTokens,
        bool isLocal,
        HttpMessageHandler? handler = null)
    {
        _route = route;
        _declaredDimensions = declaredDimensions;
        MaxContextTokens = maxContextTokens;
        IsLocal = isLocal;
        Endpoint = baseUrl.TrimEnd('/') + "/embeddings";

        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = TimeSpan.FromMinutes(5);

        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
    }

    /// <summary>Absolute endpoint the adapter posts to (printed in the report, credentials never are).</summary>
    public string Endpoint { get; }

    public bool IsLocal { get; }

    public int MaxContextTokens { get; }

    /// <summary>Dimension the config claimed, or <c>0</c> when the config did not state one.</summary>
    public int DeclaredDimensions => _declaredDimensions;

    /// <summary>Dimension the service actually returned; <c>0</c> before the first successful call.</summary>
    public int ObservedDimensions => _observedDimensions;

    public string RouteLabel => _route.ToString();

    public EmbeddingCallStats Stats =>
        new(_calls, _texts, _elapsedMs, _failures, _promptTokensReported ? _promptTokens : null);

    /// <summary>
    /// Observed dimension when known, else the declared one; <c>0</c> when neither is known.
    /// The builder refuses a non-positive dimension, so "unknown" can never silently become an index.
    /// </summary>
    public EmbeddingModelInfo Describe() =>
        new(
            _route.ProviderId,
            _route.ModelId,
            _observedDimensions > 0 ? _observedDimensions : _declaredDimensions,
            MaxContextTokens,
            IsLocal,
            isAvailable: true);

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var vectors = await EmbedBatchAsync([text], cancellationToken).ConfigureAwait(false);
        return vectors[0];
    }

    public async Task<float[][]> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
            throw new ArgumentException("embedding batch must not be empty", nameof(texts));

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = _route.ModelId,
            ["input"] = texts.ToArray(),
        });

        using var content = new StringContent(payload, new UTF8Encoding(false), "application/json");

        var stopwatch = Stopwatch.StartNew();
        _calls++;
        _texts += texts.Count;

        try
        {
            using var response = await _http.PostAsync(Endpoint, content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"embedding request to {Endpoint} failed: HTTP {(int)response.StatusCode} "
                    + $"{response.ReasonPhrase}: {Truncate(body)}");

            var vectors = Parse(body, texts.Count);
            if (_observedDimensions == 0)
                _observedDimensions = vectors[0].Length;

            return vectors;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _failures++;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            _elapsedMs += stopwatch.ElapsedMilliseconds;
        }
    }

    public void Dispose() => _http.Dispose();

    private float[][] Parse(string body, int expected)
    {
        using var document = JsonDocument.Parse(body);

        if (document.RootElement.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("prompt_tokens", out var promptTokens)
            && promptTokens.ValueKind == JsonValueKind.Number)
        {
            _promptTokens += promptTokens.GetInt64();
            _promptTokensReported = true;
        }

        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"embedding response from {Endpoint} has no 'data' array: {Truncate(body)}");

        var ordered = new float[expected][];
        var position = 0;

        foreach (var item in data.EnumerateArray())
        {
            var index = item.TryGetProperty("index", out var indexElement)
                        && indexElement.ValueKind == JsonValueKind.Number
                ? indexElement.GetInt32()
                : position;

            if (!item.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(
                    $"embedding response from {Endpoint} has an item without an 'embedding' array: {Truncate(body)}");

            var vector = new float[embedding.GetArrayLength()];
            var slot = 0;
            foreach (var value in embedding.EnumerateArray())
                vector[slot++] = value.GetSingle();

            if (index < 0 || index >= expected)
                throw new InvalidOperationException(
                    $"embedding response from {Endpoint} reports index {index}, outside the {expected} inputs sent");

            if (ordered[index] is not null)
                throw new InvalidOperationException(
                    $"embedding response from {Endpoint} reports index {index} twice");

            ordered[index] = vector;
            position++;
        }

        if (position != expected)
            throw new InvalidOperationException(
                $"embedding response from {Endpoint} returned {position} vectors for {expected} inputs");

        for (var i = 0; i < ordered.Length; i++)
            if (ordered[i] is null)
                throw new InvalidOperationException(
                    $"embedding response from {Endpoint} is missing a vector for input {i}");

        return ordered;
    }

    private static string Truncate(string body) =>
        body.Length <= 300 ? body : body[..300] + "...";
}
