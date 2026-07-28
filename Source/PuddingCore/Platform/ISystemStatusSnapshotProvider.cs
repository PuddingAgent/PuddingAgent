using PuddingCode.Abstractions;
using PuddingCode.Runtime;

namespace PuddingCode.Platform;

/// <summary>
/// Builds one channel-neutral status snapshot for a Pudding conversation.
/// System-command transports must consume this boundary instead of assembling
/// model, context, or runtime state independently.
/// </summary>
public interface ISystemStatusSnapshotProvider
{
    Task<SystemStatusSnapshot> GetAsync(
        SystemStatusSnapshotRequest request,
        CancellationToken ct = default);
}

public sealed record SystemStatusSnapshotRequest(
    string WorkspaceId,
    string ConversationId,
    string AgentId);

public sealed record SystemStatusSnapshot(
    string WorkspaceId,
    string ConversationId,
    string AgentId,
    string AgentDisplayName,
    string? SourceTemplateId,
    SessionState SessionState,
    int RunningSubAgents,
    RuntimeExecutionMode RuntimeMode,
    int ActiveRuntimeSessions,
    int SessionWindowErrorCount,
    string? SessionFaultSummary,
    string? ProviderId,
    string? ModelId,
    int CapabilityCount,
    ContextHealthSnapshot? ContextHealth,
    IReadOnlyList<string> Warnings);
