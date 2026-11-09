using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Pudding.Api.Contracts;

// PA-06B 模型能力目录 DTO：只暴露受权模型身份、上限、能力、可用性与价格来源；
// 不返回 endpoint 密钥、API token 或可变 provider 对象。

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelCatalogEntry(string ModelId, string Revision, int MaxContextTokens, int MaxOutputTokens,
    ImmutableArray<string> InputModalities, bool SupportsTools, bool SupportsStructuredOutput, string Availability,
    string PriceSource, bool ExplicitFree, string? Currency, decimal? InputPerMillion, decimal? CacheHitPerMillion,
    decimal? OutputPerMillion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelQuery(string WorkspaceId, string? Cursor = null, int MaxCount = 50);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelCatalogPage(ImmutableArray<ModelCatalogEntry> Models, string? Cursor, bool Complete);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelSelectionRequest(AgentRef Agent, string Purpose, ImmutableArray<string> RequiredCapabilities);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelSelection(string ModelId, string Revision, string ReasonCode, bool InheritedFromPrimary);

public interface IModelCatalogApi
{
    Task<ModelCatalogPage> ListAsync(ModelQuery query, CancellationToken ct = default);
    Task<ModelSelection> ResolveAsync(ModelSelectionRequest request, CancellationToken ct = default);
}

public sealed class ModelCatalogException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
