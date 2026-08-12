using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingPlatform.Data;

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

        var metadata = DeserializeMetadata(entity.MetadataJson);
        return new GatewayCommandRoute
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
    }

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
