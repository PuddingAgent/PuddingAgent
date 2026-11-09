using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Pudding.Api.Contracts;

// PA-02A 公开目录/工厂 DTO。Workspace/Actor/父任务由宿主绑定；请求字符串不能扩大范围。
// Get/List 返回借用句柄语义：查询不创建、不启动推理、不隐式付费。

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentDirectoryEntry(string WorkspaceId, string AgentId, string DisplayName, string TemplateId,
    int TemplateVersion, long Revision, string State, DateTimeOffset UpdatedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentListQuery(string WorkspaceId, int MaxCount = 50, string? Cursor = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentDirectoryPage(ImmutableArray<AgentDirectoryEntry> Agents, string? Cursor, bool Complete);

public interface IAgentDirectoryApi
{
    Task<AgentDirectoryPage> ListAsync(AgentListQuery query, CancellationToken ct = default);
    Task<AgentDirectoryEntry?> GetAsync(AgentRef agent, CancellationToken ct = default);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateAgentRequest(string OperationId, string WorkspaceId, string TemplateId,
    int TemplateVersion, string DisplayName, ImmutableArray<string>? Capabilities = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentInstanceReceipt(string OperationId, string WorkspaceId, string AgentId, long Revision,
    DateTimeOffset CreatedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveAgentRequest(string OperationId, AgentRef Agent, long ExpectedRevision);

public interface IAgentFactoryApi
{
    Task<AgentInstanceReceipt> CreateAsync(CreateAgentRequest request, CancellationToken ct = default);
    Task<AgentInstanceReceipt> ArchiveAsync(ArchiveAgentRequest request, CancellationToken ct = default);
}

public sealed class AgentDirectoryException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
