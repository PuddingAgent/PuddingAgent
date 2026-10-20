using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "list_feishu_recipients",
    name: "查询飞书收件人",
    description: "列出当前 Agent 绑定的飞书渠道及管理员配置的白名单收件人，无需用户先发消息。将返回的 address 和 channelId 传给 send_message 的 to 和 channel_id；只有一位候选时可用 user:owner 作为默认收件人。只返回配置收件人，不代表飞书已送达或权限检查成功。",
    category: ToolCategory.Messaging,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly)]
public sealed class ListFeishuRecipientsTool(IServiceScopeFactory scopeFactory)
    : PuddingToolBase<ListFeishuRecipientsArgs>
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ListFeishuRecipientsArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var directory = scope.ServiceProvider.GetService<IFeishuRecipientDirectory>();
        if (directory is null)
            return ToolExecutionResult.Fail("飞书收件人目录未配置。");

        var recipients = (await directory.ListAsync(context.WorkspaceId,
                context.ConfigurationAgentInstanceId ?? context.AgentInstanceId, ct))
            .Where(item => string.IsNullOrWhiteSpace(args.ChannelId)
                || item.ChannelId == args.ChannelId.Trim()).ToList();
        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
        {
            status = "ok",
            count = recipients.Count,
            recipients,
            defaultRecipient = recipients.Count == 1 ? recipients[0] : null,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}

public sealed record ListFeishuRecipientsArgs
{
    [ToolParam("Optional channel id to filter the current Agent's configured Feishu recipients.")]
    public string? ChannelId { get; init; }
}
