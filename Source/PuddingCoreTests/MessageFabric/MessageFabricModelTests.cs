using PuddingCode.Models;

namespace PuddingCoreTests.MessageFabric;

[TestClass]
public sealed class MessageFabricModelTests
{
    [TestMethod]
    public void RoomParticipant_Allows_User_And_Agent_As_FirstClassParticipants()
    {
        var user = new RoomParticipant
        {
            ParticipantId = "p-user-owner",
            RoomId = "room-default",
            Kind = MessageEndpointKinds.User,
            EndpointId = "owner",
            DisplayName = "Owner",
        };
        var agent = new RoomParticipant
        {
            ParticipantId = "p-agent-assistant",
            RoomId = "room-default",
            Kind = MessageEndpointKinds.Agent,
            EndpointId = "agent.default",
            DisplayName = "Default Assistant",
        };

        Assert.AreEqual(MessageEndpointKinds.User, user.Kind);
        Assert.AreEqual(MessageEndpointKinds.Agent, agent.Kind);
        Assert.IsTrue(user.CanSend);
        Assert.IsTrue(agent.CanSend);
        Assert.IsTrue(user.CanReceive);
        Assert.IsTrue(agent.CanReceive);
    }

    [TestMethod]
    public void BroadcastRoute_Uses_OneRoomMessage_And_MultipleDeliveries()
    {
        var route = new MessageRoutePlan
        {
            MessageId = "m1",
            RoomMessage = new RoomMessageDraft
            {
                RoomId = "room-default",
                MessageId = "m1",
                From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner" },
                Audience = MessageAudiences.Broadcast,
                Visibility = MessageVisibilities.Public,
                Content = "hello all",
                CreatedAt = 100,
            },
            Deliveries =
            [
                new MessageDeliveryDraft
                {
                    DeliveryId = "d1",
                    MessageId = "m1",
                    Target = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "a1" },
                    Priority = 0,
                },
                new MessageDeliveryDraft
                {
                    DeliveryId = "d2",
                    MessageId = "m1",
                    Target = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "a2" },
                    Priority = 0,
                },
            ],
        };

        Assert.AreEqual("m1", route.RoomMessage.MessageId);
        Assert.AreEqual(2, route.Deliveries.Count);
    }

    [TestMethod]
    public void InboxItem_Represents_PullBased_Delivery_For_Agent()
    {
        var item = new MessageInboxItem
        {
            DeliveryId = "d1",
            MessageId = "m1",
            WorkspaceId = "default",
            RoomId = "room-default",
            From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner" },
            Target = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
            Content = "new task",
            Status = MessageDeliveryStatuses.Queued,
            Priority = 5,
            CreatedAt = 100,
        };

        Assert.AreEqual("assistant", item.Target.Id);
        Assert.AreEqual(MessageDeliveryStatuses.Queued, item.Status);
    }
}
