using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Pudding.Api.Contracts;

public sealed record AgentRef(string WorkspaceId, string AgentId);

/// <summary>Missing, set and clear are distinct. Set(null) explicitly clears a nullable field.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public readonly record struct ConfigurationField<T>(bool IsSpecified, T? Value)
{
    public static ConfigurationField<T> Set(T? value) => new(true, value);
    public static implicit operator ConfigurationField<T>(T? value) => Set(value);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentConfigurationPatch
{
    public ConfigurationField<string> DisplayName { get; init; }
    public ConfigurationField<string> Description { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfigurationUpdateOptions(string ExpectedRevision, string OperationId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfigurationUpdateRequest(AgentConfigurationPatch Patch, ConfigurationUpdateOptions Options);

/// <summary>A projection, not a mutable live Agent or a complete manifest.</summary>
public sealed record AgentConfigurationSnapshot(
    AgentRef Agent, string Revision, string? DisplayName, string? Description,
    string TemplateId, DateTimeOffset ObservedAt);

/// <summary>Only the configuration source is applied in v1. This is not a UI acknowledgement or a Run reconfiguration.</summary>
public sealed record ConfigurationChangeReceipt(
    string OperationId, AgentRef Agent, string PreviousRevision, string PersistedRevision,
    string AppliedRevision, string AppliedTarget, bool Changed, long? ChangeSequence,
    DateTimeOffset CommittedAt);

public sealed record AgentConfigurationChanged(
    long Sequence, string EventId, string OperationId, AgentRef Agent,
    string PreviousRevision, string Revision, ImmutableArray<string> Fields, DateTimeOffset CommittedAt);

/// <summary>Complete means caught up at ObservedRevision, not an everlasting end of the stream.</summary>
public sealed record ConfigurationChangePage(
    ImmutableArray<AgentConfigurationChanged> Items, long NextCursor, bool Complete, string ObservedRevision);

public sealed record ConfigurationApiError(string Code, string Message, string EffectOutcome = "none");

public sealed class ConfigurationApiException(string code, string message, string effectOutcome = "none")
    : Exception(message)
{
    public string Code { get; } = code;
    public string EffectOutcome { get; } = effectOutcome;
}

/// <summary>Caller identity is bound by the trusted entry point, never supplied in these requests.</summary>
public interface IAgentConfigurationApi
{
    Task<AgentConfigurationSnapshot> GetAsync(AgentRef agent, CancellationToken ct = default);
    Task<ConfigurationChangeReceipt> UpdateAsync(AgentRef agent, AgentConfigurationPatch patch,
        ConfigurationUpdateOptions options, CancellationToken ct = default);
    Task<ConfigurationChangePage> ReadChangesAsync(AgentRef agent, long afterSequence = 0,
        int limit = 100, CancellationToken ct = default);
}
