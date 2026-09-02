using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Services;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Messaging;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class MessageDeliveryDispatcherTests
{
    [TestMethod]
    public async Task RecoveryPass_NoClaim_PrunesStaleKnownTarget()
    {
        var pendingTargets = new List<MessageDeliveryTarget>
        {
            new()
            {
                WorkspaceId = "default",
                RoomId = "room-default",
                TargetKind = MessageEndpointKinds.Agent,
                TargetId = "retired-sub-agent",
                HandlingMode = MessageDeliveryHandlingModes.Execute,
            },
        };
        var inbox = new RecordingMessageInbox
        {
            MaxClaimCount = 0,
            PendingTargets = pendingTargets,
        };
        var dispatcher = CreateDispatcher(inbox, new RecordingRuntimeAgentDispatcher());

        await dispatcher.RunRecoveryPassOnceAsync(CancellationToken.None);
        Assert.AreEqual(1, inbox.ClaimCount);

        pendingTargets.Clear();
        await dispatcher.RunRecoveryPassOnceAsync(CancellationToken.None);

        Assert.AreEqual(
            1,
            inbox.ClaimCount,
            "A durable target with no claimable row must not be probed every recovery interval.");
    }

    [TestMethod]
    public async Task HandleAsync_ClaimsDispatchesAndAcksAgentDelivery()
    {
        var inbox = new RecordingMessageInbox();
        var runtime = new RecordingRuntimeAgentDispatcher
        {
            Result = new RuntimeDispatchResult
            {
                SessionId = "session-1",
                AgentInstanceId = "agent-b",
                IsSuccess = true,
                ExecutionState = AgentExecutionState.Completed,
            },
        };
        var dispatcher = CreateDispatcher(inbox, runtime);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        Assert.IsNotNull(inbox.LastClaim);
        Assert.AreEqual("agent-b", inbox.LastClaim!.Endpoint.Id);
        Assert.IsEmpty(runtime.Requests);
        Assert.HasCount(1, runtime.StreamRequests);
        Assert.AreEqual("agent-b", runtime.StreamRequests[0].AgentInstanceId);
        Assert.AreEqual("general-assistant", runtime.StreamRequests[0].AgentTemplateId);
        Assert.AreEqual("hello", runtime.StreamRequests[0].MessageText);
        Assert.IsNotNull(runtime.StreamRequests[0].Origin);
        Assert.AreEqual(MessageEndpointKinds.User, runtime.StreamRequests[0].Origin!.FromKind);
        Assert.AreEqual("owner", runtime.StreamRequests[0].Origin!.FromId);
        Assert.AreEqual("agent_message", runtime.StreamRequests[0].Origin!.MessageType);
        Assert.AreEqual("m1", runtime.StreamRequests[0].MessageId);
        Assert.IsNotNull(runtime.StreamRequests[0].LlmConfig);
        Assert.AreEqual("test-model", runtime.StreamRequests[0].LlmConfig!.ModelId);
        Assert.HasCount(1, inbox.Acked);
        Assert.AreEqual("d1", inbox.Acked[0].DeliveryId);
        Assert.AreEqual(inbox.LastClaim.ExecutionId, inbox.Acked[0].ExecutionId);
        Assert.IsEmpty(inbox.Retried);
    }

    [TestMethod]
    public async Task HandleAsync_OrdinaryAgentDelivery_UsesTargetMainSession()
    {
        var inbox = new RecordingMessageInbox();
        var runtime = new RecordingRuntimeAgentDispatcher();
        var catalog = new RecordingWorkspaceAgentCatalog(
            Agent("agent-b", mainSessionId: "agent-b-main-session"));
        var dispatcher = CreateDispatcher(inbox, runtime, catalog: catalog);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        Assert.HasCount(1, runtime.StreamRequests);
        Assert.AreEqual("agent-b-main-session", runtime.StreamRequests[0].SessionId);
        Assert.AreNotEqual("session-1", runtime.StreamRequests[0].SessionId);
    }

    [TestMethod]
    public async Task HandleAsync_AgentDelivery_HandsOffCanonicalTurnWithSenderContext()
    {
        var inbox = new RecordingMessageInbox
        {
            ClaimContent = "hello",
            ClaimMetadata = new Dictionary<string, string>
            {
                ["custom_key"] = "custom-value",
                [MessageFabricTurnMetadata.FromId] = "forged-agent",
                [MessageFabricTurnMetadata.RoomId] = "forged-room",
                [MessageFabricTurnMetadata.ReplyExpected] = "false",
                [MessageDeliveryPolicy.IntentMetadataKey] = MessageIntents.Ask,
                [MessageDeliveryPolicy.RequiresResponseMetadataKey] = "true",
            },
            ClaimFrom = new MessageAddress
            {
                Kind = MessageEndpointKinds.Agent,
                Id = "agent-a",
                DisplayName = "Agent A",
            },
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var submit = new RecordingSubmitTurnHandler();
        var dispatcher = CreateDispatcher(
            inbox,
            runtime,
            submitTurnHandler: submit);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        Assert.IsEmpty(runtime.StreamRequests);
        Assert.HasCount(1, submit.Commands);
        var command = submit.Commands.Single();
        Assert.IsTrue(command.IsTrustedMessageFabricIngress);
        Assert.AreEqual("agent-b-main-session", command.ConversationId);
        var envelope = AgentContextEnvelopeRenderer.TryParse(
            command.Content.Single().Text);
        Assert.IsNotNull(envelope);
        Assert.AreEqual("hello", envelope.Context.Text);
        Assert.AreEqual(MessageEndpointKinds.Agent, envelope.From.Kind);
        Assert.AreEqual("agent-a", envelope.From.Id);
        Assert.AreEqual("Agent A", envelope.From.DisplayName);
        Assert.AreEqual(
            "true",
            command.Metadata![MessageFabricTurnMetadata.ReplyExpected]);
        Assert.AreEqual(
            "agent-a",
            command.Metadata[MessageFabricTurnMetadata.FromId]);
        Assert.AreEqual(
            "room-default",
            command.Metadata[MessageFabricTurnMetadata.RoomId]);
        Assert.AreEqual("custom-value", command.Metadata["custom_key"]);
        Assert.HasCount(1, inbox.Acked);
    }


    [TestMethod]
    public async Task HandleAsync_OrdinaryAgentDeliveryWithoutMainSession_DeadLettersWithError()
    {
        var inbox = new RecordingMessageInbox { ClaimAttemptCount = 3 };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var catalog = new RecordingWorkspaceAgentCatalog(Agent("agent-b", mainSessionId: null));
        var dispatcher = CreateDispatcher(inbox, runtime, catalog: catalog);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        Assert.IsEmpty(runtime.Requests);
        Assert.IsEmpty(inbox.Acked);
        Assert.HasCount(1, inbox.DeadLettered);
        StringAssert.Contains(inbox.DeadLettered[0].Error, "does not have a bound main session");
    }

    [TestMethod]
    public async Task HandleAsync_RetriesDeliveryWhenRuntimeFails()
    {
        var inbox = new RecordingMessageInbox();
        var runtime = new RecordingRuntimeAgentDispatcher
        {
            StreamFrames = [ServerSentEventFrame.Json("error", new { message = "model failed" })],
        };
        var dispatcher = CreateDispatcher(inbox, runtime);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        Assert.IsEmpty(inbox.Acked);
        Assert.HasCount(1, inbox.Retried);
        Assert.AreEqual("d1", inbox.Retried[0].DeliveryId);
        Assert.AreEqual(inbox.LastClaim!.ExecutionId, inbox.Retried[0].ExecutionId);
        StringAssert.Contains(inbox.Retried[0].Error, "model failed");
    }

    [TestMethod]
    public async Task HandleAsync_LostDeliveryLeaseDiscardsRuntimeResultWithoutSideEffects()
    {
        var inbox = new RecordingMessageInbox { RenewLeaseResult = false };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var dispatcher = CreateDispatcher(inbox, runtime);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        Assert.HasCount(1, runtime.StreamRequests);
        Assert.HasCount(1, inbox.Renewed);
        Assert.IsEmpty(inbox.Acked);
        Assert.IsEmpty(inbox.Retried);
        Assert.IsEmpty(inbox.DeadLettered);
    }

    [TestMethod]
    public async Task HandleAsync_HeartbeatWhenAgentBusy_AcksAndDropsWithoutCanonicalTurn()
    {
        var heartbeatFrom = new MessageAddress { Kind = MessageEndpointKinds.System, Id = "heartbeat" };
        var inbox = new RecordingMessageInbox
        {
            ClaimFrom = heartbeatFrom,
            ClaimContent = "── 系统心跳 ──\n\n[系统心跳] 你醒来了。",
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var availability = new RecordingAgentExecutionAvailabilityProvider("busy");
        var submit = new RecordingSubmitTurnHandler();
        var dispatcher = CreateDispatcher(
            inbox,
            runtime,
            availability,
            submitTurnHandler: submit);

        await dispatcher.HandleAsync(
            CreateEvent(MessageEndpointKinds.Agent, "agent-b", from: heartbeatFrom),
            CancellationToken.None);

        Assert.IsEmpty(runtime.StreamRequests);
        Assert.IsEmpty(submit.Commands);
        Assert.HasCount(1, inbox.Acked);
        Assert.AreEqual("d1", inbox.Acked[0].DeliveryId);
        Assert.IsEmpty(inbox.Retried);
        Assert.IsEmpty(inbox.DeadLettered);
    }

    [TestMethod]
    public async Task HandleAsync_AvailableHeartbeat_HandsOffCanonicalTurnAndAcksDelivery()
    {
        var heartbeatFrom = new MessageAddress { Kind = MessageEndpointKinds.System, Id = "heartbeat" };
        var inbox = new RecordingMessageInbox
        {
            ClaimFrom = heartbeatFrom,
            ClaimContent = "── 系统心跳 ──\n\n[系统心跳] 你醒来了。",
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var availability = new RecordingAgentExecutionAvailabilityProvider("idle");
        var submit = new RecordingSubmitTurnHandler();
        var dispatcher = CreateDispatcher(
            inbox,
            runtime,
            availability,
            submitTurnHandler: submit);

        await dispatcher.HandleAsync(
            CreateEvent(MessageEndpointKinds.Agent, "agent-b", from: heartbeatFrom),
            CancellationToken.None);

        Assert.IsEmpty(runtime.StreamRequests);
        Assert.HasCount(1, submit.Commands);
        var command = submit.Commands.Single();
        var envelope = AgentContextEnvelopeRenderer.TryParse(
            command.Content.Single().Text);
        Assert.IsNotNull(envelope);
        Assert.AreEqual("heartbeat", envelope.MessageType);
        Assert.AreEqual(MessageEndpointKinds.System, envelope.From.Kind);
        Assert.AreEqual("heartbeat", envelope.From.Id);
        Assert.AreEqual(
            "false",
            command.Metadata![MessageFabricTurnMetadata.ReplyExpected]);
        Assert.HasCount(1, inbox.Acked);
        Assert.AreEqual("d1", inbox.Acked[0].DeliveryId);
        Assert.IsEmpty(inbox.Retried);
        Assert.IsEmpty(inbox.DeadLettered);
    }

    [TestMethod]
    public async Task HandleAsync_OrdinaryDeliveryBusyOnThirdAttempt_DefersWithoutDeadLetter()
    {
        var inbox = new RecordingMessageInbox { ClaimAttemptCount = 3 };
        var runtime = new RecordingRuntimeAgentDispatcher
        {
            StreamFrames =
            [
                ServerSentEventFrame.Json("error", new
                {
                    error = "Agent 'agent-b' is busy.",
                    executionState = "Busy",
                }),
            ],
        };
        var dispatcher = CreateDispatcher(inbox, runtime);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        Assert.IsEmpty(inbox.Acked);
        Assert.IsEmpty(inbox.Retried);
        Assert.HasCount(1, inbox.Deferred);
        Assert.AreEqual("d1", inbox.Deferred[0].DeliveryId);
        Assert.IsEmpty(inbox.DeadLettered);
    }

    [TestMethod]
    public async Task HandleAsync_OrdinaryDeliveryBusyDeferral_StaysImmediatelyClaimableForIdleDrain()
    {
        // Busy deferral is queueing, not failure backoff. AvailableAt must stay in
        // the present so the agent.availability.changed(idle) drain (and the recovery
        // loop) can claim the delivery immediately after the agent frees up. A future
        // AvailableAt is filtered out by ClaimNextAsync (AvailableAt <= now) and
        // re-introduces the 30s wake delay this test guards against.
        var inbox = new RecordingMessageInbox { ClaimAttemptCount = 3 };
        var runtime = new RecordingRuntimeAgentDispatcher
        {
            StreamFrames =
            [
                ServerSentEventFrame.Json("error", new
                {
                    error = "Agent 'agent-b' is busy.",
                    executionState = "Busy",
                }),
            ],
        };
        var dispatcher = CreateDispatcher(inbox, runtime);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        Assert.HasCount(1, inbox.Deferred);
        Assert.AreEqual("d1", inbox.Deferred[0].DeliveryId);
        Assert.IsTrue(
            inbox.Deferred[0].AvailableAt <= DateTimeOffset.UtcNow,
            $"busy deferral must stay immediately claimable but AvailableAt was {inbox.Deferred[0].AvailableAt:o}");
    }

    [TestMethod]
    public async Task HandleAsync_BusyDeferral_SkipsClaimWithinCooldownWindow()
    {
        // The in-memory busy cooldown must stop the recovery loop / follow-up events
        // from re-claiming a busy target within the cooldown window: every claim
        // increments AttemptCount, so without the cooldown a busy target would be
        // hot-looped claim → dispatch → busy → defer and AttemptCount would inflate
        // without any progress.
        var backgroundSender = new MessageAddress { Kind = MessageEndpointKinds.System, Id = "scheduler" };
        var inbox = new RecordingMessageInbox
        {
            ClaimAttemptCount = 3,
            ClaimFrom = backgroundSender,
        };
        var runtime = new RecordingRuntimeAgentDispatcher
        {
            StreamFrames =
            [
                ServerSentEventFrame.Json("error", new
                {
                    error = "Agent 'agent-b' is busy.",
                    executionState = "Busy",
                }),
            ],
        };
        var dispatcher = CreateDispatcher(inbox, runtime);

        await dispatcher.HandleAsync(
            CreateEvent(MessageEndpointKinds.Agent, "agent-b", from: backgroundSender),
            CancellationToken.None);
        var firstExecution = inbox.LastClaim!.ExecutionId;

        // Second dispatch attempt inside the cooldown window must be skipped before
        // claiming, so no new execution is created and no second defer happens.
        await dispatcher.HandleAsync(
            CreateEvent(MessageEndpointKinds.Agent, "agent-b", from: backgroundSender),
            CancellationToken.None);

        Assert.HasCount(1, inbox.Deferred);
        Assert.AreEqual(firstExecution, inbox.LastClaim!.ExecutionId);
    }

    [TestMethod]
    public async Task HandleAsync_IdleAvailability_ClearsBusyCooldownAndDrains()
    {
        var inbox = new RecordingMessageInbox { ClaimAttemptCount = 3 };
        var runtime = new RecordingRuntimeAgentDispatcher
        {
            StreamFrames =
            [
                ServerSentEventFrame.Json("error", new
                {
                    error = "Agent 'agent-b' is busy.",
                    executionState = "Busy",
                }),
            ],
        };
        var dispatcher = CreateDispatcher(inbox, runtime);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);
        Assert.HasCount(1, inbox.Deferred);

        // The agent frees up: the idle availability event must clear the cooldown and
        // drain the deferred delivery immediately (no cooldown latency).
        runtime.StreamFrames = null; // default frames → successful dispatch
        await dispatcher.HandleAsync(CreateAvailabilityEvent("idle", "agent-b"), CancellationToken.None);

        Assert.HasCount(1, inbox.Acked);
        Assert.AreEqual("d1", inbox.Acked[0].DeliveryId);
        Assert.HasCount(1, inbox.Deferred);
    }

    [TestMethod]
    public async Task HandleAsync_DeadLettersDeliveryWhenThirdRuntimeAttemptFails()
    {
        var inbox = new RecordingMessageInbox { ClaimAttemptCount = 3 };
        var runtime = new RecordingRuntimeAgentDispatcher
        {
            StreamFrames = [ServerSentEventFrame.Json("error", new { message = "model failed" })],
        };
        var dispatcher = CreateDispatcher(inbox, runtime);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        Assert.IsEmpty(inbox.Retried);
        Assert.HasCount(1, inbox.DeadLettered);
        Assert.AreEqual("d1", inbox.DeadLettered[0].DeliveryId);
        Assert.AreEqual(inbox.LastClaim!.ExecutionId, inbox.DeadLettered[0].ExecutionId);
        StringAssert.Contains(inbox.DeadLettered[0].Error, "model failed");
    }

    [TestMethod]
    public async Task HandleAsync_IgnoresNonAgentDeliveries()
    {
        var inbox = new RecordingMessageInbox();
        var runtime = new RecordingRuntimeAgentDispatcher();
        var dispatcher = CreateDispatcher(inbox, runtime);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.User, "owner"), CancellationToken.None);

        Assert.IsNull(inbox.LastClaim);
        Assert.IsEmpty(runtime.Requests);
    }

    [TestMethod]
    public async Task HandleAsync_GatewayIngress_UsesCanonicalSubmitAndAcksWithoutDirectRuntime()
    {
        var metadata = new Dictionary<string, string>
        {
            [MessageGatewayMetadata.IsGatewayIngress] = "true",
            [MessageGatewayMetadata.ChannelId] = "feishu",
            [MessageGatewayMetadata.ChannelType] = "feishu",
            [MessageGatewayMetadata.ConnectorId] = "feishu:agent-b",
            [MessageGatewayMetadata.ExternalConversationId] = "oc_chat",
            [MessageGatewayMetadata.ExternalMessageId] = "om_message",
            [MessageGatewayMetadata.ClientRequestId] = "gateway-request-1",
        };
        var inbox = new RecordingMessageInbox
        {
            ClaimConversationId = "conversation-1",
            ClaimMetadata = metadata,
            ClaimContent = "hello",
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var submit = new RecordingSubmitTurnHandler();
        var dispatcher = CreateDispatcher(
            inbox,
            runtime,
            submitTurnHandler: submit);

        await dispatcher.HandleAsync(
            CreateEvent(
                MessageEndpointKinds.Agent,
                "agent-b",
                metadata),
            CancellationToken.None);

        Assert.HasCount(1, submit.Commands);
        var command = submit.Commands.Single();
        Assert.IsTrue(command.IsTrustedGatewayIngress);
        Assert.AreEqual("conversation-1", command.ConversationId);
        Assert.AreEqual("gateway-request-1", command.ClientRequestId);
        Assert.AreEqual("m1", command.ClientMessageId);
        CollectionAssert.AreEqual(
            new[] { "agent-b" },
            command.Recipients.AgentIds!.ToArray());
        Assert.AreEqual("hello", command.Content.Single().Text);
        Assert.AreEqual("feishu", command.Metadata![MessageGatewayMetadata.ChannelType]);
        Assert.AreEqual("d1", inbox.LastClaim!.DeliveryId);
        Assert.IsEmpty(runtime.Requests);
        Assert.IsEmpty(runtime.StreamRequests);
        Assert.HasCount(1, inbox.Acked);
        Assert.IsEmpty(inbox.Retried);
    }

    [TestMethod]
    public async Task HandleAsync_MessageDeliver_ClaimsWithoutPrecheckingAvailability()
    {
        var inbox = new RecordingMessageInbox();
        var runtime = new RecordingRuntimeAgentDispatcher();
        var availability = new RecordingAgentExecutionAvailabilityProvider("busy");
        var dispatcher = CreateDispatcher(inbox, runtime, availability);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        Assert.IsNotNull(inbox.LastClaim);
        Assert.IsEmpty(runtime.Requests);
        Assert.HasCount(1, runtime.StreamRequests);
        Assert.IsEmpty(availability.Requests);
    }

    [TestMethod]
    public async Task HandleAsync_PassiveNotificationWhileAgentBusy_AppendsAndAcksWithoutModelTurn()
    {
        var inbox = new RecordingMessageInbox
        {
            ClaimMetadata = new Dictionary<string, string>
            {
                [MessageDeliveryPolicy.IntentMetadataKey] = MessageIntents.Inform,
                [MessageDeliveryPolicy.RequiresResponseMetadataKey] = "false",
            },
            ClaimHandlingMode = MessageDeliveryHandlingModes.Notify,
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var availability = new RecordingAgentExecutionAvailabilityProvider("busy");
        var submit = new RecordingSubmitTurnHandler();
        var notifications = new RecordingConversationNotificationStore();
        var dispatcher = CreateDispatcher(
            inbox,
            runtime,
            availability,
            submitTurnHandler: submit,
            notificationStore: notifications);

        await dispatcher.HandleAsync(
            CreateEvent(
                MessageEndpointKinds.Agent,
                "agent-b",
                inbox.ClaimMetadata,
                handlingMode: null),
            CancellationToken.None);

        Assert.IsEmpty(availability.Requests);
        Assert.IsEmpty(runtime.Requests);
        Assert.IsEmpty(runtime.StreamRequests);
        Assert.IsEmpty(submit.Commands);
        Assert.HasCount(1, notifications.Requests);
        Assert.AreEqual("agent-b-main-session", notifications.Requests[0].ConversationId);
        Assert.HasCount(1, inbox.Acked);
    }

    [TestMethod]
    public async Task HandleAsync_ConnectorCanonicalTurn_HandsOffAndPreservesReceiptCorrelation()
    {
        var inbox = new RecordingMessageInbox
        {
            ClaimContent = "external request",
            ClaimMetadata = new Dictionary<string, string>
            {
                [MessageDeliveryPolicy.IntentMetadataKey] = MessageIntents.Ask,
                [MessageDeliveryPolicy.RequiresResponseMetadataKey] = "true",
                [MessageDeliveryPolicy.CanonicalTurnMetadataKey] = "true",
                ["source"] = "external.api",
            },
            ClaimFrom = new MessageAddress
            {
                Kind = MessageEndpointKinds.Connector,
                Id = "access-token:token-1",
                DisplayName = "External API",
            },
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var submit = new RecordingSubmitTurnHandler();
        var dispatcher = CreateDispatcher(
            inbox,
            runtime,
            submitTurnHandler: submit);

        await dispatcher.HandleAsync(
            CreateEvent(
                MessageEndpointKinds.Agent,
                "agent-b",
                metadata: inbox.ClaimMetadata,
                from: inbox.ClaimFrom),
            CancellationToken.None);

        Assert.IsEmpty(runtime.StreamRequests);
        Assert.HasCount(1, submit.Commands);
        var command = submit.Commands.Single();
        Assert.IsTrue(command.IsTrustedMessageFabricIngress);
        Assert.AreEqual("agent-b-main-session", command.ConversationId);
        Assert.AreEqual(
            "m1",
            command.Metadata![MessageFabricTurnMetadata.MessageId]);
        Assert.AreEqual(
            MessageEndpointKinds.Connector,
            command.Metadata[MessageFabricTurnMetadata.FromKind]);
        Assert.AreEqual(
            "access-token:token-1",
            command.Metadata[MessageFabricTurnMetadata.FromId]);
        Assert.AreEqual(
            "true",
            command.Metadata[MessageFabricTurnMetadata.ReplyExpected]);
        Assert.HasCount(1, inbox.Acked);
    }

    [TestMethod]
    public async Task HandleAsync_PassiveNotifications_ClaimsBoundedBatchButAppendsSeparateFacts()
    {
        var metadata = new Dictionary<string, string>
        {
            [MessageDeliveryPolicy.IntentMetadataKey] = MessageIntents.AgentReply,
            [MessageDeliveryPolicy.RequiresResponseMetadataKey] = "false",
        };
        var inbox = new RecordingMessageInbox
        {
            ClaimMetadata = metadata,
            ClaimHandlingMode = MessageDeliveryHandlingModes.Notify,
            BatchClaims =
            [
                new MessageInboxItem
                {
                    DeliveryId = "d2",
                    MessageId = "m2",
                    WorkspaceId = "default",
                    RoomId = "room-other",
                    From = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "agent-c" },
                    Target = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "agent-b" },
                    Content = "second notification",
                    Status = MessageDeliveryStatuses.Delivering,
                    HandlingMode = MessageDeliveryHandlingModes.Notify,
                    AttemptCount = 1,
                    CreatedAt = 101,
                    Metadata = metadata,
                },
            ],
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var notifications = new RecordingConversationNotificationStore();
        var dispatcher = CreateDispatcher(
            inbox,
            runtime,
            notificationStore: notifications);

        await dispatcher.HandleAsync(
            CreateEvent(
                MessageEndpointKinds.Agent,
                "agent-b",
                metadata,
                handlingMode: MessageDeliveryHandlingModes.Notify),
            CancellationToken.None);

        Assert.AreEqual(19, inbox.LastBatchMax);
        Assert.IsNull(inbox.LastBatchClaim?.RoomId);
        Assert.AreEqual(MessageDeliveryHandlingModes.Notify, inbox.LastBatchClaim?.HandlingMode);
        Assert.HasCount(2, notifications.Requests);
        Assert.AreNotEqual(notifications.Requests[0].MessageId, notifications.Requests[1].MessageId);
        Assert.HasCount(2, inbox.Acked);
        Assert.IsEmpty(runtime.StreamRequests);
    }

    [TestMethod]
    public async Task HandleAsync_ClaimsDispatchesAndAcksWhenTargetAgentIsIdle()
    {
        var inbox = new RecordingMessageInbox();
        var runtime = new RecordingRuntimeAgentDispatcher();
        var availability = new RecordingAgentExecutionAvailabilityProvider("idle");
        var dispatcher = CreateDispatcher(inbox, runtime, availability);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        Assert.IsNotNull(inbox.LastClaim);
        Assert.HasCount(1, runtime.StreamRequests);
        Assert.HasCount(1, inbox.Acked);
        Assert.IsEmpty(availability.Requests);
    }

    [TestMethod]
    public async Task StartAsync_SubscribesToMessageDeliverAndAvailabilityChanged()
    {
        var inbox = new RecordingMessageInbox();
        var runtime = new RecordingRuntimeAgentDispatcher();
        var eventBus = new RecordingInternalEventBus();
        var dispatcher = CreateDispatcher(inbox, runtime, eventBus: eventBus);

        await dispatcher.StartAsync(CancellationToken.None);
        await dispatcher.StopAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "message.deliver", "agent.availability.changed" },
            eventBus.SubscriptionPatterns);
    }

    [TestMethod]
    public async Task StartAsync_DiscoversAndDispatchesDurablePendingTarget()
    {
        var inbox = new RecordingMessageInbox
        {
            PendingTargets =
            [
                new MessageDeliveryTarget
                {
                    WorkspaceId = "default",
                    RoomId = "room-default",
                    TargetKind = MessageEndpointKinds.Agent,
                    TargetId = "agent-b",
                },
            ],
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var dispatcher = CreateDispatcher(inbox, runtime);

        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (inbox.Acked.Count == 0)
                await Task.Delay(10, timeout.Token);
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }

        Assert.HasCount(1, inbox.PendingTargetKinds);
        Assert.AreEqual(MessageEndpointKinds.Agent, inbox.PendingTargetKinds[0]);
        Assert.HasCount(1, runtime.StreamRequests);
        Assert.HasCount(1, inbox.Acked);
    }

    [TestMethod]
    public async Task HandleAsync_AvailabilityChangedToIdle_ClaimsDispatchesAndAcks()
    {
        var inbox = new RecordingMessageInbox();
        var runtime = new RecordingRuntimeAgentDispatcher();
        var availability = new RecordingAgentExecutionAvailabilityProvider("idle");
        var dispatcher = CreateDispatcher(inbox, runtime, availability);

        await dispatcher.HandleAsync(CreateAvailabilityEvent("idle", "agent-b"), CancellationToken.None);

        Assert.IsNotNull(inbox.LastClaim);
        Assert.AreEqual("agent-b", inbox.LastClaim!.Endpoint.Id);
        Assert.HasCount(1, runtime.StreamRequests);
        Assert.HasCount(1, inbox.Acked);
    }

    [TestMethod]
    public async Task HandleAsync_OrdinaryAgentDelivery_PersistsInboundAndReplyTranscript()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<PlatformDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton(new RecordingMessageInbox
        {
            ClaimContent = "hello from another agent",
        });
        services.AddSingleton(new RecordingRuntimeAgentDispatcher
        {
            StreamFrames =
            [
                ServerSentEventFrame.Json("delta", new { delta = "reply " }),
                ServerSentEventFrame.Json("delta", new { delta = "from target" }),
                ServerSentEventFrame.Json("done", new { reply = "reply from target" }),
            ],
        });
        services.AddScoped<IMessageInbox>(sp => sp.GetRequiredService<RecordingMessageInbox>());
        services.AddScoped<IRuntimeAgentDispatcher>(sp => sp.GetRequiredService<RecordingRuntimeAgentDispatcher>());
        services.AddScoped<IWorkspaceAgentCatalog>(_ => new RecordingWorkspaceAgentCatalog(
            Agent("agent-b", mainSessionId: "agent-b-main-session")));
        services.AddScoped<IAgentRuntimeProfileResolver>(_ => new RecordingAgentRuntimeProfileResolver(
            [Agent("agent-b", mainSessionId: "agent-b-main-session")]));
        services.AddScoped<IAgentInvocationDispatchFactory, AgentInvocationDispatchFactory>();
        services.AddSingleton<IChatTranscriptWriter, ChatTranscriptWriter>();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.EnsureCreatedAsync();
        }

        var dispatcher = new MessageDeliveryDispatcher(
            new RecordingInternalEventBus(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new AgentWakeQueue(NullLogger<AgentWakeQueue>.Instance),
            new AgentExecutionAdmissionCoordinator(),
            NullLogger<MessageDeliveryDispatcher>.Instance);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        await using var assertScope = provider.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var transcript = await db.ChatMessages
            .Where(m => m.SessionId == "agent-b-main-session")
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync();

        Assert.HasCount(2, transcript);
        Assert.AreEqual("user", transcript[0].Role);
        var inboundEnvelope = AgentContextEnvelopeRenderer.TryParse(transcript[0].Content);
        Assert.IsNotNull(inboundEnvelope);
        Assert.AreEqual("hello from another agent", inboundEnvelope!.Context.Text);
        Assert.AreEqual(MessageEndpointKinds.User, inboundEnvelope.From.Kind);
        Assert.AreEqual("owner", inboundEnvelope.From.Id);
        Assert.AreEqual("agent", transcript[1].Role);
        Assert.AreEqual("reply from target", transcript[1].Content);
    }

    [TestMethod]
    public async Task HandleAsync_DuplicateRuntimeResult_DoesNotPersistOrSendPlaceholderReply()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var inbox = new RecordingMessageInbox
        {
            ClaimFrom = new MessageAddress
            {
                Kind = MessageEndpointKinds.User,
                Id = "owner",
                DisplayName = "Owner",
            },
            ClaimContent = "already delivered",
        };
        var messageSystem = new RecordingMessageSystem();
        var services = new ServiceCollection();
        services.AddDbContext<PlatformDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton(inbox);
        services.AddSingleton(new RecordingRuntimeAgentDispatcher
        {
            StreamFrames =
            [
                ServerSentEventFrame.Json("done", new
                {
                    reply = RuntimeDispatchMarkers.DuplicateMessagePlaceholder,
                    duplicateMessage = true,
                    stopReason = RuntimeDispatchMarkers.DuplicateMessageStopReason,
                }),
            ],
        });
        services.AddSingleton<IMessageSystem>(messageSystem);
        services.AddScoped<IMessageInbox>(sp => sp.GetRequiredService<RecordingMessageInbox>());
        services.AddScoped<IRuntimeAgentDispatcher>(sp => sp.GetRequiredService<RecordingRuntimeAgentDispatcher>());
        services.AddScoped<IWorkspaceAgentCatalog>(_ => new RecordingWorkspaceAgentCatalog(
            Agent("agent-b", mainSessionId: "agent-b-main-session")));
        services.AddScoped<IAgentRuntimeProfileResolver>(_ => new RecordingAgentRuntimeProfileResolver(
            [Agent("agent-b", mainSessionId: "agent-b-main-session")]));
        services.AddScoped<IAgentInvocationDispatchFactory, AgentInvocationDispatchFactory>();
        services.AddSingleton<IChatTranscriptWriter, ChatTranscriptWriter>();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.ChatMessages.Add(new ChatMessageEntity
            {
                MessageId = "m1",
                SessionId = "agent-b-main-session",
                Role = "user",
                Content = "already persisted inbound",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            await db.SaveChangesAsync();
        }

        var dispatcher = new MessageDeliveryDispatcher(
            new RecordingInternalEventBus(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new AgentWakeQueue(NullLogger<AgentWakeQueue>.Instance),
            new AgentExecutionAdmissionCoordinator(),
            NullLogger<MessageDeliveryDispatcher>.Instance);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        await using var assertScope = provider.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var transcript = await assertDb.ChatMessages
            .Where(message => message.SessionId == "agent-b-main-session")
            .ToListAsync();
        Assert.HasCount(1, transcript);
        Assert.IsFalse(transcript.Any(message =>
            RuntimeDispatchMarkers.IsDuplicateMessagePlaceholder(message.Content)));
        Assert.IsEmpty(messageSystem.Sent);
        Assert.HasCount(1, inbox.Acked);
    }

    [TestMethod]
    public async Task HandleAsync_AgentDelivery_RecordsDeferredReplyProjectionRoute()
    {
        var inbox = new RecordingMessageInbox
        {
            ClaimMetadata = new Dictionary<string, string>
            {
                [MessageDeliveryPolicy.IntentMetadataKey] = MessageIntents.Ask,
                [MessageDeliveryPolicy.RequiresResponseMetadataKey] = "true",
            },
            ClaimFrom = new MessageAddress
            {
                Kind = MessageEndpointKinds.Agent,
                Id = "agent-a",
                DisplayName = "Agent A",
            },
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var messageSystem = new RecordingMessageSystem();
        var submit = new RecordingSubmitTurnHandler();
        var dispatcher = CreateDispatcher(
            inbox,
            runtime,
            messageSystem: messageSystem,
            submitTurnHandler: submit);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        Assert.IsEmpty(runtime.StreamRequests);
        Assert.IsEmpty(messageSystem.Sent);
        var command = submit.Commands.Single();
        Assert.AreEqual(
            "true",
            command.Metadata![MessageFabricTurnMetadata.ReplyExpected]);
        Assert.AreEqual(
            "agent-a",
            command.Metadata[MessageFabricTurnMetadata.FromId]);
        Assert.AreEqual(
            "m1",
            command.Metadata[MessageFabricTurnMetadata.MessageId]);
        Assert.HasCount(1, inbox.Acked);
    }

    [TestMethod]
    public async Task HandleAsync_CanonicalHandoffFailure_RetriesInboundDelivery()
    {
        var inbox = new RecordingMessageInbox
        {
            ClaimFrom = new MessageAddress
            {
                Kind = MessageEndpointKinds.Agent,
                Id = "retired-child-agent",
                DisplayName = "Retired Child Agent",
            },
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var submit = new RecordingSubmitTurnHandler
        {
            Failure = new InvalidOperationException("acceptance store unavailable"),
        };
        var dispatcher = CreateDispatcher(
            inbox,
            runtime,
            submitTurnHandler: submit);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        Assert.IsEmpty(runtime.StreamRequests);
        Assert.IsEmpty(inbox.Acked);
        Assert.HasCount(1, inbox.Retried);
        StringAssert.Contains(inbox.Retried[0].Error, "acceptance store unavailable");
        Assert.IsEmpty(inbox.DeadLettered);
    }

    [TestMethod]
    public async Task HandleAsync_AgentDelivery_IsAcceptedAsIndependentTurnWithoutBatching()
    {
        var agentSender = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "agent-a" };
        var inbox = new RecordingMessageInbox
        {
            ClaimFrom = agentSender,
            BatchClaims =
            [
                new MessageInboxItem
                {
                    DeliveryId = "d2",
                    MessageId = "m2",
                    WorkspaceId = "default",
                    RoomId = "room-default",
                    From = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "agent-a" },
                    Target = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "agent-b" },
                    Content = "second message",
                    Status = MessageDeliveryStatuses.Delivering,
                    Priority = 0,
                    AttemptCount = 1,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            ],
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var submit = new RecordingSubmitTurnHandler();
        var dispatcher = CreateDispatcher(
            inbox,
            runtime,
            submitTurnHandler: submit);

        await dispatcher.HandleAsync(
            CreateEvent(MessageEndpointKinds.Agent, "agent-b", from: agentSender),
            CancellationToken.None);

        Assert.HasCount(1, submit.Commands);
        Assert.AreEqual("hello", AgentContextEnvelopeRenderer.TryParse(
            submit.Commands.Single().Content.Single().Text)!.Context.Text);
        Assert.HasCount(1, inbox.Acked);
        Assert.AreEqual("d1", inbox.Acked.Single().DeliveryId);
        Assert.IsFalse(inbox.Acked.Any(item => item.DeliveryId == "d2"));
        Assert.IsEmpty(inbox.Retried);
    }

    [TestMethod]
    public async Task HandleAsync_AgentReplyDelivery_DoesNotEchoReply()
    {
        var inbox = new RecordingMessageInbox
        {
            ClaimFrom = new MessageAddress
            {
                Kind = MessageEndpointKinds.Agent,
                Id = "agent-a",
                DisplayName = "Agent A",
            },
        };
        var runtime = new RecordingRuntimeAgentDispatcher
        {
            StreamFrames = [ServerSentEventFrame.Json("done", new { reply = "ack" })],
        };
        var messageSystem = new RecordingMessageSystem();
        var submit = new RecordingSubmitTurnHandler();
        var dispatcher = CreateDispatcher(
            inbox,
            runtime,
            messageSystem: messageSystem,
            submitTurnHandler: submit);

        await dispatcher.HandleAsync(
            CreateEvent(
                MessageEndpointKinds.Agent,
                "agent-b",
                metadata: new Dictionary<string, string> { ["intent"] = "agent_reply" }),
            CancellationToken.None);

        Assert.IsEmpty(messageSystem.Sent);
        Assert.IsEmpty(runtime.StreamRequests);
        Assert.AreEqual(
            "false",
            submit.Commands.Single().Metadata![MessageFabricTurnMetadata.ReplyExpected]);
        Assert.HasCount(1, inbox.Acked);
    }

    [TestMethod]
    public async Task HandleAsync_AvailabilityChangedToBusy_DoesNotClaim()
    {
        var inbox = new RecordingMessageInbox();
        var runtime = new RecordingRuntimeAgentDispatcher();
        var availability = new RecordingAgentExecutionAvailabilityProvider("idle");
        var dispatcher = CreateDispatcher(inbox, runtime, availability);

        await dispatcher.HandleAsync(CreateAvailabilityEvent("busy", "agent-b"), CancellationToken.None);

        Assert.IsNull(inbox.LastClaim);
        Assert.IsEmpty(runtime.Requests);
    }

    [TestMethod]
    public async Task HandleAsync_SubAgentResultMessage_UsesStreamDispatchAndAcks()
    {
        var inbox = new RecordingMessageInbox
        {
            ClaimMetadata = new Dictionary<string, string>
            {
                ["source"] = "subagent",
                ["intent"] = "subagent_result",
            },
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var dispatcher = CreateDispatcher(inbox, runtime);

        await dispatcher.HandleAsync(CreateSubAgentResultEvent(), CancellationToken.None);

        Assert.IsEmpty(runtime.Requests);
        Assert.HasCount(1, runtime.StreamRequests);
        Assert.AreEqual("agent-b", runtime.StreamRequests[0].AgentInstanceId);
        Assert.AreEqual("subagent result", runtime.StreamRequests[0].MessageText);
        Assert.HasCount(1, inbox.Acked);
    }

    [TestMethod]
    public async Task HandleAsync_ForegroundTurnPreemptsRunningSubAgentResultAndDefersDelivery()
    {
        var inbox = new RecordingMessageInbox
        {
            ClaimMetadata = new Dictionary<string, string>
            {
                ["source"] = "subagent",
                ["intent"] = "subagent_result",
            },
            ClaimFrom = new MessageAddress
            {
                Kind = MessageEndpointKinds.Agent,
                Id = "child-agent",
            },
            MaxClaimCount = 1,
        };
        var runtime = new BlockingRuntimeAgentDispatcher();
        var coordinator = new AgentExecutionAdmissionCoordinator();
        var dispatcher = CreateDispatcher(
            inbox,
            runtime,
            admissionCoordinator: coordinator);

        var backgroundTask = dispatcher.HandleAsync(
            CreateSubAgentResultEvent(),
            CancellationToken.None);
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using (coordinator.AcquireForeground("default", "agent-b"))
            await backgroundTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(runtime.CancellationObserved);
        Assert.HasCount(1, inbox.Deferred);
        Assert.AreEqual("d1", inbox.Deferred[0].DeliveryId);
        Assert.IsEmpty(inbox.Acked);
        Assert.IsEmpty(inbox.Retried);
        Assert.IsEmpty(inbox.DeadLettered);
    }

    [TestMethod]
    public async Task HandleAsync_ForegroundDemandClaimsAndDropsHeartbeatWithoutCreatingTurn()
    {
        var heartbeatFrom = new MessageAddress { Kind = MessageEndpointKinds.System, Id = "heartbeat" };
        var inbox = new RecordingMessageInbox
        {
            ClaimFrom = heartbeatFrom,
            ClaimContent = "── 系统心跳 ──\n\n[系统心跳] 你醒来了。",
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var coordinator = new AgentExecutionAdmissionCoordinator();
        var submit = new RecordingSubmitTurnHandler();
        var dispatcher = CreateDispatcher(
            inbox,
            runtime,
            submitTurnHandler: submit,
            admissionCoordinator: coordinator);

        using (coordinator.AcquireForeground("default", "agent-b"))
        {
            await dispatcher.HandleAsync(
                CreateEvent(MessageEndpointKinds.Agent, "agent-b", from: heartbeatFrom),
                CancellationToken.None);
        }

        Assert.IsNotNull(inbox.LastClaim);
        Assert.IsEmpty(submit.Commands);
        Assert.HasCount(1, inbox.Acked);
        Assert.AreEqual("d1", inbox.Acked.Single().DeliveryId);
        Assert.IsEmpty(inbox.Deferred);
        Assert.IsEmpty(inbox.Retried);
        Assert.IsEmpty(inbox.DeadLettered);
    }

    [TestMethod]
    public async Task HandleAsync_ForegroundDemandSkipsBackgroundClaimWithoutIncrementingAttempt()
    {
        var inbox = new RecordingMessageInbox();
        var runtime = new RecordingRuntimeAgentDispatcher();
        var coordinator = new AgentExecutionAdmissionCoordinator();
        var dispatcher = CreateDispatcher(
            inbox,
            runtime,
            admissionCoordinator: coordinator);

        using (coordinator.AcquireForeground("default", "agent-b"))
        {
            await dispatcher.HandleAsync(
                CreateSubAgentResultEvent(),
                CancellationToken.None);
        }

        Assert.IsNull(inbox.LastClaim);
        Assert.IsEmpty(runtime.StreamRequests);
    }

    [TestMethod]
    public async Task HandleAsync_SubAgentResultMessage_PersistsParentContinuationTranscript()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<PlatformDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton(new RecordingMessageInbox
        {
            ClaimMetadata = new Dictionary<string, string>
            {
                ["source"] = "subagent",
                ["intent"] = "subagent_result",
                ["sub_agent_id"] = "sub-1",
            },
            ClaimContent = """
            {
              "schema": "pudding-message",
              "version": 1,
              "message_id": "msg-sub-result",
              "message_type": "subagent_result",
              "from": { "kind": "agent", "id": "sub-1", "display_name": "Sub Agent" },
              "to": [{ "kind": "agent", "id": "parent-agent" }],
              "constraints": ["This message was delivered by Pudding Message Fabric."],
              "context": { "format": "text/markdown", "text": "child completed" }
            }
            """,
        });
        services.AddSingleton(new RecordingRuntimeAgentDispatcher
        {
            StreamFrames =
            [
                ServerSentEventFrame.Json("thinking", new { delta = "thinking about child result" }),
                ServerSentEventFrame.Json("delta", new { delta = "parent " }),
                ServerSentEventFrame.Json("delta", new { delta = "continuation" }),
                ServerSentEventFrame.Json("usage", new { promptTokens = 2, completionTokens = 3, totalTokens = 5 }),
                ServerSentEventFrame.Json("done", new { reply = "parent continuation", usage = new { promptTokens = 2, completionTokens = 3, totalTokens = 5 } }),
            ],
        });
        services.AddScoped<IMessageInbox>(sp => sp.GetRequiredService<RecordingMessageInbox>());
        services.AddScoped<IRuntimeAgentDispatcher>(sp => sp.GetRequiredService<RecordingRuntimeAgentDispatcher>());
        services.AddScoped<IWorkspaceAgentCatalog>(_ => new RecordingWorkspaceAgentCatalog(
            Agent("agent-b", mainSessionId: "agent-b-main-session")));
        services.AddScoped<IAgentRuntimeProfileResolver>(_ => new RecordingAgentRuntimeProfileResolver(
            [Agent("agent-b", mainSessionId: "agent-b-main-session")]));
        services.AddScoped<IAgentInvocationDispatchFactory, AgentInvocationDispatchFactory>();
        services.AddSingleton<IChatTranscriptWriter, ChatTranscriptWriter>();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.EnsureCreatedAsync();
        }

        var dispatcher = new MessageDeliveryDispatcher(
            new RecordingInternalEventBus(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new AgentWakeQueue(NullLogger<AgentWakeQueue>.Instance),
            new AgentExecutionAdmissionCoordinator(),
            NullLogger<MessageDeliveryDispatcher>.Instance);

        await dispatcher.HandleAsync(CreateSubAgentResultEvent(), CancellationToken.None);

        await using var assertScope = provider.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var transcript = await db.ChatMessages.SingleAsync(m => m.SessionId == "session-1" && m.Role == "agent");
        Assert.AreEqual("parent continuation", transcript.Content);
        Assert.IsNotNull(transcript.ThinkingJson);
        StringAssert.Contains(transcript.ThinkingJson!, "thinking about child result");
        var decodedThinking = ReasoningCompactCodec.Decode(transcript.ThinkingJson);
        Assert.IsNotNull(decodedThinking);
        Assert.IsTrue(decodedThinking!.IsCompactFormat, "新写 thinking 必须为 v2 紧凑格式");
        Assert.IsTrue(decodedThinking.HashValid, "hash 应与 text 匹配");
        Assert.AreEqual("thinking about child result", decodedThinking.Text);
        Assert.HasCount(1, decodedThinking.Chunks);
        Assert.AreEqual("thinking about child result", decodedThinking.Chunks[0].Text);
        Assert.IsNotNull(transcript.UsageJson);
        StringAssert.Contains(transcript.UsageJson!, "totalTokens");
        var runtime = provider.GetRequiredService<RecordingRuntimeAgentDispatcher>();
        Assert.HasCount(1, runtime.StreamRequests);
        StringAssert.Contains(runtime.StreamRequests[0].MessageText, "\"schema\": \"pudding-message\"");
        StringAssert.Contains(runtime.StreamRequests[0].MessageText, "\"message_type\": \"subagent_result\"");
    }

    [TestMethod]
    public async Task HandleAsync_ThinkingFrames_PersistCompactV2ThinkingJson()
    {
        var transcript = await PersistSubAgentTranscriptAsync(
        [
            ServerSentEventFrame.Json("thinking", new { delta = "step one " }),
            ServerSentEventFrame.Json("thinking", new { delta = "step two" }),
            ServerSentEventFrame.Json("delta", new { delta = "parent " }),
            ServerSentEventFrame.Json("delta", new { delta = "continuation" }),
            ServerSentEventFrame.Json("done", new { reply = "parent continuation" }),
        ]);

        Assert.IsNotNull(transcript.ThinkingJson);
        Assert.IsFalse(transcript.ThinkingJson!.StartsWith('['), "新写 thinking 必须为 v2 紧凑格式而非旧数组");
        var decoded = ReasoningCompactCodec.Decode(transcript.ThinkingJson);
        Assert.IsNotNull(decoded);
        Assert.IsTrue(decoded!.IsCompactFormat, "落库 ThinkingJson 应为新格式");
        Assert.IsTrue(decoded.HashValid, "hash 应与 text 匹配");
        Assert.AreEqual("step one step two", decoded.Text);
        Assert.HasCount(2, decoded.Chunks);
        Assert.AreEqual("step one ", decoded.Chunks[0].Text);
        Assert.AreEqual("step two", decoded.Chunks[1].Text);
        Assert.IsTrue(decoded.Chunks[0].Timestamp > 0);
        Assert.IsTrue(decoded.Chunks[1].Timestamp >= decoded.Chunks[0].Timestamp);
    }

    [TestMethod]
    public async Task HandleAsync_ThinkingFrames_ChineseMultiByteUtf8Offsets_RoundTrip()
    {
        var transcript = await PersistSubAgentTranscriptAsync(
        [
            ServerSentEventFrame.Json("thinking", new { delta = "思考中" }),
            ServerSentEventFrame.Json("thinking", new { delta = "，分析" }),
            ServerSentEventFrame.Json("thinking", new { delta = "用户需求" }),
            ServerSentEventFrame.Json("delta", new { delta = "好的，" }),
            ServerSentEventFrame.Json("delta", new { delta = "我来处理" }),
            ServerSentEventFrame.Json("done", new { reply = "好的，我来处理" }),
        ]);

        Assert.IsNotNull(transcript.ThinkingJson);
        var decoded = ReasoningCompactCodec.Decode(transcript.ThinkingJson);
        Assert.IsNotNull(decoded);
        Assert.IsTrue(decoded!.IsCompactFormat);
        Assert.IsTrue(decoded.HashValid);
        Assert.AreEqual("思考中，分析用户需求", decoded.Text);
        Assert.HasCount(3, decoded.Chunks);
        Assert.AreEqual("思考中", decoded.Chunks[0].Text);
        Assert.AreEqual("，分析", decoded.Chunks[1].Text);
        Assert.AreEqual("用户需求", decoded.Chunks[2].Text);
        Assert.IsTrue(decoded.Chunks[2].Timestamp >= decoded.Chunks[1].Timestamp);
    }

    private static async Task<ChatMessageEntity> PersistSubAgentTranscriptAsync(
        IReadOnlyList<ServerSentEventFrame> frames)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<PlatformDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton(new RecordingMessageInbox
        {
            ClaimMetadata = new Dictionary<string, string>
            {
                ["source"] = "subagent",
                ["intent"] = "subagent_result",
                ["sub_agent_id"] = "sub-1",
            },
            ClaimContent = """
            {
              "schema": "pudding-message",
              "version": 1,
              "message_id": "msg-sub-result",
              "message_type": "subagent_result",
              "from": { "kind": "agent", "id": "sub-1", "display_name": "Sub Agent" },
              "to": [{ "kind": "agent", "id": "parent-agent" }],
              "constraints": ["This message was delivered by Pudding Message Fabric."],
              "context": { "format": "text/markdown", "text": "child completed" }
            }
            """,
        });
        services.AddSingleton(new RecordingRuntimeAgentDispatcher { StreamFrames = frames });
        services.AddScoped<IMessageInbox>(sp => sp.GetRequiredService<RecordingMessageInbox>());
        services.AddScoped<IRuntimeAgentDispatcher>(sp => sp.GetRequiredService<RecordingRuntimeAgentDispatcher>());
        services.AddScoped<IWorkspaceAgentCatalog>(_ => new RecordingWorkspaceAgentCatalog(
            Agent("agent-b", mainSessionId: "agent-b-main-session")));
        services.AddScoped<IAgentRuntimeProfileResolver>(_ => new RecordingAgentRuntimeProfileResolver(
            [Agent("agent-b", mainSessionId: "agent-b-main-session")]));
        services.AddScoped<IAgentInvocationDispatchFactory, AgentInvocationDispatchFactory>();
        services.AddSingleton<IChatTranscriptWriter, ChatTranscriptWriter>();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.EnsureCreatedAsync();
        }

        var dispatcher = new MessageDeliveryDispatcher(
            new RecordingInternalEventBus(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new AgentWakeQueue(NullLogger<AgentWakeQueue>.Instance),
            new AgentExecutionAdmissionCoordinator(),
            NullLogger<MessageDeliveryDispatcher>.Instance);

        await dispatcher.HandleAsync(CreateSubAgentResultEvent(), CancellationToken.None);

        await using var assertScope = provider.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await db.ChatMessages.SingleAsync(m => m.SessionId == "session-1" && m.Role == "agent");
    }

    private static MessageDeliveryDispatcher CreateDispatcher(
        RecordingMessageInbox inbox,
        IRuntimeAgentDispatcher runtime,
        RecordingAgentExecutionAvailabilityProvider? availability = null,
        RecordingInternalEventBus? eventBus = null,
        RecordingWorkspaceAgentCatalog? catalog = null,
        RecordingMessageSystem? messageSystem = null,
        RecordingSubmitTurnHandler? submitTurnHandler = null,
        RecordingConversationNotificationStore? notificationStore = null,
        AgentExecutionAdmissionCoordinator? admissionCoordinator = null)
    {
        var services = new ServiceCollection();
        var effectiveCatalog = catalog ?? new RecordingWorkspaceAgentCatalog(
            Agent("agent-b", mainSessionId: "agent-b-main-session"));
        services.AddScoped<IMessageInbox>(_ => inbox);
        services.AddScoped<IRuntimeAgentDispatcher>(_ => runtime);
        if (availability is not null)
        {
            services.AddScoped<IAgentExecutionAvailabilityProvider>(_ => availability);
            services.AddScoped<IAgentFirewall>(
                _ => new AgentFirewall(availabilityProvider: availability));
        }
        services.AddScoped<IWorkspaceAgentCatalog>(_ => effectiveCatalog);
        services.AddScoped<IAgentRuntimeProfileResolver>(_ => new RecordingAgentRuntimeProfileResolver(effectiveCatalog.Agents));
        services.AddScoped<IAgentInvocationDispatchFactory, AgentInvocationDispatchFactory>();
        services.AddLogging();
        if (messageSystem is not null)
            services.AddScoped<IMessageSystem>(_ => messageSystem);
        services.AddScoped<ISubmitTurnHandler>(
            _ => submitTurnHandler ?? new RecordingSubmitTurnHandler());
        services.AddScoped<IConversationNotificationStore>(
            _ => notificationStore ?? new RecordingConversationNotificationStore());

        var provider = services.BuildServiceProvider();
        return new MessageDeliveryDispatcher(
            eventBus ?? new RecordingInternalEventBus(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new AgentWakeQueue(NullLogger<AgentWakeQueue>.Instance),
            admissionCoordinator ?? new AgentExecutionAdmissionCoordinator(),
            NullLogger<MessageDeliveryDispatcher>.Instance);
    }

    private static WorkspaceAgentDto Agent(string agentId, string? mainSessionId) =>
        new(
            agentId,
            agentId,
            Description: null,
            DisplayName: agentId,
            AvatarId: null,
            AvatarUrl: null,
            SourceTemplateId: "general-assistant",
            MainSessionId: mainSessionId,
            SystemPromptOverride: null,
            PreferredProviderId: null,
            PreferredModelId: null,
            IsEnabled: true,
            IsFrozen: false,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private static InternalEvent CreateEvent(
        string targetKind,
        string targetId,
        IReadOnlyDictionary<string, string>? metadata = null,
        MessageAddress? from = null,
        string? handlingMode = MessageDeliveryHandlingModes.Execute) =>
        new()
        {
            Type = "message.deliver",
            SessionId = "session-1",
            WorkspaceId = "default",
            Source = new EventSource { SourceType = "message", SourceId = "m1" },
            Payload = new MessageDeliverEventPayload
            {
                MessageId = "m1",
                DeliveryId = "d1",
                WorkspaceId = "default",
                RoomId = "room-default",
                From = from ?? new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner" },
                Target = new MessageAddress { Kind = targetKind, Id = targetId },
                Content = "hello",
                HandlingMode = handlingMode,
                Metadata = metadata ?? new Dictionary<string, string>(),
            },
        };

    private static InternalEvent CreateAvailabilityEvent(string status, string agentId) =>
        new()
        {
            Type = "agent.availability.changed",
            SessionId = "session-1",
            WorkspaceId = "default",
            Source = new EventSource { SourceType = "agent", SourceId = agentId },
            Payload = new AgentAvailabilityChangedEventPayload
            {
                WorkspaceId = "default",
                AgentId = agentId,
                Status = status,
                CurrentExecutionId = status == "idle" ? null : "exec-1",
                CurrentTask = status == "idle" ? null : "running task",
            },
        };

    private static InternalEvent CreateSubAgentResultEvent() =>
        new()
        {
            Type = "message.deliver",
            SessionId = "session-1",
            WorkspaceId = "default",
            Source = new EventSource { SourceType = "message", SourceId = "m-sub" },
            Payload = new MessageDeliverEventPayload
            {
                MessageId = "m-sub",
                DeliveryId = "d-sub",
                WorkspaceId = "default",
                RoomId = "room-default",
                From = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "parent-sub-child" },
                Target = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "agent-b" },
                Content = "subagent result",
                Metadata = new Dictionary<string, string>
                {
                    ["source"] = "subagent",
                    ["intent"] = "subagent_result",
                },
            },
        };

    private sealed class RecordingMessageInbox : IMessageInbox
    {
        private int _claimCount;

        public MessageClaimRequest? LastClaim { get; private set; }
        public int ClaimCount => Volatile.Read(ref _claimCount);
        public int ClaimAttemptCount { get; init; } = 1;
        public int? MaxClaimCount { get; init; }
        public IReadOnlyDictionary<string, string>? ClaimMetadata { get; init; }
        public string ClaimHandlingMode { get; init; } = MessageDeliveryHandlingModes.Execute;
        public string? ClaimConversationId { get; init; }
        public string? ClaimContent { get; init; }
        public MessageAddress? ClaimFrom { get; init; }
        public IReadOnlyList<MessageInboxItem> BatchClaims { get; init; } = [];
        public IReadOnlyList<MessageDeliveryTarget> PendingTargets { get; init; } = [];
        public List<string> PendingTargetKinds { get; } = [];
        public bool RenewLeaseResult { get; init; } = true;
        public List<(string DeliveryId, string ExecutionId, TimeSpan LeaseDuration)> Renewed { get; } = [];
        public List<(string DeliveryId, string ExecutionId)> Acked { get; } = [];
        public List<(string DeliveryId, string ExecutionId, string Error, DateTimeOffset AvailableAt)> Retried { get; } = [];
        public List<(string DeliveryId, string ExecutionId, string Error, DateTimeOffset AvailableAt)> Deferred { get; } = [];
        public List<(string DeliveryId, string ExecutionId, string Error)> DeadLettered { get; } = [];
        public MessageClaimRequest? LastBatchClaim { get; private set; }
        public int LastBatchMax { get; private set; }

        public Task<IReadOnlyList<MessageInboxItem>> ListAsync(MessageInboxQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MessageInboxItem>>([]);

        public Task<IReadOnlyList<MessageDeliveryTarget>> ListPendingTargetsAsync(
            string targetKind,
            CancellationToken ct = default)
        {
            PendingTargetKinds.Add(targetKind);
            return Task.FromResult(PendingTargets);
        }

        public Task<MessageInboxItem?> ClaimNextAsync(MessageClaimRequest request, CancellationToken ct = default)
        {
            LastClaim = request;
            var claimCount = Interlocked.Increment(ref _claimCount);
            if (MaxClaimCount is int maxClaimCount && claimCount > maxClaimCount)
                return Task.FromResult<MessageInboxItem?>(null);

            return Task.FromResult<MessageInboxItem?>(new MessageInboxItem
            {
                DeliveryId = "d1",
                MessageId = "m1",
                WorkspaceId = "default",
                RoomId = "room-default",
                ConversationId = ClaimConversationId,
                From = ClaimFrom ?? new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner" },
                Target = request.Endpoint,
                Content = ClaimContent ?? (ClaimMetadata is null ? "hello" : "subagent result"),
                Status = MessageDeliveryStatuses.Delivering,
                HandlingMode = ClaimHandlingMode,
                Priority = 0,
                AttemptCount = ClaimAttemptCount,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ClaimedByExecutionId = request.ExecutionId,
                Metadata = ClaimMetadata ?? new Dictionary<string, string>(),
            });
        }

        public Task<IReadOnlyList<MessageInboxItem>> ClaimBatchAsync(
            MessageClaimRequest request,
            int maxBatch,
            CancellationToken ct = default)
        {
            LastBatchClaim = request;
            LastBatchMax = maxBatch;
            return Task.FromResult(BatchClaims);
        }

        public Task<bool> RenewLeaseAsync(
            string deliveryId,
            string executionId,
            TimeSpan leaseDuration,
            CancellationToken ct = default)
        {
            Renewed.Add((deliveryId, executionId, leaseDuration));
            return Task.FromResult(RenewLeaseResult);
        }

        public Task<int> RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task AckAsync(string deliveryId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task AckAsync(string deliveryId, string executionId, CancellationToken ct = default)
        {
            Acked.Add((deliveryId, executionId));
            return Task.CompletedTask;
        }

                public Task RetryAsync(string deliveryId, string executionId, string error, DateTimeOffset availableAt, CancellationToken ct = default)
        {
            Retried.Add((deliveryId, executionId, error, availableAt));
            return Task.CompletedTask;
        }

        public Task DeferAsync(string deliveryId, string executionId, string error, CancellationToken ct = default)
        {
            Deferred.Add((deliveryId, executionId, error, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(string deliveryId, string executionId, string error, CancellationToken ct = default)
        {
            DeadLettered.Add((deliveryId, executionId, error));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSubmitTurnHandler : ISubmitTurnHandler
    {
        public List<SubmitTurnCommand> Commands { get; } = [];
        public Exception? Failure { get; init; }

        public Task<AcceptanceResult> HandleAsync(
            SubmitTurnCommand command,
            CancellationToken ct)
        {
            Commands.Add(command);
            if (Failure is not null)
                throw Failure;
            return Task.FromResult(new AcceptanceResult
            {
                ConversationId = command.ConversationId,
                MessageId = command.ClientMessageId,
                TurnIds = ["turn-1"],
                CommandIds = ["command-1"],
                AcceptedSequence = 1,
            });
        }
    }

    private sealed class RecordingConversationNotificationStore
        : IConversationNotificationStore
    {
        public List<ConversationNotificationRequest> Requests { get; } = [];

        public Task<ConversationNotificationResult> AcceptAsync(
            ConversationNotificationRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ConversationNotificationResult(
                request.ConversationId,
                request.MessageId,
                Requests.Count,
                AlreadyAccepted: false));
        }
    }

    private sealed class RecordingRuntimeAgentDispatcher : IRuntimeAgentDispatcher
    {
        public List<RuntimeDispatchRequest> Requests { get; } = [];
        public List<RuntimeDispatchRequest> StreamRequests { get; } = [];
        public IReadOnlyList<ServerSentEventFrame>? StreamFrames { get; set; }
        public RuntimeDispatchResult Result { get; init; } = new()
        {
            SessionId = "session-1",
            AgentInstanceId = "agent-b",
            IsSuccess = true,
            ExecutionState = AgentExecutionState.Completed,
        };

        public Task<RuntimeDispatchResult> DispatchAsync(RuntimeDispatchRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }

        public async IAsyncEnumerable<ServerSentEventFrame> DispatchStreamAsync(
            RuntimeDispatchRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            StreamRequests.Add(request);
            if (StreamFrames is not null)
            {
                foreach (var frame in StreamFrames)
                {
                    yield return frame;
                    await Task.Yield();
                }
                yield break;
            }

            yield return ServerSentEventFrame.Json("delta", new { text = "ok" });
            await Task.Yield();
            yield return ServerSentEventFrame.Json("done", new { ok = true });
        }
    }

    private sealed class BlockingRuntimeAgentDispatcher : IRuntimeAgentDispatcher
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        public Task<RuntimeDispatchResult> DispatchAsync(
            RuntimeDispatchRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ServerSentEventFrame> DispatchStreamAsync(
            RuntimeDispatchRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }

            yield break;
        }
    }

    private sealed class RecordingAgentExecutionAvailabilityProvider(string status) : IAgentExecutionAvailabilityProvider
    {
        public List<(string WorkspaceId, string AgentId)> Requests { get; } = [];

        public Task<AgentExecutionAvailability> GetAsync(string workspaceId, string agentId, CancellationToken ct = default)
        {
            Requests.Add((workspaceId, agentId));
            return Task.FromResult(new AgentExecutionAvailability(
                workspaceId,
                agentId,
                status,
                CurrentExecutionId: status == "idle" ? null : "exec-1",
                CurrentTask: status == "idle" ? null : "running task"));
        }
    }

    private sealed class RecordingWorkspaceAgentCatalog(params WorkspaceAgentDto[] agents) : IWorkspaceAgentCatalog
    {
        public IReadOnlyList<WorkspaceAgentDto> Agents { get; } = agents;

        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(
            string workspaceId,
            CancellationToken ct = default) =>
            Task.FromResult(Agents);
    }

    private sealed class RecordingAgentRuntimeProfileResolver(IReadOnlyList<WorkspaceAgentDto> agents)
        : IAgentRuntimeProfileResolver
    {
        public Task<AgentRuntimeProfile> ResolveAsync(
            string workspaceId,
            string agentId,
            CancellationToken ct = default)
        {
            var agent = agents.FirstOrDefault(item =>
                string.Equals(item.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
            if (agent is null)
                throw new InvalidOperationException($"Agent '{agentId}' was not found in workspace '{workspaceId}'.");

            return Task.FromResult(new AgentRuntimeProfile
            {
                WorkspaceId = workspaceId,
                AgentId = agent.AgentId,
                DisplayName = agent.DisplayName ?? agent.Name,
                MainSessionId = agent.MainSessionId,
                SourceTemplateId = agent.SourceTemplateId,
                PreferredProviderId = "test",
                PreferredModelId = "test-model",
                LlmConfig = new LlmConfig
                {
                    Endpoint = "https://llm.test/v1",
#pragma warning disable CS0618
                    ApiKey = "test-key",
#pragma warning restore CS0618
                    ModelId = "test-model",
                },
            });
        }
    }

    private sealed class RecordingMessageSystem : IMessageSystem
    {
        public List<MessageEnvelope> Sent { get; } = [];
        public Exception? Failure { get; init; }

        public Task<MessageSendResult> SendAsync(MessageEnvelope envelope, CancellationToken ct = default)
        {
            Sent.Add(envelope);
            if (Failure is not null)
                throw Failure;

            return Task.FromResult(new MessageSendResult
            {
                MessageId = envelope.MessageId,
                RoomId = envelope.RoomId,
                DeliveryIds = ["reply-delivery"],
            });
        }
    }

    private sealed class RecordingInternalEventBus : IInternalEventBus
    {
        public List<string> SubscriptionPatterns { get; } = [];

        public Task PublishAsync(InternalEvent evt, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IEventSubscriptionHandle> SubscribeAsync(
            string eventTypePattern,
            Func<InternalEvent, Task> handler,
            CancellationToken ct = default)
        {
            SubscriptionPatterns.Add(eventTypePattern);
            return Task.FromResult<IEventSubscriptionHandle>(new RecordingEventSubscriptionHandle(eventTypePattern));
        }

        public Task UnsubscribeAsync(IEventSubscriptionHandle handle) => Task.CompletedTask;
    }

    private sealed class RecordingEventSubscriptionHandle(string eventTypePattern) : IEventSubscriptionHandle
    {
        public string SubscriptionId { get; } = "sub-1";
        public string EventTypePattern { get; } = eventTypePattern;
        public bool IsActive { get; private set; } = true;
        public void Dispose() => IsActive = false;
    }
}
