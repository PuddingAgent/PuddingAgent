namespace PuddingCode.Abstractions;

/// <summary>
/// Records token usage events for billing and analytics.
/// </summary>
public interface ITokenUsageRecorder
{
    Task RecordAsync(string sessionId, string workspaceId, CancellationToken ct = default);
}
