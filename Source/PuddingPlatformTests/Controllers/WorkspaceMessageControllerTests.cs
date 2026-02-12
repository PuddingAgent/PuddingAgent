using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingPlatform.Controllers.Api;

namespace PuddingPlatformTests.Controllers;

[TestClass]
public sealed class WorkspaceMessageControllerTests
{
    [TestMethod]
    public async Task Send_DirectAgentMessage_EnqueuesThroughMessageSystem()
    {
        var messageSystem = new RecordingMessageSystem();
        var controller = CreateController(messageSystem);

        var result = await controller.Send("default", new WorkspaceMessageSendRequest
        {
            Content = "Please inspect the queue.",
            RoomId = "room-default",
            ConversationId = "session-1",
            TargetAgentIds = ["assistant"],
            Priority = 10,
            Intent = "chat",
            Metadata = new Dictionary<string, string>
            {
                ["client_message_id"] = "client-1",
            },
        }, CancellationToken.None);

        var ok = AssertIsOk(result);
        var response = (MessageSendResult)ok.Value!;
        Assert.AreEqual("message-1", response.MessageId);
        Assert.IsNotNull(messageSystem.LastEnvelope);
        Assert.AreEqual(MessageEndpointKinds.User, messageSystem.LastEnvelope!.From.Kind);
        Assert.AreEqual("user-1", messageSystem.LastEnvelope.From.Id);
        Assert.AreEqual(MessageAudiences.Direct, messageSystem.LastEnvelope.Audience);
        Assert.AreEqual("assistant", messageSystem.LastEnvelope.To.Single().Id);
        Assert.AreEqual(10, messageSystem.LastEnvelope.Priority);
        Assert.AreEqual("chat", messageSystem.LastEnvelope.Metadata["intent"]);
        Assert.AreEqual("workspace_message_api", messageSystem.LastEnvelope.Metadata["source"]);
        Assert.AreEqual("client-1", messageSystem.LastEnvelope.Metadata["client_message_id"]);
    }

    [TestMethod]
    public async Task Send_BroadcastMessage_TargetsRoom()
    {
        var messageSystem = new RecordingMessageSystem();
        var controller = CreateController(messageSystem);

        await controller.Send("default", new WorkspaceMessageSendRequest
        {
            Content = "Broadcast to all agents.",
            RoomId = "room-default",
            Audience = MessageAudiences.Broadcast,
            Priority = 5,
        }, CancellationToken.None);

        Assert.IsNotNull(messageSystem.LastEnvelope);
        Assert.AreEqual(MessageAudiences.Broadcast, messageSystem.LastEnvelope!.Audience);
        Assert.AreEqual(MessageEndpointKinds.Room, messageSystem.LastEnvelope.To.Single().Kind);
        Assert.AreEqual("room-default", messageSystem.LastEnvelope.To.Single().Id);
        Assert.AreEqual(5, messageSystem.LastEnvelope.Priority);
    }

    [TestMethod]
    public async Task Send_DirectWithoutTarget_ReturnsBadRequest()
    {
        var controller = CreateController(new RecordingMessageSystem());

        var result = await controller.Send("default", new WorkspaceMessageSendRequest
        {
            Content = "No target.",
            Audience = MessageAudiences.Direct,
        }, CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    private static WorkspaceMessageController CreateController(RecordingMessageSystem messageSystem)
    {
        var controller = new WorkspaceMessageController(messageSystem);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "user-1"),
                    new Claim(ClaimTypes.Name, "Owner"),
                ], "test")),
            },
        };

        return controller;
    }

    private static OkObjectResult AssertIsOk(ActionResult<MessageSendResult> result)
    {
        Assert.IsInstanceOfType<OkObjectResult>(result.Result);
        return (OkObjectResult)result.Result!;
    }

    private sealed class RecordingMessageSystem : IMessageSystem
    {
        public MessageEnvelope? LastEnvelope { get; private set; }

        public Task<MessageSendResult> SendAsync(MessageEnvelope envelope, CancellationToken ct = default)
        {
            LastEnvelope = envelope;
            return Task.FromResult(new MessageSendResult
            {
                MessageId = "message-1",
                RoomId = envelope.RoomId,
                DeliveryIds = ["delivery-1"],
            });
        }
    }
}
