using System.Collections.Immutable;
using Pudding.Api.Contracts;

namespace PuddingAgentConfiguration.Contracts;

/// <summary>Created by trusted composition code. Do not deserialize a caller from a plugin or HTTP body.</summary>
public sealed record ConfigurationCaller(string ActorId);
public enum ConfigurationAccess { Read, Update, ReadChanges }

public interface IAgentConfigurationAuthorizer
{
    ValueTask<bool> IsAllowedAsync(ConfigurationCaller caller, AgentRef agent,
        ConfigurationAccess access, CancellationToken ct);
}

public sealed record RecordedConfigurationOperation(
    string ActorId, string OperationId, string RequestHash, ConfigurationChangeReceipt Receipt);

/// <summary>All members are immutable. Snapshot, dedup receipts and changes commit as one atomic unit.</summary>
public sealed record AgentConfigurationRecord(
    AgentConfigurationSnapshot Snapshot,
    ImmutableArray<RecordedConfigurationOperation> Operations,
    ImmutableArray<AgentConfigurationChanged> Changes)
{
    public static AgentConfigurationRecord Create(AgentConfigurationSnapshot snapshot) => new(snapshot, [], []);
}

public sealed record ConfigurationTransaction<T>(AgentConfigurationRecord State, T Result, bool Write);

/// <summary>
/// An adapter must serialize transactions against ALL other writers of the same Agent.
/// Callback executes once under that fence, with a fresh immutable state; throwing performs no write.
/// Persist State atomically before returning Result. Never retry a callback after committing it.
/// Cancellation before commit has no effect; uncertain persistence must report operation.unknown.
/// Missing Agents throw resource.not_found. Read never creates an Agent.
/// </summary>
public interface IAgentConfigurationStore
{
    Task<AgentConfigurationRecord> ReadAsync(AgentRef agent, CancellationToken ct);
    Task<T> TransactAsync<T>(AgentRef agent,
        Func<AgentConfigurationRecord, ConfigurationTransaction<T>> transaction, CancellationToken ct);
}
