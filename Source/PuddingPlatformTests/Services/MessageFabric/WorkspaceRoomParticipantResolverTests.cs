using PuddingCode.Models;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services.MessageFabric;

namespace PuddingPlatformTests.Services.MessageFabric;

[TestClass]
public sealed class WorkspaceRoomParticipantResolverTests
{
    [TestMethod]
    public void Resolve_Includes_User_And_EnabledAgents_As_FirstClassParticipants()
    {
        var participants = WorkspaceRoomParticipantResolver.Resolve(
            workspaceId: "default",
            roomId: "room-default",
            userId: "owner",
            userDisplayName: "Owner",
            agents: Agents());

        Assert.AreEqual(3, participants.Count);
        Assert.IsTrue(participants.Any(p => p.Kind == MessageEndpointKinds.User && p.EndpointId == "owner"));
        Assert.IsTrue(participants.Any(p => p.Kind == MessageEndpointKinds.Agent && p.EndpointId == "assistant"));
        Assert.IsTrue(participants.Any(p => p.Kind == MessageEndpointKinds.Agent && p.EndpointId == "consultant"));
        Assert.IsFalse(participants.Any(p => p.EndpointId == "frozen"));
    }

    [TestMethod]
    public void Resolve_Uses_DisplayName_And_Avatar_For_AgentParticipants()
    {
        var participant = WorkspaceRoomParticipantResolver.Resolve(
                workspaceId: "default",
                roomId: "room-default",
                userId: "owner",
                userDisplayName: null,
                agents: Agents())
            .Single(p => p.EndpointId == "consultant");

        Assert.AreEqual("顾问", participant.DisplayName);
        Assert.AreEqual("avatar-consultant", participant.AvatarUrl);
        Assert.IsTrue(participant.CanSend);
        Assert.IsTrue(participant.CanReceive);
        Assert.AreEqual("available", participant.Status);
    }

    [TestMethod]
    public void Resolve_ExcludesAgentsWithoutBoundMainSession()
    {
        var participants = WorkspaceRoomParticipantResolver.Resolve(
            workspaceId: "default",
            roomId: "room-default",
            userId: "owner",
            userDisplayName: null,
            agents:
            [
                Agent("unbound", "Unbound", mainSessionId: null),
                Agent("bound", "Bound", mainSessionId: "bound-main"),
            ]);

        Assert.IsFalse(participants.Any(p => p.EndpointId == "unbound"));
        Assert.IsTrue(participants.Any(p => p.EndpointId == "bound"));
    }

    private static List<WorkspaceAgentDto> Agents() =>
    [
        Agent("assistant", "默认助手", mainSessionId: "assistant-main"),
        Agent("consultant", "咨询专家", displayName: "顾问", avatarUrl: "avatar-consultant", mainSessionId: "consultant-main"),
        Agent("frozen", "冻结助手", isFrozen: true, mainSessionId: "frozen-main"),
        Agent("disabled", "禁用助手", isEnabled: false, mainSessionId: "disabled-main"),
    ];

    private static WorkspaceAgentDto Agent(
        string agentId,
        string name,
        string? displayName = null,
        string? avatarUrl = null,
        string? mainSessionId = null,
        bool isFrozen = false,
        bool isEnabled = true) => new(
            AgentId: agentId,
            Name: name,
            Description: null,
            DisplayName: displayName,
            AvatarId: null,
            AvatarUrl: avatarUrl,
            SourceTemplateId: "global:general-assistant",
            MainSessionId: mainSessionId,
            SystemPromptOverride: null,
            PreferredProviderId: null,
            PreferredModelId: null,
            IsEnabled: isEnabled,
            IsFrozen: isFrozen,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
}
