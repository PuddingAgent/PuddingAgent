using PuddingCode.Models;
using PuddingPlatform.Services.MessageFabric;

namespace PuddingPlatformTests.Services.MessageFabric;

[TestClass]
public sealed class MessageRouterTests
{
    [TestMethod]
    public async Task RouteAsync_DirectToAgent_CreatesOneDelivery()
    {
        var router = new MessageRouter();
        var plan = await router.RouteAsync(new MessageEnvelope
        {
            MessageId = "m1",
            RoomId = "room-default",
            From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner", WorkspaceId = "default" },
            To = [new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" }],
            Audience = MessageAudiences.Direct,
            Visibility = MessageVisibilities.Public,
            Content = "hello",
            Priority = 5,
            CreatedAt = 100,
        }, Participants());

        Assert.AreEqual("m1", plan.MessageId);
        Assert.AreEqual("room-default", plan.RoomMessage.RoomId);
        Assert.AreEqual("hello", plan.RoomMessage.Content);
        Assert.AreEqual(MessageAudiences.Direct, plan.RoomMessage.Audience);
        Assert.AreEqual(1, plan.Deliveries.Count);
        Assert.AreEqual("assistant", plan.Deliveries[0].Target.Id);
        Assert.AreEqual(MessageEndpointKinds.Agent, plan.Deliveries[0].Target.Kind);
        Assert.AreEqual(5, plan.Deliveries[0].Priority);
    }

    [TestMethod]
    public async Task RouteAsync_DirectToAgentShortAddress_ResolvesCanonicalAgentId()
    {
        var router = new MessageRouter();
        var plan = await router.RouteAsync(new MessageEnvelope
        {
            MessageId = "m-short",
            RoomId = "room-default",
            From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner", WorkspaceId = "default" },
            To = [new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "general-assistant.40a" }],
            Audience = MessageAudiences.Direct,
            Visibility = MessageVisibilities.Public,
            Content = "hello",
        }, Participants());

        Assert.AreEqual(1, plan.Deliveries.Count);
        Assert.AreEqual("default.global_general-assistant.40a", plan.Deliveries[0].Target.Id);
    }

    [TestMethod]
    public async Task RouteAsync_DirectToMissingAgent_ThrowsClearError()
    {
        var router = new MessageRouter();

        InvalidOperationException? ex = null;
        try
        {
            await router.RouteAsync(new MessageEnvelope
            {
                MessageId = "m-missing",
                RoomId = "room-default",
                From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner", WorkspaceId = "default" },
                To = [new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "missing-agent" }],
                Audience = MessageAudiences.Direct,
                Visibility = MessageVisibilities.Public,
                Content = "hello",
            }, Participants());
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }

        Assert.IsNotNull(ex);
        StringAssert.Contains(ex!.Message, "missing-agent");
        StringAssert.Contains(ex.Message, "cannot receive messages");
    }

    [TestMethod]
    public async Task RouteAsync_BroadcastToRoom_CreatesDeliveriesForReceivableAgentsOnly()
    {
        var router = new MessageRouter();
        var plan = await router.RouteAsync(new MessageEnvelope
        {
            MessageId = "m2",
            RoomId = "room-default",
            From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner", WorkspaceId = "default" },
            To = [new MessageAddress { Kind = MessageEndpointKinds.Room, Id = "room-default" }],
            Audience = MessageAudiences.Broadcast,
            Visibility = MessageVisibilities.Public,
            Content = "hello all",
        }, Participants());

        CollectionAssert.AreEqual(
            new[] { "assistant", "consultant", "default.global_general-assistant.40a" },
            plan.Deliveries.Select(d => d.Target.Id).ToArray());
    }

    [TestMethod]
    public async Task RouteAsync_BroadcastWithNoReceivableAgents_ThrowsClearError()
    {
        var router = new MessageRouter();

        InvalidOperationException? ex = null;
        try
        {
            await router.RouteAsync(new MessageEnvelope
            {
                MessageId = "m-no-targets",
                RoomId = "room-default",
                From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner", WorkspaceId = "default" },
                To = [new MessageAddress { Kind = MessageEndpointKinds.Room, Id = "room-default" }],
                Audience = MessageAudiences.Broadcast,
                Visibility = MessageVisibilities.Public,
                Content = "hello all",
            }, Participants().Where(p => p.Kind != MessageEndpointKinds.Agent || !p.CanReceive).ToList());
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }

        Assert.IsNotNull(ex);
        StringAssert.Contains(ex!.Message, "receive broadcast messages");
    }

    [TestMethod]
    public async Task RouteAsync_DirectToUser_AllowsAgentToUserDelivery()
    {
        var router = new MessageRouter();
        var plan = await router.RouteAsync(new MessageEnvelope
        {
            MessageId = "m3",
            RoomId = "room-default",
            From = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant", WorkspaceId = "default" },
            To = [new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner" }],
            Audience = MessageAudiences.Direct,
            Visibility = MessageVisibilities.Private,
            Content = "需要确认",
        }, Participants());

        Assert.AreEqual(1, plan.Deliveries.Count);
        Assert.AreEqual(MessageEndpointKinds.User, plan.Deliveries[0].Target.Kind);
        Assert.AreEqual("owner", plan.Deliveries[0].Target.Id);
        Assert.AreEqual(MessageVisibilities.Private, plan.RoomMessage.Visibility);
    }

    [TestMethod]
    public async Task RouteAsync_DeduplicatesDirectTargets()
    {
        var router = new MessageRouter();
        var plan = await router.RouteAsync(new MessageEnvelope
        {
            MessageId = "m4",
            RoomId = "room-default",
            From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner", WorkspaceId = "default" },
            To =
            [
                new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
                new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
            ],
            Audience = MessageAudiences.Direct,
            Visibility = MessageVisibilities.Public,
            Content = "hello once",
        }, Participants());

        Assert.AreEqual(1, plan.Deliveries.Count);
    }

    private static IReadOnlyList<RoomParticipant> Participants() =>
    [
        new()
        {
            ParticipantId = "p-user",
            RoomId = "room-default",
            Kind = MessageEndpointKinds.User,
            EndpointId = "owner",
        },
        new()
        {
            ParticipantId = "p-agent-1",
            RoomId = "room-default",
            Kind = MessageEndpointKinds.Agent,
            EndpointId = "assistant",
            DisplayName = "默认助手",
        },
        new()
        {
            ParticipantId = "p-agent-2",
            RoomId = "room-default",
            Kind = MessageEndpointKinds.Agent,
            EndpointId = "consultant",
            DisplayName = "咨询专家",
        },
        new()
        {
            ParticipantId = "p-agent-3",
            RoomId = "room-default",
            Kind = MessageEndpointKinds.Agent,
            EndpointId = "default.global_general-assistant.40a",
            DisplayName = "开发助手",
        },
        new()
        {
            ParticipantId = "p-agent-off",
            RoomId = "room-default",
            Kind = MessageEndpointKinds.Agent,
            EndpointId = "sleeping",
            CanReceive = false,
        },
        new()
        {
            ParticipantId = "p-agent-disabled",
            RoomId = "room-default",
            Kind = MessageEndpointKinds.Agent,
            EndpointId = "disabled",
            Status = "disabled",
        },
    ];
}
