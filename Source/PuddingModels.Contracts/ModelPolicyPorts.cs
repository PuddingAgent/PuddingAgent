using System.Collections.Immutable;

namespace PuddingModels.Contracts;

// PA-06 端口（BCL-only）：有效配置来源、类型化 CAS 写入、模型能力目录与可信报价。
// 不返回 provider 密钥/endpoint；未知价格不得映射为零，只有 ExplicitFree 才允许零报价。

public sealed record AgentProfileScope(string WorkspaceId, string AgentId, string ActorId)
{
    public bool IsValid => Valid(WorkspaceId) && Valid(AgentId) && Valid(ActorId);
    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

public enum PolicySourceKind { Instance, Workspace, System }

public sealed record PurposeModelLayer(string? ModelId, bool InheritPrimary, PolicySourceKind Source, long SourceRevision);

public sealed record PurposeModelSetting(string Purpose, PurposeModelLayer? Instance, PurposeModelLayer? Workspace,
    PurposeModelLayer? System);

public sealed record AgentConfigurationSnapshot(long InstanceRevision, long PolicyGeneration,
    ImmutableArray<PurposeModelSetting> Purposes);

/// <summary>Missing/Set/Clear: a patch is absent from the array when unset, carries a ModelId to set, or Clear=true to remove an instance override.
/// InheritPrimary is the only way to explicitly follow the Primary model (a null ModelId without this flag is not an inheritance request).</summary>
public sealed record ModelPolicyPatch(string Purpose, string? ModelId, bool Clear, bool InheritPrimary = false);

public sealed record UpdateModelPolicyCommand(string OperationId, long ExpectedRevision,
    ImmutableArray<ModelPolicyPatch> Patches, string ApplyPolicy);

public sealed record ModelPolicyWriteResult(long PersistedRevision, long AppliedRevision, bool AppliesFromNextRun,
    ImmutableArray<string> PendingReasons, long PolicyGeneration);

/// <summary>Host-owned configuration authority. Writes are CAS on ExpectedRevision; the template is only a creation seed.</summary>
public interface IAgentModelPolicyStore
{
    ValueTask<AgentConfigurationSnapshot> ReadAsync(AgentProfileScope scope, CancellationToken ct = default);
    ValueTask<ModelPolicyWriteResult> WriteAsync(AgentProfileScope scope, UpdateModelPolicyCommand command,
        CancellationToken ct = default);
}

public sealed record ModelCapabilityRecord(string ModelId, string Revision, int MaxContextTokens, int MaxOutputTokens,
    ImmutableArray<string> InputModalities, bool SupportsTools, bool SupportsStructuredOutput, string Availability,
    decimal? InputPerMillion, decimal? CacheHitPerMillion, decimal? OutputPerMillion, string? Currency,
    string PriceSource, bool ExplicitFree, DateTimeOffset QuoteValidFrom, DateTimeOffset QuoteValidTo, string TimeZoneId,
    decimal MaxChargeable, string PricingRuleVersion);

public interface IModelCatalogSource
{
    ValueTask<ModelCapabilityRecord?> GetAsync(string modelId, CancellationToken ct = default);
    ValueTask<ImmutableArray<ModelCapabilityRecord>> ListAsync(string workspaceId, string? cursor, int maxCount,
        CancellationToken ct = default);
}

public sealed record ModelQuoteRequest(string ModelId, string Revision, DateTimeOffset DispatchAt, string AccountCurrency);

public sealed record ModelQuoteSnapshot(string QuoteId, string ModelId, string Revision, string Currency, string Unit,
    decimal? InputPerMillion, decimal? CacheHitPerMillion, decimal? OutputPerMillion, long BaseUnit,
    DateTimeOffset ValidFrom, DateTimeOffset ValidTo, string TimeZoneId, string PriceSource, bool ExplicitFree,
    decimal MaxChargeable, string PricingRuleVersion, DateTimeOffset FrozenAt)
{
    /// <summary>Prices are valid only for the actual dispatch instant; a queue that crossed the window must requote.</summary>
    public bool IsUsableAt(DateTimeOffset dispatchAt) => dispatchAt >= ValidFrom && dispatchAt < ValidTo;
}

/// <summary>Quotes are looked up at dispatch time and frozen for that attempt; a null result means "no trustworthy price".</summary>
public interface IModelQuoteSource
{
    ValueTask<ModelQuoteSnapshot?> QuoteAsync(ModelQuoteRequest request, CancellationToken ct = default);
}

public static class ModelErrorCodes
{
    public const string RequestInvalid = "model.request_invalid";
    public const string ScopeForbidden = "model.scope_forbidden";
    public const string ConfigRevisionConflict = "model.config_revision_conflict";
    public const string FieldUnknown = "model.config_field_unknown";
    public const string PolicyMissing = "model.policy_missing";
    public const string CapabilityMissing = "model.capability_missing";
    public const string Unavailable = "model.unavailable";
    public const string PriceUnknown = "model.price_unknown";
    public const string CurrencyMismatch = "model.currency_mismatch";
    public const string QuoteWindowMissed = "model.quote_window_missed";
    public const string QuoteCapExceeded = "model.quote_cap_exceeded";
    public const string UsageUnknown = "model.usage_unknown";
    public const string LeaseBusy = "model.lease_busy";
    public const string LeaseGenerationRevoked = "model.lease_generation_revoked";
    public const string LeaseInvalid = "model.lease_invalid";
    public const string LeaseReleaseRejected = "model.lease_release_rejected";
}

public sealed class ModelException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
