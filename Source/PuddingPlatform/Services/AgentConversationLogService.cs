using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// Writes agent-readable conversation transcripts to both the platform materialized view
/// and the agent's private data directory.
/// </summary>
public sealed class AgentConversationLogService(
    IDbContextFactory<PlatformDbContext> dbFactory,
    PuddingDataPaths paths,
    ILogger<AgentConversationLogService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>持久化消息并返回生成的 messageId；重复消息返回 null。</summary>
    public async Task<long?> PersistMessageAsync(
        AgentConversationLogWriteRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId)
            || string.IsNullOrWhiteSpace(request.Role)
            || string.IsNullOrWhiteSpace(request.Content))
        {
            return null;
        }

        var messageId = await PersistDatabaseMessageAsync(request, ct);
        if (messageId is null)
            return null;

        if (string.IsNullOrWhiteSpace(request.AgentInstanceId))
        {
            logger.LogWarning(
                "[AgentConversationLog] Skip private file write because agent is missing session={Session} workspace={Workspace}",
                request.SessionId,
                request.WorkspaceId);
            return messageId;
        }

        await PersistPrivateFilesAsync(request, ct);
        return messageId;
    }

    private async Task<long?> PersistDatabaseMessageAsync(
        AgentConversationLogWriteRequest request,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var windowStart = request.CreatedAt - 2_000;
        var windowEnd = request.CreatedAt + 2_000;

        var exists = await db.ChatMessages
            .AsNoTracking()
            .AnyAsync(m => m.SessionId == request.SessionId
                && m.Role == request.Role
                && m.Content == request.Content
                && m.CreatedAt >= windowStart
                && m.CreatedAt <= windowEnd, ct);

        if (exists)
        {
            logger.LogDebug(
                "[AgentConversationLog] Skip duplicate transcript session={Session} role={Role} createdAt={CreatedAt}",
                request.SessionId,
                request.Role,
                request.CreatedAt);
            return null;
        }

        var entity = new ChatMessageEntity
        {
            WorkspaceId = request.WorkspaceId,
            AgentInstanceId = request.AgentInstanceId,
            AgentTemplateId = request.AgentTemplateId,
            SessionId = request.SessionId,
            Role = request.Role,
            Content = request.Content,
            ThinkingJson = request.ThinkingJson,
            UsageJson = request.UsageJson,
            CreatedAt = request.CreatedAt,
        };

        db.ChatMessages.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    private async Task PersistPrivateFilesAsync(
        AgentConversationLogWriteRequest request,
        CancellationToken ct)
    {
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(request.CreatedAt);
        var day = createdAt.UtcDateTime.ToString("yyyy-MM-dd");
        var dayRoot = paths.AgentInstanceMessageLogDayRoot(request.AgentInstanceId, day);
        Directory.CreateDirectory(dayRoot);

        var record = new AgentConversationLogRecord(
            request.WorkspaceId,
            request.AgentInstanceId,
            request.AgentTemplateId,
            request.SessionId,
            request.Role,
            request.Content,
            createdAt,
            $"agent-message-log:{day}:{request.SessionId}:{request.CreatedAt}");

        var jsonlPath = paths.AgentInstanceMessageLogJsonlFile(request.AgentInstanceId, day, request.SessionId);
        var jsonLine = JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine;
        await File.AppendAllTextAsync(jsonlPath, jsonLine, ct);

        var mdPath = paths.AgentInstanceMessageLogMarkdownFile(request.AgentInstanceId, day, request.SessionId);
        if (!File.Exists(mdPath))
        {
            await File.AppendAllTextAsync(
                mdPath,
                $"# Agent Message Log\n\n## {request.SessionId}\n\n",
                ct);
        }

        var markdown = $"[{request.Role} @ {createdAt:O}]\n\n{request.Content}\n\n";
        await File.AppendAllTextAsync(mdPath, markdown, ct);
    }
}

public sealed record AgentConversationLogWriteRequest(
    string WorkspaceId,
    string AgentInstanceId,
    string AgentTemplateId,
    string SessionId,
    string Role,
    string Content,
    long CreatedAt,
    string? ThinkingJson = null,
    string? UsageJson = null);

public sealed record AgentConversationLogRecord(
    string WorkspaceId,
    string AgentInstanceId,
    string AgentTemplateId,
    string SessionId,
    string Role,
    string Content,
    DateTimeOffset CreatedAt,
    string EvidenceRef);
