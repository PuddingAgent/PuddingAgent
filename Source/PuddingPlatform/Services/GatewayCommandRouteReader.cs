using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// 读取网关入口命令（ChatExecutionCommands.MetadataJson）上保存的受信任
/// 飞书回信路由。只读适配器：命令写入仍归 acceptance/lease/journal 存储所有，
/// 与 <see cref="ExecutionCommandReader"/> 同层。
/// </summary>
public sealed class GatewayCommandRouteReader(
    IDbContextFactory<PlatformDbContext> dbFactory) : IGatewayCommandRouteReader
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<GatewayCommandRoute?> GetAsync(
        string commandId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ChatExecutionCommands
            .AsNoTracking()
            .FirstOrDefaultAsync(command => command.CommandId == commandId, ct);
        if (entity is null)
            return null;

        return Map(entity, DeserializeMetadata(entity.MetadataJson));
    }

    /// <summary>
    /// 主动发送场景（无 CommandId，如心跳/网页端）：按 Agent + 工作区查找最近一条
    /// 飞书网关入口命令的稳定回信路由，用于把主动消息投递到用户最近一次与该 Agent
    /// 对话的飞书单聊会话。找不到（该 Agent 从未收到过飞书消息）返回 null。
    /// </summary>
    public async Task<GatewayCommandRoute?> FindRecentFeishuRouteAsync(
        string agentInstanceId,
        string workspaceId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // MetadataJson 是 JSON 字符串，无法用 SQL WHERE 直接过滤 gateway 键；
        // 按 Agent + 工作区取最近若干条命令，在内存中反序列化后找第一条飞书 ingress。
        const int scanLimit = 64;
        var entities = await db.ChatExecutionCommands
            .AsNoTracking()
            .Where(command =>
                command.AgentInstanceId == agentInstanceId
                && command.WorkspaceId == workspaceId)
            .OrderByDescending(command => command.CreatedAt)
            .Take(scanLimit)
            .ToListAsync(ct);

        foreach (var entity in entities)
        {
            var metadata = DeserializeMetadata(entity.MetadataJson);
            if (!IsTrue(Get(metadata, MessageGatewayMetadata.IsGatewayIngress)))
                continue;
            if (!string.Equals(
                    Get(metadata, MessageGatewayMetadata.ChannelType),
                    "feishu",
                    StringComparison.OrdinalIgnoreCase))
                continue;
            return Map(entity, metadata);
        }

        return null;
    }

    private static GatewayCommandRoute Map(
        ChatExecutionCommandEntity entity,
        Dictionary<string, string> metadata)
        => new()
        {
            CommandId = entity.CommandId,
            WorkspaceId = entity.WorkspaceId,
            ConversationId = entity.SessionId,
            AgentInstanceId = entity.AgentInstanceId,
            TurnId = entity.TurnId,
            IsGatewayIngress = IsTrue(
                Get(metadata, MessageGatewayMetadata.IsGatewayIngress)),
            ChannelType = Get(metadata, MessageGatewayMetadata.ChannelType),
            ConnectorId = Get(metadata, MessageGatewayMetadata.ConnectorId),
            ExternalConversationId = Get(
                metadata,
                MessageGatewayMetadata.ExternalConversationId),
            ExternalMessageId = Get(
                metadata,
                MessageGatewayMetadata.ExternalMessageId),
            Metadata = metadata,
        };

    private static Dictionary<string, string> DeserializeMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                       json,
                       JsonOptions)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string? Get(
        IReadOnlyDictionary<string, string> metadata,
        string key)
        => metadata.TryGetValue(key, out var value)
           && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static bool IsTrue(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "1", StringComparison.Ordinal);
}
