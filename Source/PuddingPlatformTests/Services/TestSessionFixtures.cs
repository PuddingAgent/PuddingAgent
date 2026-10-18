using PuddingCode.Platform;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>会话身份解析测试共用的最小 fake（只实现查询路径；写方法 fail closed）。</summary>
internal sealed class FakeSessionRepository : ISessionRepository
{
    private readonly Dictionary<string, SessionRecord> _sessions = new(StringComparer.Ordinal);

    public SessionRecord? MainSession { get; set; }

    public (string WorkspaceId, string PrincipalKind, string PrincipalId)? LastMainLookup { get; private set; }

    public void Add(SessionRecord record) => _sessions[record.SessionId] = record;

    public Task<SessionRecord?> GetAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(_sessions.GetValueOrDefault(sessionId));

    public Task<SessionRecord?> FindMainAsync(
        string workspaceId,
        string principalKind,
        string principalId,
        CancellationToken ct = default)
    {
        LastMainLookup = (workspaceId, principalKind, principalId);
        return Task.FromResult(MainSession);
    }

    public Task<SessionRecord> CreateAsync(SessionRecord record, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<SessionRecord?> FindActiveAsync(
        string channelId,
        string ownerUserId,
        string workspaceId,
        string agentTemplateId,
        CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<SessionRecord> RebindMainAsync(
        string workspaceId,
        string principalKind,
        string principalId,
        string successorSessionId,
        CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<SessionRecord>> QueryAsync(
        string? channelId = null,
        string? userId = null,
        string? workspaceId = null,
        CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task UpdateAsync(SessionRecord record, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task DeleteAsync(string sessionId, CancellationToken ct = default)
        => throw new NotSupportedException();
}

internal sealed class FakeWorkspaceAgentCatalog(params WorkspaceAgentDto[] agents) : IWorkspaceAgentCatalog
{
    public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(
        string workspaceId,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkspaceAgentDto>>(agents);
}
