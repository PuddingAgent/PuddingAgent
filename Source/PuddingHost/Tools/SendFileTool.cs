using System.Text.Json;
using HarnessAgent.Core.Connectors.Feishu;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
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
/// 把一个本地文件作为飞书 <c>msg_type=file</c> 回复发到当前可信路由。
///
/// <para>
/// 与 <see cref="SendImageTool"/> 的分工：图片只发 vision artifact，
/// 文件发磁盘上的任意允许路径——因为交付物（PDF、报表、归档）不是 artifact。
/// </para>
///
/// <para>
/// 三道闸门，缺一不可：
/// ① 执行身份与命令归属（主会话 Turn 或继承父命令的子代理）；
/// ② 路由来源必须是飞书发起的会话，且 connector / 会话 / 源消息齐全；
/// ③ 取材合法（<see cref="FeishuFileSourcePolicy"/>：允许根 + 凭据类拦截）。
/// 第三道是唯一能阻止「模型被注入后把私钥发出去」的关卡，不能因为「收件人是自己」而省掉。
/// </para>
/// </summary>
[Tool(
    id: "send_file",
    name: "Send file",
    description:
        "Send a local file from an allowed root as a file reply in the current Feishu turn. " +
        "Pass an absolute path; never provide a recipient. Files over 30 MB are delivered in parts " +
        "with a reassembly hint. " +
        "Credential-like paths (SSH keys, .env, cookies, .pfx) are refused.",
    category: ToolCategory.Messaging,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.None,
    SubAgentExposure = SubAgentExposure.Default,
    SortOrder = 21)]
public sealed class SendFileTool(
    IServiceScopeFactory scopeFactory,
    PuddingDataPaths dataPaths,
    ILogger<SendFileTool> logger)
    : PuddingToolBase<SendFileArgs>
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SendFileArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return ToolExecutionResult.Fail("path is required.");

        var policy = new FeishuFileSourcePolicy(
            FeishuFileRoots.Resolve(
                dataPaths.DataRoot,
                dataPaths.WorkspacesRoot,
                context.WorkspaceId,
                Environment.GetEnvironmentVariable(FeishuFileRoots.OverrideVariable)));
        var decision = policy.Evaluate(args.Path);
        if (!decision.Allowed)
            return ToolExecutionResult.Fail($"The file cannot be sent: {decision.Reason}.");

        var fullPath = decision.FullPath!;
        var info = new FileInfo(fullPath);

        var identity = context.ExecutionIdentity;
        if (identity is not
            {
                CommandId.Length: > 0,
                ToolCallId.Length: > 0,
                ConversationId.Length: > 0,
            })
        {
            return ToolExecutionResult.Fail(
                "send_file requires a valid execution identity.");
        }

        var isMainTurn = identity.Kind == RuntimeExecutionKind.ConversationTurn;
        var isDelegatedSubAgent = identity.Kind == RuntimeExecutionKind.SubAgent
            && identity.ParentRunId is { Length: > 0 };
        if (!isMainTurn && !isDelegatedSubAgent)
        {
            return ToolExecutionResult.Fail(
                "send_file is only available inside a main conversation turn "
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
                "send_file is only available for a Feishu-originated turn.");
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

        var messageId = await FeishuFileProjection.QueueAsync(
            messageSystem,
            $"{command.CommandId}:tool:{identity.ToolCallId}",
            command.WorkspaceId,
            command.AgentInstanceId,
            connectorId,
            command.SessionId,
            command.TurnId,
            externalMessageId,
            externalConversationId,
            fullPath,
            metadata,
            ct);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "[SendFileTool] File queued command={CommandId} toolCall={ToolCallId} file={FileName} bytes={Bytes} message={MessageId}",
            command.CommandId,
            identity.ToolCallId,
            info.Name,
            info.Length,
            messageId);
        return ToolExecutionResult.Ok(
            JsonSerializer.Serialize(
                new
                {
                    status = "queued",
                    messageId,
                    fileName = info.Name,
                    bytes = info.Length,
                },
                JsonOptions));
    }

    /// <summary>
    /// 校验命令归属的执行主体。主会话要求命令属于当前 AgentInstanceId；
    /// 子代理运行使用临时 sub-session 身份，但命令归属根 Agent，
    /// 因此按 ConfigurationAgentInstanceId 校验。
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

public sealed record SendFileArgs
{
    [ToolParam("Absolute path of the file to send. Must sit under an allowed root (workspace or {DataRoot}/output unless overridden).")]
    public string? Path { get; init; }
}
