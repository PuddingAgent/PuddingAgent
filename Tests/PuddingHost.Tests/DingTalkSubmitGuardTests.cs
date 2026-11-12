using System.Text.Json;
using PuddingAgent.Services;
using PuddingCode.Platform;
using PuddingDingTalk;

namespace PuddingHost.Tests;

/// <summary>
/// <see cref="DingTalkSubmitGuard"/> 是钉钉群来源的完整性守卫。
/// </summary>
/// <remarks>
/// 回归背景：单一会话改造后，所有渠道共用同一个主会话，<c>ConversationId</c> 不再能用来判断
/// 「这是不是钉钉群回合」。守卫因此改为只看 provenance 元数据，但最初的写法要求
/// 「可信网关提交必须携带钉钉来源」，结果把飞书 / Slack / Web 等所有非钉钉渠道一并拒绝：
/// 消息已经入站，回合却抛 <see cref="UnauthorizedAccessException"/>，用户收不到任何回复。
/// 本套用例锁住「无钉钉来源即与本守卫无关」这条前提。
/// </remarks>
public sealed class DingTalkSubmitGuardTests
{
    [Fact]
    public void GatewayTurnWithoutDingTalkProvenance_IsAllowed() =>
        DingTalkSubmitGuard.Validate(Command(isTrustedGateway: true, provenance: null));

    [Fact]
    public void LocalTurnWithoutProvenance_IsAllowed() =>
        DingTalkSubmitGuard.Validate(Command(isTrustedGateway: false, provenance: null));

    [Fact]
    public void ProvenanceWithoutTrustedGateway_IsRejected()
    {
        var sender = Sender();
        var command = Command(isTrustedGateway: false, provenance: JsonSerializer.Serialize(sender), sender: sender);
        Assert.Throws<UnauthorizedAccessException>(() => DingTalkSubmitGuard.Validate(command));
    }

    [Fact]
    public void MatchingProvenance_IsAllowed()
    {
        var sender = Sender();
        DingTalkSubmitGuard.Validate(
            Command(isTrustedGateway: true, provenance: JsonSerializer.Serialize(sender), sender: sender));
    }

    [Fact]
    public void ProvenanceWithForeignActor_IsRejected()
    {
        var sender = Sender();
        var command = Command(isTrustedGateway: true, provenance: JsonSerializer.Serialize(sender), sender: sender)
            with { UserId = "someone-else" };
        Assert.Throws<UnauthorizedAccessException>(() => DingTalkSubmitGuard.Validate(command));
    }

    [Fact]
    public void ProvenanceWithForeignMessage_IsRejected()
    {
        var sender = Sender();
        var command = Command(isTrustedGateway: true, provenance: JsonSerializer.Serialize(sender), sender: sender)
            with { ClientMessageId = "another-message" };
        Assert.Throws<UnauthorizedAccessException>(() => DingTalkSubmitGuard.Validate(command));
    }

    [Fact]
    public void ProvenanceWithForeignRecipient_IsRejected()
    {
        var sender = Sender();
        var command = Command(isTrustedGateway: true, provenance: JsonSerializer.Serialize(sender), sender: sender)
            with { Recipients = new RecipientRequest { Type = "agent", AgentIds = ["another-agent"] } };
        Assert.Throws<UnauthorizedAccessException>(() => DingTalkSubmitGuard.Validate(command));
    }

    private static DingTalkConversationIngress.Sender Sender()
    {
        var at = DateTimeOffset.UnixEpoch;
        return new("robot", "agent-b", new GroupSource("corp", "room", "member", at, at), "message-1");
    }

    private static SubmitTurnCommand Command(
        bool isTrustedGateway, string? provenance, DingTalkConversationIngress.Sender? sender = null)
        => new(
            ConversationId: "conversation-1",
            WorkspaceId: "default",
            UserId: sender?.Actor ?? "owner",
            ClientRequestId: sender?.Input ?? "request-1",
            ClientMessageId: sender?.Input ?? "message-1",
            Recipients: new RecipientRequest { Type = "agent", AgentIds = ["agent-b"] },
            Content: [new ContentPart { Type = "text", Text = "hello" }],
            Metadata: provenance is null
                ? null
                : new Dictionary<string, string> { [DingTalkConversationIngress.Provenance] = provenance })
        {
            IsTrustedGatewayIngress = isTrustedGateway,
        };
}
