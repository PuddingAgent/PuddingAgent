using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PuddingAgent.Services;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingHost.Tests.Services;

public sealed class ConnectorRecipientDeliveryTests
{
    [Theory]
    [InlineData("open_id", true)]
    [InlineData("user_id", false)]
    public async Task ExplicitRecipient_PreservesTargetAndUuidAcrossRetry_AndRejectsUnknownType(
        string receiveIdType, bool valid)
    {
        var target = new MessageAddress { Kind = MessageEndpointKinds.Connector, Id = "feishu:channel", WorkspaceId = "default" };
        var item = new MessageInboxItem
        {
            MessageId = "message", DeliveryId = "delivery", WorkspaceId = "default",
            From = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "agent" },
            Target = target, Content = "hello", Status = MessageDeliveryStatuses.Queued,
            Metadata = new Dictionary<string, string>
            {
                [MessageGatewayMetadata.ChannelType] = "feishu",
                [MessageGatewayMetadata.ReceiveId] = "ou_selected",
                [MessageGatewayMetadata.ReceiveIdType] = receiveIdType,
                [MessageGatewayMetadata.IdempotencyKey] = "stable-uuid",
                [MessageGatewayMetadata.ExternalConversationId] = "oc_stale",
                [MessageGatewayMetadata.ExternalMessageId] = "om_stale",
                ["open_id"] = "ou_stale", ["chat_id"] = "oc_stale", ["message_id"] = "om_stale",
            },
        };
        var inbox = new Mock<IMessageInbox>();
        inbox.Setup(x => x.ClaimNextAsync(It.IsAny<MessageClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        var services = new ServiceCollection();
        services.AddSingleton(inbox.Object);
        using var provider = services.BuildServiceProvider();
        var calls = new List<ConnectorMessage>();
        var connector = new Mock<IPuddingConnector>();
        connector.SetupGet(x => x.Descriptor).Returns(new ConnectorDescriptor
        {
            ConnectorId = target.Id, ConnectorType = "feishu", Protocol = "test",
        });
        connector.Setup(x => x.StartAsync(It.IsAny<ConnectorContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connector.Setup(x => x.SendAsync(It.IsAny<ConnectorMessage>(), It.IsAny<CancellationToken>()))
            .Returns((ConnectorMessage message, CancellationToken _) =>
            {
                calls.Add(message);
                return calls.Count == 1 ? Task.FromException(new IOException("transient")) : Task.CompletedTask;
            });
        var host = new ConnectorHost((_, _) => Task.CompletedTask, NullLogger<ConnectorHost>.Instance);
        host.Register(connector.Object);
        await host.StartAsync(target.Id);
        var dispatcher = new ConnectorDeliveryDispatcher(Mock.Of<IInternalEventBus>(),
            provider.GetRequiredService<IServiceScopeFactory>(), host, NullLogger<ConnectorDeliveryDispatcher>.Instance);
        var evt = new InternalEvent
        {
            Type = "message.deliver",
            Payload = new MessageDeliverEventPayload
            {
                MessageId = item.MessageId, DeliveryId = item.DeliveryId, WorkspaceId = "default", RoomId = "default",
                From = item.From, Target = target, Content = item.Content,
            },
        };
        await dispatcher.HandleAsync(evt);
        inbox.Verify(x => x.RetryAsync("delivery", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        if (!valid)
        {
            Assert.Empty(calls);
            return;
        }
        await dispatcher.HandleAsync(evt);
        Assert.Equal(2, calls.Count);
        foreach (var message in calls)
        {
            Assert.Equal("ou_selected", message.Target);
            Assert.Equal("ou_selected", message.Metadata["open_id"]);
            Assert.Equal("stable-uuid", message.Metadata["uuid"]);
            Assert.False(message.Metadata.ContainsKey("message_id"));
            Assert.False(message.Metadata.ContainsKey("chat_id"));
        }
        inbox.Verify(x => x.AckAsync("delivery", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
