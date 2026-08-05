using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.MessageGateway;

namespace PuddingAgent.Tools;

/// <summary>
/// Sends one existing workspace Vision Artifact to the current trusted Feishu route.
/// The model never supplies connector or external message identifiers.
/// </summary>
[Tool(
    id: "send_image",
    name: "Send image",
    description:
        "Send a workspace Vision Artifact as an image reply in the current Feishu turn. " +
        "Pass an exact artifactId returned by generate_image/import_image or listed in the attached-image notice; never provide a recipient.",
    category: ToolCategory.Messaging,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.None,
    SubAgentExposure = SubAgentExposure.Default,
    SortOrder = 20)]
public sealed class SendImageTool(
    IServiceScopeFactory scopeFactory,
    VisionArtifactStorageService artifactStorage,
    ILogger<SendImageTool> logger)
    : PuddingToolBase<SendImageArgs>
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SendImageArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var artifactId = args.ArtifactId?.Trim();
        if (string.IsNullOrWhiteSpace(artifactId))
            return ToolExecutionResult.Fail("artifact_id is required.");
        if (await artifactStorage.ResolveLocalFileAsync(
                context.WorkspaceId,
                artifactId,
                ct) is null)
        {
            return ToolExecutionResult.Fail(
                "The image artifact was not found in the current workspace.");
        }

        var identity = context.ExecutionIdentity;
        if (identity is not
            {
                CommandId.Length: > 0,
                ToolCallId.Length: > 0,
                ConversationId.Length: > 0,
            })
        {
            return ToolExecutionResult.Fail(
                "send_image requires a valid execution identity.");
        }

        // 主会话 Turn 与子代理执行均可安全解析到 Feishu 命令路由：
        // 子代理身份由编排层继承父 CommandId/ConversationId/TurnId，
        // 命令行仍指向网关入口命令（含 Feishu 路由元数据）。
        var isMainTurn = identity.Kind == RuntimeExecutionKind.ConversationTurn;
        var isDelegatedSubAgent = identity.Kind == RuntimeExecutionKind.SubAgent
            && identity.ParentRunId is { Length: > 0 };
        if (!isMainTurn && !isDelegatedSubAgent)
        {
            return ToolExecutionResult.Fail(
                "send_image is only available inside a main conversation turn "
                + "or a delegated sub-agent run.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var messageSystem =
            scope.ServiceProvider.GetRequiredService<IMessageSystem>();
        var command = await db.ChatExecutionCommands.SingleOrDefaultAsync(
            item => item.CommandId == identity.CommandId,
            ct);
        if (command is null
            || !string.Equals(
                command.WorkspaceId,
                context.WorkspaceId,
                StringComparison.Ordinal)
            || !string.Equals(
                command.SessionId,
                identity.ConversationId,
                StringComparison.Ordinal)
            || !MatchesExecutionAgent(command, context, isMainTurn))
        {
            return ToolExecutionResult.Fail(
                "The current execution command could not be resolved safely.");
        }

        var metadata = DeserializeMetadata(command.MetadataJson);
        if (!IsTrue(Get(metadata, MessageGatewayMetadata.IsGatewayIngress))
            || !string.Equals(
                Get(metadata, MessageGatewayMetadata.ChannelType),
                "feishu",
                StringComparison.OrdinalIgnoreCase))
        {
            return ToolExecutionResult.Fail(
                "send_image is only available for a Feishu-originated turn.");
        }

        var connectorId = Get(metadata, MessageGatewayMetadata.ConnectorId);
        var externalConversationId = Get(
            metadata,
            MessageGatewayMetadata.ExternalConversationId);
        var externalMessageId = Get(
            metadata,
            MessageGatewayMetadata.ExternalMessageId);
        if (connectorId is null || externalConversationId is null)
        {
            return ToolExecutionResult.Fail(
                "The current Feishu reply route is incomplete.");
        }

        var messageId = await FeishuImageProjection.QueueAsync(
            messageSystem,
            $"{command.CommandId}:tool:{identity.ToolCallId}",
            command.WorkspaceId,
            command.AgentInstanceId,
            connectorId,
            command.SessionId,
            command.TurnId,
            externalMessageId,
            externalConversationId,
            artifactId,
            metadata,
            ct);
        metadata[MessageGatewayMetadata.ImageToolSuppressDirective] = "true";
        command.MetadataJson = JsonSerializer.Serialize(metadata, JsonOptions);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "[SendImageTool] Image queued command={CommandId} toolCall={ToolCallId} artifact={ArtifactId} message={MessageId}",
            command.CommandId,
            identity.ToolCallId,
            artifactId,
            messageId);
        return ToolExecutionResult.Ok(
            JsonSerializer.Serialize(
                new
                {
                    status = "queued",
                    messageId,
                    artifactId,
                    imageDirectiveSuppressed = true,
                },
                JsonOptions));
    }

    /// <summary>
    /// 校验命令归属的执行主体。主会话要求命令属于当前 AgentInstanceId；
    /// 子代理运行使用临时 sub-session 身份（AgentInstanceId），
    /// 但命令归属根 Agent，因此按 ConfigurationAgentInstanceId（根 Agent）校验。
    /// </summary>
    private static bool MatchesExecutionAgent(
        ChatExecutionCommandEntity command,
        ToolExecutionContext context,
        bool isMainTurn)
    {
        if (isMainTurn)
        {
            return string.Equals(
                command.AgentInstanceId,
                context.AgentInstanceId,
                StringComparison.Ordinal);
        }

        return context.ConfigurationAgentInstanceId is { Length: > 0 }
               && string.Equals(
                   command.AgentInstanceId,
                   context.ConfigurationAgentInstanceId,
                   StringComparison.Ordinal);
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

public sealed record SendImageArgs
{
    [ToolParam("Existing workspace Vision Artifact id returned by generate_image/import_image or listed in the attached-image notice.")]
    public string? ArtifactId { get; init; }
}
