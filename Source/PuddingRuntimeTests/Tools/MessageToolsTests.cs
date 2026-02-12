using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class MessageToolsTests
{
    [TestMethod]
    public async Task SendMessageTool_Sends_Direct_Agent_Message_Through_Fabric()
    {
        var fabric = new RecordingMessageSystem();
        var tool = new SendMessageTool(CreateScopeFactory(fabric));

        var result = await ExecuteAsync(tool, new Dictionary<string, string>
        {
            ["to"] = "user:owner",
            ["content"] = "你好",
            ["priority"] = "5",
            ["room_id"] = "room-default",
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, fabric.Sent.Count);
        var envelope = fabric.Sent[0];
        Assert.AreEqual(MessageEndpointKinds.Agent, envelope.From.Kind);
        Assert.AreEqual("agent-a", envelope.From.Id);
        Assert.AreEqual("default", envelope.From.WorkspaceId);
        Assert.AreEqual("session-1", envelope.ConversationId);
        Assert.AreEqual("room-default", envelope.RoomId);
        Assert.AreEqual(MessageAudiences.Direct, envelope.Audience);
        Assert.AreEqual(MessageVisibilities.Public, envelope.Visibility);
        Assert.AreEqual("你好", envelope.Content);
        Assert.AreEqual(5, envelope.Priority);
        Assert.AreEqual("inform", envelope.Metadata["intent"]);
        Assert.AreEqual(MessageEndpointKinds.User, envelope.To[0].Kind);
        Assert.AreEqual("owner", envelope.To[0].Id);
        StringAssert.Contains(result.Output, "m-recorded");
    }

    [TestMethod]
    public async Task SendMessageTool_Allows_PrivateVisibilityOverride()
    {
        var fabric = new RecordingMessageSystem();
        var tool = new SendMessageTool(CreateScopeFactory(fabric));

        var result = await ExecuteAsync(tool, new Dictionary<string, string>
        {
            ["to"] = "agent:agent-b",
            ["content"] = "private diagnostic",
            ["visibility"] = MessageVisibilities.Private,
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(MessageVisibilities.Private, fabric.Sent[0].Visibility);
    }

    [TestMethod]
    public async Task ListAgentsTool_ReturnsRosterFromProvider()
    {
        var provider = new RecordingAgentRosterProvider([
            new AgentRosterItem(
                "agent-b",
                "Audit Agent",
                "agent:agent-b",
                "idle",
                true,
                ["review"],
                null),
        ]);
        var tool = new ListAgentsTool(provider);

        var result = await ExecuteAsync(tool, new Dictionary<string, string>
        {
            ["room_id"] = "room-default",
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("default", provider.LastWorkspaceId);
        Assert.AreEqual("room-default", provider.LastRoomId);
        Assert.IsTrue(provider.LastIncludeBusy);
        Assert.IsFalse(provider.LastIncludeFrozen);
        StringAssert.Contains(result.Output, "agent:agent-b");
        StringAssert.Contains(result.Output, "Audit Agent");
    }

    [TestMethod]
    public async Task ReceiveMessagesTool_Queries_Current_Agent_Inbox()
    {
        var inbox = new RecordingMessageInbox([
            new MessageInboxItem
            {
                DeliveryId = "d1",
                MessageId = "m1",
                WorkspaceId = "default",
                RoomId = "room-default",
                From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner" },
                Target = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "agent-a" },
                Content = "hello agent",
                Status = MessageDeliveryStatuses.Queued,
                Priority = 1,
                CreatedAt = 100,
            },
        ]);
        var tool = new ReceiveMessagesTool(inbox);

        var result = await ExecuteAsync(tool, new Dictionary<string, string>
        {
            ["room_id"] = "room-default",
            ["limit"] = "5",
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(inbox.LastQuery);
        Assert.AreEqual(MessageEndpointKinds.Agent, inbox.LastQuery.Endpoint.Kind);
        Assert.AreEqual("agent-a", inbox.LastQuery.Endpoint.Id);
        Assert.AreEqual("default", inbox.LastQuery.WorkspaceId);
        Assert.AreEqual("room-default", inbox.LastQuery.RoomId);
        Assert.AreEqual(5, inbox.LastQuery.Limit);
        Assert.IsFalse(inbox.LastQuery.IncludeDelivered);
        StringAssert.Contains(result.Output, "hello agent");
    }

    [TestMethod]
    public async Task ReceiveMessagesTool_Acks_Returned_Deliveries_When_Requested()
    {
        var inbox = new RecordingMessageInbox([
            new MessageInboxItem
            {
                DeliveryId = "d1",
                MessageId = "m1",
                WorkspaceId = "default",
                From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner" },
                Target = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "agent-a" },
                Content = "ack me",
                Status = MessageDeliveryStatuses.Queued,
                Priority = 1,
                CreatedAt = 100,
            },
        ]);
        var tool = new ReceiveMessagesTool(inbox);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["ack"] = true,
        });

        Assert.IsTrue(result.Success, result.Error);
        CollectionAssert.AreEqual(new[] { "d1" }, inbox.Acked.ToArray());
    }

    private sealed class RecordingMessageSystem : IMessageSystem
    {
        public List<MessageEnvelope> Sent { get; } = [];

        public Task<MessageSendResult> SendAsync(MessageEnvelope envelope, CancellationToken ct = default)
        {
            Sent.Add(envelope);
            return Task.FromResult(new MessageSendResult
            {
                MessageId = "m-recorded",
                RoomId = envelope.RoomId,
                DeliveryIds = ["d-recorded"],
            });
        }
    }

    private static IServiceScopeFactory CreateScopeFactory(IMessageSystem messageSystem)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(messageSystem);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static Task<ToolExecutionResult> ExecuteAsync(
        IPuddingTool tool,
        IReadOnlyDictionary<string, string> parameters) =>
        ExecuteAsync(tool, parameters.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value));

    private static Task<ToolExecutionResult> ExecuteAsync(
        IPuddingTool tool,
        IReadOnlyDictionary<string, object?> parameters) =>
        tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = JsonSerializer.Serialize(parameters),
            Context = new ToolExecutionContext
            {
                AgentInstanceId = "agent-a",
                WorkspaceId = "default",
                SessionId = "session-1",
            },
        });

    private sealed class RecordingMessageInbox(IReadOnlyList<MessageInboxItem> items) : IMessageInbox
    {
        public MessageInboxQuery? LastQuery { get; private set; }
        public List<string> Acked { get; } = [];

        public Task<IReadOnlyList<MessageInboxItem>> ListAsync(MessageInboxQuery query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(items);
        }

        public Task AckAsync(string deliveryId, CancellationToken ct = default)
        {
            Acked.Add(deliveryId);
            return Task.CompletedTask;
        }

        public Task<MessageInboxItem?> ClaimNextAsync(MessageClaimRequest request, CancellationToken ct = default) =>
            Task.FromResult<MessageInboxItem?>(null);

        public Task<IReadOnlyList<MessageInboxItem>> ClaimBatchAsync(
            MessageClaimRequest request,
            int maxBatch,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MessageInboxItem>>([]);

        public Task<int> RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task AckAsync(string deliveryId, string executionId, CancellationToken ct = default) =>
            AckAsync(deliveryId, ct);

        public Task RetryAsync(string deliveryId, string executionId, string error, DateTimeOffset availableAt, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeadLetterAsync(string deliveryId, string executionId, string error, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingAgentRosterProvider(IReadOnlyList<AgentRosterItem> items) : IAgentRosterProvider
    {
        public string? LastWorkspaceId { get; private set; }
        public string? LastRoomId { get; private set; }
        public bool LastIncludeBusy { get; private set; }
        public bool LastIncludeFrozen { get; private set; }

        public Task<IReadOnlyList<AgentRosterItem>> ListAgentsAsync(
            string workspaceId,
            string roomId,
            bool includeBusy,
            bool includeFrozen,
            CancellationToken ct)
        {
            LastWorkspaceId = workspaceId;
            LastRoomId = roomId;
            LastIncludeBusy = includeBusy;
            LastIncludeFrozen = includeFrozen;
            return Task.FromResult(items);
        }
    }
}
