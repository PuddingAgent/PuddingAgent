using System.Net;
using System.Text.Json;
using PuddingVectorIndex;

namespace PuddingRetrievalEvalProbe;

/// <summary>
/// What the composition root learned about one embedding route: where to call it, with which
/// credential, and the facts the four-axis report has to state (dimension, context window, locality,
/// price). Produced from the LLM resource-pool config file rather than from a hard-coded constant —
/// "换模型 = 改配置" (ADR-089 索引服务与库管理 §2).
/// </summary>
internal sealed record ResolvedEmbedding(
    EmbeddingRoute Route,
    string BaseUrl,
    string? ApiKey,
    int? Dimensions,
    int MaxContextTokens,
    bool IsLocal,
    double? PricePer1MInputTokens,
    string ConfigPath);

/// <summary>
/// Resolves the user-visible <c>"provider/model"</c> route against the resource-pool config, then turns
/// it into a callable endpoint. This is deliberately on the <b>probe</b> side (the probe is the
/// experiment's composition root): the vector component takes an <see cref="IEmbeddingProvider"/> and
/// never sees a URL, a key or a config file (ADR-089 §1 硬约束 1/2).
/// <para>
/// Fail-closed everywhere: an unknown provider, a disabled provider, an unregistered model, a model not
/// flagged as an embedding model, or a missing <c>baseUrl</c> are all errors with the offending path in
/// the message — never a silent fallback to some other route.
/// </para>
/// </summary>
internal static class EmbeddingRouteResolver
{
    /// <summary>The resource pool's own config; overridable with <c>--providers-config</c>.</summary>
    public const string DefaultProvidersConfigPath = @"D:\data\config\llm.providers.json";

    public static ResolvedEmbedding Resolve(
        string? configPath,
        string? routeOverride,
        string? baseUrlOverride,
        int? dimensionsOverride)
    {
        var path = string.IsNullOrWhiteSpace(configPath) ? DefaultProvidersConfigPath : configPath;

        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"LLM provider config not found at '{path}'; pass --providers-config <path>", path);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var route = ResolveRoute(root, routeOverride, path);

        var provider = FindProvider(root, route.ProviderId)
            ?? throw new InvalidOperationException(
                $"provider '{route.ProviderId}' is not registered in {path}");

        if (provider.TryGetProperty("isEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
            throw new InvalidOperationException($"provider '{route.ProviderId}' is disabled in {path}");

        var baseUrl = baseUrlOverride;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = provider.TryGetProperty("baseUrl", out var url) && url.ValueKind == JsonValueKind.String
                ? url.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException($"provider '{route.ProviderId}' declares no baseUrl in {path}");
        }

        var apiKey = provider.TryGetProperty("apiKey", out var key) && key.ValueKind == JsonValueKind.String
            ? key.GetString()
            : null;

        var model = FindModel(provider, route.ModelId)
            ?? throw new InvalidOperationException(
                $"model '{route.ModelId}' is not registered under provider '{route.ProviderId}' in {path}");

        if (model.TryGetProperty("isEmbedding", out var isEmbedding) && isEmbedding.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException(
                $"model '{route.ModelId}' is not flagged as an embedding model in {path}; refusing to use it as one");

        var maxContextTokens = model.TryGetProperty("maxContextTokens", out var context) && context.ValueKind == JsonValueKind.Number
            ? context.GetInt32()
            : 0;

        double? price = model.TryGetProperty("pricePer1MInputTokens", out var priceElement)
                        && priceElement.ValueKind == JsonValueKind.Number
            ? priceElement.GetDouble()
            : null;

        var dimensions = dimensionsOverride ?? DeclaredDimension(root, route);

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed))
            throw new InvalidOperationException($"provider '{route.ProviderId}' has an unusable baseUrl '{baseUrl}' in {path}");

        return new ResolvedEmbedding(
            route,
            baseUrl!,
            apiKey,
            dimensions,
            maxContextTokens,
            IsLoopback(parsed.Host),
            price,
            path);
    }

    private static EmbeddingRoute ResolveRoute(JsonElement root, string? routeOverride, string path)
    {
        if (!string.IsNullOrWhiteSpace(routeOverride))
            return EmbeddingRoute.Parse(routeOverride!);

        if (root.TryGetProperty("embedding", out var embedding)
            && embedding.ValueKind == JsonValueKind.Object
            && embedding.TryGetProperty("providerId", out var providerId)
            && embedding.TryGetProperty("modelId", out var modelId)
            && providerId.ValueKind == JsonValueKind.String
            && modelId.ValueKind == JsonValueKind.String)
        {
            return new EmbeddingRoute(providerId.GetString()!, modelId.GetString()!);
        }

        throw new InvalidOperationException(
            $"no embedding route given and {path} has no 'embedding' section; pass "
            + "--embedding-route provider/model (hard-coding a default here would defeat the point of "
            + "stating the route in configuration)");
    }

    private static int? DeclaredDimension(JsonElement root, EmbeddingRoute route)
    {
        if (!root.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Object)
            return null;

        var sameProvider = embedding.TryGetProperty("providerId", out var providerId)
                           && providerId.ValueKind == JsonValueKind.String
                           && string.Equals(providerId.GetString(), route.ProviderId, StringComparison.OrdinalIgnoreCase);

        var sameModel = embedding.TryGetProperty("modelId", out var modelId)
                        && modelId.ValueKind == JsonValueKind.String
                        && string.Equals(modelId.GetString(), route.ModelId, StringComparison.OrdinalIgnoreCase);

        if (!sameProvider || !sameModel)
            return null;

        return embedding.TryGetProperty("dimension", out var dimension) && dimension.ValueKind == JsonValueKind.Number
            ? dimension.GetInt32()
            : null;
    }

    private static JsonElement? FindProvider(JsonElement root, string providerId)
    {
        if (!root.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var provider in providers.EnumerateArray())
        {
            if (provider.TryGetProperty("providerId", out var id)
                && id.ValueKind == JsonValueKind.String
                && string.Equals(id.GetString(), providerId, StringComparison.OrdinalIgnoreCase))
            {
                return provider;
            }
        }

        return null;
    }

    private static JsonElement? FindModel(JsonElement provider, string modelId)
    {
        if (!provider.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var model in models.EnumerateArray())
        {
            if (model.TryGetProperty("modelId", out var id)
                && id.ValueKind == JsonValueKind.String
                && string.Equals(id.GetString(), modelId, StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }
        }

        return null;
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}
