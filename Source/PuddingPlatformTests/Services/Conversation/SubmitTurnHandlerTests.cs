using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Conversation;

namespace PuddingPlatformTests.Services.Conversation;

[TestClass]
public sealed class SubmitTurnHandlerTests
{
    [TestMethod]
    public async Task HandleAsync_UntrustedCommand_StripsReservedGatewayMetadata()
    {
        var store = new RecordingAcceptanceStore();
        var handler = new SubmitTurnHandler(
            store,
            new NullVisualArtifactResolver(),
            NullLogger<SubmitTurnHandler>.Instance);

        await handler.HandleAsync(Command() with
        {
            Metadata = new Dictionary<string, string>
            {
                ["client_fact"] = "kept",
                [MessageGatewayMetadata.IsGatewayIngress] = "true",
                [MessageGatewayMetadata.ConnectorId] = "feishu:forged",
                [MessageFabricTurnMetadata.IsIngress] = "true",
                [MessageFabricTurnMetadata.FromId] = "agent:forged",
            },
        }, CancellationToken.None);

        Assert.IsNotNull(store.LastRequest);
        Assert.AreEqual("kept", store.LastRequest!.Metadata!["client_fact"]);
        Assert.IsFalse(
            store.LastRequest.Metadata.ContainsKey(
                MessageGatewayMetadata.IsGatewayIngress));
        Assert.IsFalse(
            store.LastRequest.Metadata.ContainsKey(
                MessageGatewayMetadata.ConnectorId));
        Assert.IsFalse(
            store.LastRequest.Metadata.ContainsKey(
                MessageFabricTurnMetadata.IsIngress));
        Assert.IsFalse(
            store.LastRequest.Metadata.ContainsKey(
                MessageFabricTurnMetadata.FromId));
    }

    [TestMethod]
    public async Task HandleAsync_TrustedMessageFabricCommand_PreservesReservedMetadata()
    {
        var store = new RecordingAcceptanceStore();
        var handler = new SubmitTurnHandler(
            store,
            new NullVisualArtifactResolver(),
            NullLogger<SubmitTurnHandler>.Instance);
        var command = Command() with
        {
            Metadata = new Dictionary<string, string>
            {
                [MessageFabricTurnMetadata.IsIngress] = "true",
                [MessageFabricTurnMetadata.FromId] = "agent-a",
            },
            IsTrustedMessageFabricIngress = true,
        };

        await handler.HandleAsync(command, CancellationToken.None);

        Assert.AreEqual(
            "true",
            store.LastRequest!.Metadata![MessageFabricTurnMetadata.IsIngress]);
        Assert.AreEqual(
            "agent-a",
            store.LastRequest.Metadata[MessageFabricTurnMetadata.FromId]);
    }

    [TestMethod]
    public async Task HandleAsync_TrustedGatewayCommand_PreservesReservedMetadata()
    {
        var store = new RecordingAcceptanceStore();
        var handler = new SubmitTurnHandler(
            store,
            new NullVisualArtifactResolver(),
            NullLogger<SubmitTurnHandler>.Instance);
        var command = Command() with
        {
            Metadata = new Dictionary<string, string>
            {
                [MessageGatewayMetadata.IsGatewayIngress] = "true",
                [MessageGatewayMetadata.ConnectorId] = "feishu:agent-b",
            },
            IsTrustedGatewayIngress = true,
        };

        await handler.HandleAsync(command, CancellationToken.None);

        Assert.AreEqual(
            "true",
            store.LastRequest!.Metadata![
                MessageGatewayMetadata.IsGatewayIngress]);
        Assert.AreEqual(
            "feishu:agent-b",
            store.LastRequest.Metadata[
                MessageGatewayMetadata.ConnectorId]);
    }

    private static SubmitTurnCommand Command() =>
        new(
            ConversationId: "conversation-1",
            WorkspaceId: "default",
            UserId: "owner",
            ClientRequestId: "request-1",
            ClientMessageId: "message-1",
            Recipients: new RecipientRequest
            {
                Type = "agent",
                AgentIds = ["agent-b"],
            },
            Content:
            [
                new ContentPart
                {
                    Type = "text",
                    Text = "hello",
                },
            ],
            Metadata: null);

    private sealed class NullVisualArtifactResolver : IVisualArtifactLocalFileResolver
    {
        public Task<VisualArtifactLocalFile?> ResolveLocalFileAsync(
            string workspaceId,
            string artifactId,
            CancellationToken ct = default)
            => Task.FromResult<VisualArtifactLocalFile?>(null);
    }

    private sealed class RecordingAcceptanceStore
        : IConversationAcceptanceStore
    {
        public SubmitTurnRequest? LastRequest { get; private set; }

        public Task<AcceptanceResult> AcceptBatchAsync(
            SubmitTurnRequest request,
            string workspaceId,
            string conversationId,
            string? userId,
            CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new AcceptanceResult
            {
                ConversationId = conversationId,
                MessageId = request.ClientMessageId,
                TurnIds = ["turn-1"],
                CommandIds = ["command-1"],
                AcceptedSequence = 1,
            });
        }
    }
}
