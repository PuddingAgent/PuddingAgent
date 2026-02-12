using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class ChatRoomRouteResolverTests
{
    [TestMethod]
    public void Resolve_WithoutMention_UsesSelectedAgent()
    {
        var route = ChatRoomRouteResolver.Resolve(
            new AdminChatRequest(
                MessageText: "分析日志",
                OriginalMessageText: null,
                SessionId: null,
                AgentId: "consultant"),
            Agents());

        Assert.AreEqual("分析日志", route.MessageText);
        Assert.AreEqual("分析日志", route.OriginalMessageText);
        Assert.AreEqual(ChatRoomRouteResolver.AudienceAgent, route.Audience);
        CollectionAssert.AreEqual(new[] { "consultant" }, route.TargetAgentIds.ToArray());
        Assert.AreEqual("consultant", route.PrimaryAgentId);
    }

    [TestMethod]
    public void Resolve_AtAgent_UsesMentionAndStripsMentionForRuntime()
    {
        var route = ChatRoomRouteResolver.Resolve(
            new AdminChatRequest(
                MessageText: "@顾问 帮我评审方案",
                OriginalMessageText: null,
                SessionId: null,
                AgentId: "assistant"),
            Agents());

        Assert.AreEqual("帮我评审方案", route.MessageText);
        Assert.AreEqual("@顾问 帮我评审方案", route.OriginalMessageText);
        Assert.AreEqual(ChatRoomRouteResolver.AudienceAgent, route.Audience);
        CollectionAssert.AreEqual(new[] { "consultant" }, route.TargetAgentIds.ToArray());
        Assert.AreEqual("consultant", route.PrimaryAgentId);
    }

    [TestMethod]
    public void Resolve_AtAll_UsesAllAvailableAgents()
    {
        var route = ChatRoomRouteResolver.Resolve(
            new AdminChatRequest(
                MessageText: "@all 给出各自建议",
                OriginalMessageText: null,
                SessionId: null,
                AgentId: "assistant"),
            Agents());

        Assert.AreEqual("给出各自建议", route.MessageText);
        Assert.AreEqual(ChatRoomRouteResolver.AudienceAll, route.Audience);
        CollectionAssert.AreEqual(new[] { "assistant", "consultant" }, route.TargetAgentIds.ToArray());
        Assert.AreEqual("assistant", route.PrimaryAgentId);
    }

    [TestMethod]
    public void Resolve_ExplicitTargets_AreAuthoritativeButFilteredToAvailableAgents()
    {
        var route = ChatRoomRouteResolver.Resolve(
            new AdminChatRequest(
                MessageText: "广播",
                OriginalMessageText: "@all 广播",
                SessionId: null,
                AgentId: "assistant",
                TargetAgentIds: ["consultant", "frozen"],
                Audience: "all"),
            Agents());

        Assert.AreEqual("广播", route.MessageText);
        Assert.AreEqual("@all 广播", route.OriginalMessageText);
        Assert.AreEqual(ChatRoomRouteResolver.AudienceAll, route.Audience);
        CollectionAssert.AreEqual(new[] { "consultant" }, route.TargetAgentIds.ToArray());
        Assert.AreEqual("consultant", route.PrimaryAgentId);
    }

    private static List<WorkspaceAgentDto> Agents() =>
    [
        Agent("assistant", "默认助手"),
        Agent("consultant", "咨询专家", displayName: "顾问"),
        Agent("frozen", "冻结助手", isFrozen: true),
    ];

    private static WorkspaceAgentDto Agent(
        string agentId,
        string name,
        string? displayName = null,
        bool isFrozen = false) => new(
            AgentId: agentId,
            Name: name,
            Description: null,
            DisplayName: displayName,
            AvatarId: null,
            AvatarUrl: null,
            SourceTemplateId: "global:general-assistant",
            MainSessionId: null,
            SystemPromptOverride: null,
            PreferredProviderId: null,
            PreferredModelId: null,
            IsEnabled: true,
            IsFrozen: isFrozen,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
}
