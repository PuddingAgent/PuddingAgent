using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
using PuddingPlatform.Services.MessageFabric;
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
    public async Task HandleAsync_SubAgentResultMessage_HandsOffCanonicalConversationTurnAndAcks()
    {
        const string parentSessionId = "parent-conversation-canonical";
        var metadata = SubAgentResultMetadata("run-canonical", parentSessionId);
        var inbox = new RecordingMessageInbox
        {
            ClaimMetadata = metadata,
            ClaimContent = SubAgentResultEnvelopeContent,
            ClaimFrom = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "sub-1" },
            ClaimMessageId = metadata["result_id"],
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var submit = new RecordingSubmitTurnHandler();
        var dispatcher = CreateDispatcher(inbox, runtime, submitTurnHandler: submit);

        await dispatcher.HandleAsync(
            CreateSubAgentResultDeliveryEvent(eventSessionId: "session-1", metadata),
            CancellationToken.None);

        // A01-slice-4b：sub-agent 结果必须经 canonical command/turn 入口受理（受理即 ACK），
        // 不再走 legacy 流式 dispatch。
        Assert.HasCount(1, submit.Commands);
        Assert.AreEqual(parentSessionId, submit.Commands[0].ConversationId);
        Assert.IsEmpty(runtime.StreamRequests);
        Assert.IsEmpty(runtime.Requests);
        Assert.HasCount(1, inbox.Acked);
        Assert.AreEqual("d1", inbox.Acked[0].DeliveryId);
        Assert.IsEmpty(inbox.Deferred);
        Assert.IsEmpty(inbox.Retried);
        Assert.IsEmpty(inbox.DeadLettered);
    }

    [TestMethod]
    public async Task HandleAsync_SubAgentResultMessage_SkipsBackgroundPreemptionAndDefersToCanonical()
    {
        // A01-slice-4b：sub-agent 结果不再走 legacy 后台抢占/延后路径。受理是所有权
        // 转移点，不存在“被前台抢占后 defer”的中间态（该路径已不可达）。
        const string parentSessionId = "parent-conversation-preempt";
        var metadata = SubAgentResultMetadata("run-preempt", parentSessionId);
        var inbox = new RecordingMessageInbox
        {
            ClaimMetadata = metadata,
            ClaimContent = SubAgentResultEnvelopeContent,
            ClaimFrom = new MessageAddress
            {
                Kind = MessageEndpointKinds.Agent,
                Id = "child-agent",
            },
            ClaimMessageId = metadata["result_id"],
            MaxClaimCount = 1,
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var submit = new RecordingSubmitTurnHandler();
        var dispatcher = CreateDispatcher(inbox, runtime, submitTurnHandler: submit);

        await dispatcher.HandleAsync(
            CreateSubAgentResultDeliveryEvent(eventSessionId: "session-1", metadata),
            CancellationToken.None);

        Assert.HasCount(1, submit.Commands);
        Assert.HasCount(1, inbox.Acked);
        Assert.IsEmpty(inbox.Deferred, "sub-agent 结果不再进入 legacy 延后队列");
        Assert.IsEmpty(inbox.Retried);
        Assert.IsEmpty(inbox.DeadLettered);
        Assert.IsEmpty(runtime.StreamRequests);
        Assert.IsEmpty(runtime.Requests);
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
        var submit = new RecordingSubmitTurnHandler();
        var dispatcher = CreateDispatcher(
            inbox,
            runtime,
            submitTurnHandler: submit,
            admissionCoordinator: coordinator);

        // A01-slice-4b：本用例改用非 sub-agent 消息承载“前台占用时后台不取件”断言。
        // 取件前的准入早退（MessageDeliveryDispatcher.cs:327）与 canonical 化互不影响，
        // 而 sub-agent 结果现在会走 canonical 受理，不再适合作为该断言的载体。
        using (coordinator.AcquireForeground("default", "agent-b"))
        {
            await dispatcher.HandleAsync(
                CreateEvent(
                    MessageEndpointKinds.Agent,
                    "agent-b",
                    from: new MessageAddress
                    {
                        Kind = MessageEndpointKinds.System,
                        Id = "system-probe",
                    }),
                CancellationToken.None);
        }

        Assert.IsNull(inbox.LastClaim);
        Assert.IsEmpty(runtime.StreamRequests);
        Assert.IsEmpty(runtime.Requests);
        Assert.IsEmpty(submit.Commands);
    }

    [TestMethod]
    public async Task HandleAsync_SubAgentResultMessage_PersistsCanonicalParentTurnAcceptance()
    {
        const string parentSessionId = "parent-conversation-acceptance";
        var metadata = SubAgentResultMetadata("run-acceptance", parentSessionId);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<PlatformDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton(new RecordingMessageInbox
        {
            ClaimMetadata = metadata,
            ClaimMessageId = metadata["result_id"],
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
        // A01-slice-4b：canonical 受理必须落到真实 ConversationAcceptanceStore，
        // 才能断言 chat_messages / acceptance_batches / chat_execution_commands 的事实。
        services.AddScoped<IConversationAcceptanceStore>(sp => new ConversationAcceptanceStore(
            sp.GetRequiredService<PlatformDbContext>(),
            new NoopCommittedEventSignal(),
            NullLogger<ConversationAcceptanceStore>.Instance));
        services.AddScoped<ISubmitTurnHandler>(sp =>
            new AcceptanceStoreSubmitTurnHandler(sp.GetRequiredService<IConversationAcceptanceStore>()));
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
        var message = await db.ChatMessages.SingleAsync();
        Assert.AreEqual(parentSessionId, message.SessionId, "canonical 受理的用户消息必须落在持久父会话");
        Assert.AreEqual("user", message.Role);
        // A01-slice-4b R19：受理侧为 canonical 用户消息行生成的是**消息级**幂等键
        // （command.ClientMessageId = fabric-subagent-result-message:<sha256(resultId) 前 32 位>，
        // 长 63 字符），而不是 resultId 本身（42 字符）。该 Id 由结果身份确定性派生、
        // 与 delivery 无关，因此断言改为「与 canonical message 幂等键同源」，
        // 而不是「等于 resultId」——后者会错误要求受理侧直接拿 resultId 当消息主键。
        Assert.AreEqual(
            FabricId("fabric-subagent-result-message", metadata["result_id"]),
            message.MessageId,
            "canonical 受理产生的用户消息行必须复用结果身份派生的 message 幂等键");
        Assert.IsFalse(
            message.MessageId.StartsWith("msg-", StringComparison.Ordinal),
            "受理侧不得伪造 msg-* 消息身份");

        var batch = await db.AcceptanceBatches.SingleAsync();
        Assert.AreEqual(parentSessionId, batch.ConversationId);
        Assert.AreEqual(
            FabricId("fabric-subagent-result", metadata["result_id"]),
            batch.ClientRequestId,
            "acceptance 幂等键必须由结果身份派生");

        var command = await db.ChatExecutionCommands.SingleAsync();
        Assert.AreEqual(parentSessionId, command.SessionId);
        Assert.AreEqual("agent-b", command.AgentInstanceId);
        Assert.AreEqual("pending", command.Status);

        var turnAccepted = await db.ConversationEvents
            .Where(e => e.ConversationId == parentSessionId)
            .ToListAsync();
        Assert.HasCount(1, turnAccepted);
        StringAssert.Contains(turnAccepted[0].Type, "accepted");

        var inbox = provider.GetRequiredService<RecordingMessageInbox>();
        Assert.HasCount(1, inbox.Acked);
        var runtime = provider.GetRequiredService<RecordingRuntimeAgentDispatcher>();
        Assert.IsEmpty(runtime.StreamRequests);
        Assert.IsEmpty(runtime.Requests);
    }

    [TestMethod]
    public async Task HandleAsync_ThinkingFrames_PersistCompactV2ThinkingJson()
    {
        var transcript = await PersistTranscriptAsync(
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
        var transcript = await PersistTranscriptAsync(
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

    // ═════════════════════════════════════════════════════════════════════
    // A01-slice-2 · G3 端到端恢复用例（T1~T7）
    // 用户可见语义：事件 session 为空 / 父忙 / 重复投递 / 终态重放，均回到原父会话；
    // 一个 result 只触发一次业务接续。仅新增测试代码，未改任何生产代码。
    // ═════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void A01_T1_SubAgentResultIdentity_SameInput_ProducesSameId()
    {
        const string childRunId = "run-t1";
        const string parentSessionId = "parent-conversation-t1";

        var first = SubAgentResultIdentity.Compute(childRunId, "completed", parentSessionId);
        var second = SubAgentResultIdentity.Compute(childRunId, "completed", parentSessionId);

        Assert.AreEqual(
            first,
            second,
            "同一 (childRunId, 终态, 父会话) 必须派生出相同 id（幂等键不得含时间戳/随机数/Guid）");
        Assert.IsTrue(
            first.StartsWith(SubAgentResultIdentity.Prefix, StringComparison.Ordinal),
            $"确定性 result id 必须以 '{SubAgentResultIdentity.Prefix}' 开头，实际 '{first}'");
        Assert.AreEqual(
            SubAgentResultIdentity.Prefix.Length + 32,
            first.Length,
            "result id 必须是 prefix + 32 位小写 hex");
        Assert.AreEqual(
            first,
            SubAgentResultIdentity.Compute($" {childRunId} ", " completed ", $" {parentSessionId} "),
            "派生前必须规范化（trim）输入，否则崩溃重投会派生不同 id");
    }

    [TestMethod]
    public void A01_T2_SubAgentResultIdentity_TerminalStatusParticipatesInDerivation()
    {
        const string childRunId = "run-t2";
        const string parentSessionId = "parent-conversation-t2";

        var completedId = SubAgentResultIdentity.Compute(childRunId, "completed", parentSessionId);
        var failedId = SubAgentResultIdentity.Compute(childRunId, "failed", parentSessionId);

        Assert.AreNotEqual(
            completedId,
            failedId,
            "终态必须参与派生：running→completed 与 failed→completed 不得互相吞并");
        Assert.AreEqual(
            completedId,
            SubAgentResultIdentity.Compute(childRunId, "Completed", parentSessionId),
            "终态规范化（trim + ToLowerInvariant）后 Completed 与 completed 必须等价");
        Assert.AreEqual(
            "unknown",
            SubAgentResultIdentity.NormalizeTerminalStatus("   "),
            "空白终态必须规范化为 unknown，不得落空字符串");
        Assert.AreNotEqual(
            completedId,
            SubAgentResultIdentity.Compute(childRunId, "completed", "parent-conversation-t2-other"),
            "父会话不同必须派生不同 id，否则兄弟会话的终态结果会互相吞并");
    }

    [TestMethod]
    public async Task A01_T3_SameTerminalResult_PersistedTwice_DedupesByDeterministicMessageId()
    {
        const string parentSessionId = "parent-conversation-t3";
        var resultId = SubAgentResultIdentity.Compute("run-t3", "completed", parentSessionId);
        var plan = SubAgentResultRoutePlan(resultId, parentSessionId, "delivery-t3");

        await using var harness = await MessageFabricHarness.CreateAsync();
        await using var storeDb = harness.CreateContext();
        var store = new MessageFabricStore(storeDb);

        Assert.IsTrue(
            await store.PersistRouteAsync("default", plan, CancellationToken.None),
            "首次持久化子代理终态结果必须真正落库");
        Assert.IsFalse(
            await store.PersistRouteAsync("default", plan, CancellationToken.None),
            "同一确定性 resultId 的重复终态必须命中 MessageId 去重（MessageFabricStore.cs:36-38）");

        await using var assertDb = harness.CreateContext();
        Assert.AreEqual(
            1,
            await assertDb.RoomMessages.CountAsync(),
            "重投同一终态结果后 room_messages 只允许 1 行");
        Assert.AreEqual(
            1,
            await assertDb.MessageDeliveries.CountAsync(),
            "重投同一终态结果后 message_deliveries 只允许 1 行（否则会二次接续父会话）");
    }

    [TestMethod]
    public async Task A01_T4_EventSessionMissing_StillResolvesToPersistedParentSession()
    {
        const string parentSessionId = "parent-conversation-t4";
        var metadata = SubAgentResultMetadata("run-t4", parentSessionId);
        var inbox = new RecordingMessageInbox
        {
            ClaimMetadata = metadata,
            ClaimContent = SubAgentResultEnvelopeContent,
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var submit = new RecordingSubmitTurnHandler();
        var dispatcher = CreateDispatcher(inbox, runtime, submitTurnHandler: submit);

        Assert.AreEqual(
            parentSessionId,
            AgentInvocationDispatchFactory.ResolvePersistedParentConversationId(metadata),
            "持久父身份必须可从 delivery metadata 解析（键优先级 parent_conversation_id→parent_session_id→parent_session→conversation_id）");

        await dispatcher.HandleAsync(
            CreateSubAgentResultDeliveryEvent(eventSessionId: null, metadata),
            CancellationToken.None);

        // A01-slice-4b R15：sub-agent 终态结果改由 canonical 受理（受理即 ACK），
        // “事件 session 为空仍解析到持久父会话”的意图保留，证据面从 legacy 流式请求
        // 迁移到 canonical command 的会话归属。
        Assert.HasCount(1, submit.Commands, "终态结果必须经 canonical command 受理");
        Assert.AreEqual(
            parentSessionId,
            submit.Commands[0].ConversationId,
            "事件 session 为空时仍必须续接持久父会话，不得落 main_session 兜底");
        Assert.AreNotEqual(
            "agent-b-main-session",
            submit.Commands[0].ConversationId,
            "不得退化为绑定主会话（slice-1 不变式 I1）");
        Assert.IsFalse(
            submit.Commands[0].ConversationId!.StartsWith("msg-", StringComparison.Ordinal),
            "不得新建/落到 msg-* 会话");
        Assert.HasCount(1, inbox.Acked, "canonical 受理成功后必须 ACK 原 delivery");
        Assert.IsEmpty(inbox.Retried);
        Assert.IsEmpty(inbox.DeadLettered);
        Assert.IsEmpty(runtime.StreamRequests, "不得回退 legacy 流式路径");
        Assert.IsEmpty(runtime.Requests, "不得回退 legacy 直连路径");
    }

    [TestMethod]
    public async Task A01_T4b_FactoryWithoutExplicitParentParam_ResolvesPersistedParentIdentity()
    {
        const string parentSessionId = "parent-conversation-t4b";
        var logSink = new List<string>();
        var factory = new AgentInvocationDispatchFactory(
            new RecordingAgentRuntimeProfileResolver(
                [Agent("agent-b", mainSessionId: "agent-b-main-session")]),
            new SinkLogger<AgentInvocationDispatchFactory>(
                new SinkLogger(logSink, nameof(AgentInvocationDispatchFactory))));

        var dispatch = await factory.CreateForWorkspaceAgentAsync(
            new WorkspaceAgentInvocation
            {
                WorkspaceId = "default",
                AgentId = "agent-b",
                MessageId = "m-t4b",
                MessageText = "child completed",
                EventSessionId = null,
                ParentConversationId = null,
                Metadata = SubAgentResultMetadata("run-t4b", parentSessionId),
            });

        Assert.IsTrue(dispatch.UsesStreamDispatch, "source=subagent 必须走 sub-agent 续行路径");
        Assert.AreEqual(
            parentSessionId,
            dispatch.Request.SessionId,
            "没有显式父会话参数时必须回退到持久父身份（agent persistence），而不是事件 session 或主会话");
        StringAssert.Contains(
            JoinedLog(logSink),
            "sessionSource=parent_identity");
    }

    [TestMethod]
    public async Task A01_T5_PeriodicRecoveryPath_KeepsParentSessionSource()
    {
        const string parentSessionId = "parent-conversation-t5";
        var resultId = SubAgentResultIdentity.Compute("run-t5", "completed", parentSessionId);
        var plan = SubAgentResultRoutePlan(resultId, parentSessionId, "delivery-t5");

        await using var harness = await MessageFabricHarness.CreateAsync();
        await using var storeDb = harness.CreateContext();
        var store = new MessageFabricStore(storeDb);
        Assert.IsTrue(await store.PersistRouteAsync("default", plan, CancellationToken.None));

        var runtime = new RecordingRuntimeAgentDispatcher();
        var logSink = new List<string>();
        await using var provider = CreateDispatcherProvider(
            store, runtime, harness.ConnectionString, logSink, useAcceptanceStore: true);
        var dispatcher = CreateDispatcherFromProvider(provider);

        // periodic-recovery 形态：sessionId: null / metadata: null
        // （MessageDeliveryDispatcher.cs:1993-2005 的 TryDispatchKnownTargetsAsync）。
        await dispatcher.RunRecoveryPassOnceAsync(CancellationToken.None);

        // A01-slice-4b R16：恢复路径必须走 canonical 受理面。
        // 断言受理面事实（acceptance/command/消息行 + 原 delivery 已 ACK），
        // 而不是仅把 legacy 流式断言置空。
        await using var assertDb = harness.CreateContext();
        var batch = await assertDb.AcceptanceBatches.SingleAsync();
        Assert.AreEqual(parentSessionId, batch.ConversationId, "恢复路径的 acceptance 必须落在持久父会话");
        Assert.AreEqual(
            FabricId("fabric-subagent-result", resultId),
            batch.ClientRequestId,
            "恢复路径的 acceptance 幂等键必须由结果身份派生");
        var command = await assertDb.ChatExecutionCommands.SingleAsync();
        Assert.AreEqual(parentSessionId, command.SessionId, "恢复路径必须产生落持久父会话的 canonical command");
        Assert.IsFalse(
            command.SessionId.StartsWith("msg-", StringComparison.Ordinal),
            "恢复路径不得落到 msg-* 会话");
        var persistedMessage = await assertDb.ChatMessages.SingleAsync();
        Assert.AreEqual(parentSessionId, persistedMessage.SessionId, "恢复路径的用户消息必须落在持久父会话");
        var delivery = await assertDb.MessageDeliveries.SingleAsync();
        Assert.AreEqual(
            MessageDeliveryStatuses.Delivered,
            delivery.Status,
            "恢复路径受理成功后必须 ACK 原 delivery（持久事实，等价于 inbox.Acked==1）");

        Assert.IsFalse(
            JoinedLog(logSink).Contains("sessionSource=main_session", StringComparison.Ordinal),
            "恢复路径不得退化为 sessionSource=main_session");
        Assert.IsEmpty(runtime.StreamRequests, "恢复路径不得回退 legacy 流式路径");
        Assert.IsEmpty(runtime.Requests, "恢复路径不得回退 legacy 直连路径");
    }

    [TestMethod]
    public async Task A01_T6_ReplayedTerminalResult_TriggersSingleParentContinuation()
    {
        const string parentSessionId = "parent-conversation-t6";
        var resultId = SubAgentResultIdentity.Compute("run-t6", "completed", parentSessionId);
        var plan = SubAgentResultRoutePlan(resultId, parentSessionId, "delivery-t6");

        await using var harness = await MessageFabricHarness.CreateAsync();
        await using var storeDb = harness.CreateContext();
        var store = new MessageFabricStore(storeDb);
        Assert.IsTrue(await store.PersistRouteAsync("default", plan, CancellationToken.None));

        var runtime = new RecordingRuntimeAgentDispatcher();
        await using var provider = CreateDispatcherProvider(
            store, runtime, harness.ConnectionString, useAcceptanceStore: true);
        var dispatcher = CreateDispatcherFromProvider(provider);

        await dispatcher.RunRecoveryPassOnceAsync(CancellationToken.None);

        // A01-slice-4b R17：重放语义同样以幂等键为证据。
        // 首次投递必须产生且只产生一次 canonical 受理。
        await using (var firstDb = harness.CreateContext())
        {
            Assert.AreEqual(1, await firstDb.AcceptanceBatches.CountAsync(), "首次投递必须产生 1 个 acceptance batch");
            Assert.AreEqual(1, await firstDb.ChatExecutionCommands.CountAsync(), "首次投递必须产生 1 个 canonical command");
        }

        // ACK 前崩溃 → 同一终态 result 被重放：确定性 MessageId 必须命中去重，不产生第二条投递。
        Assert.IsFalse(
            await store.PersistRouteAsync("default", plan, CancellationToken.None),
            "重放的同一终态结果必须命中去重，不得新增投递");
        await dispatcher.RunRecoveryPassOnceAsync(CancellationToken.None);

        await using var assertDb = harness.CreateContext();
        Assert.AreEqual(
            1,
            await assertDb.AcceptanceBatches.CountAsync(),
            "一个 result 只允许一个 acceptance batch（重放不得新增）");
        Assert.AreEqual(
            1,
            await assertDb.ChatExecutionCommands.CountAsync(),
            "一个 result 只允许触发一次父级业务接续（重放不得新增 command）");
        Assert.AreEqual(1, await assertDb.ChatMessages.CountAsync(), "重放不得新增 chat_messages");
        Assert.AreEqual(1, await assertDb.MessageDeliveries.CountAsync(), "重放不得新增 message_deliveries");
        var replayedDelivery = await assertDb.MessageDeliveries.SingleAsync();
        Assert.AreEqual(
            MessageDeliveryStatuses.Delivered,
            replayedDelivery.Status,
            "一个 result 只允许 ACK 一次（持久事实，等价于 inbox.Acked==1）");
        Assert.AreEqual(1, await assertDb.RoomMessages.CountAsync(), "重放不得新增 room_messages");
        Assert.IsEmpty(runtime.StreamRequests, "不得回退 legacy 流式路径");
        Assert.IsEmpty(runtime.Requests, "不得回退 legacy 直连路径");
    }

    [TestMethod]
    public async Task A01_T7_BusyParent_SubAgentResultAcceptedWithoutFakeCompletion()
    {
        // A01-slice-4b R18：canonical 化后“父忙 → Deferred”不再可表达。
        // 语义迁移理由：旧语义（MessageDeliveryDispatcher.cs:831-857）是在
        // legacy 流式派发中观察到 Agent Busy 错误帧后把 delivery 放回延后队列；
        // 现在 sub-agent 终态结果在 :498-512 就进入 canonical 受理面，
        // 声明的所有权在受理那一刻转移，父忙由 command 租约/attempt 承担（R10 边界），
        // 因此「受理即 ACK + command 落持久父会话」才是正确的可观察结果。
        const string parentSessionId = "parent-conversation-t7";
        var metadata = SubAgentResultMetadata("run-t7", parentSessionId);
        var inbox = new RecordingMessageInbox
        {
            ClaimAttemptCount = 3,
            ClaimMetadata = metadata,
            ClaimContent = SubAgentResultEnvelopeContent,
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
        var submit = new RecordingSubmitTurnHandler();
        var dispatcher = CreateDispatcher(inbox, runtime, submitTurnHandler: submit);

        await dispatcher.HandleAsync(
            CreateSubAgentResultDeliveryEvent(eventSessionId: "session-1", metadata),
            CancellationToken.None);

        Assert.HasCount(1, inbox.Acked, "canonical 受理即 ACK，不得因父忙而延后或丢弃终态结果");
        Assert.HasCount(1, submit.Commands, "父忙必须由 canonical command 租约/attempt 承担，而不是 deferred 队列");
        Assert.AreEqual(
            parentSessionId,
            submit.Commands[0].ConversationId,
            "接续 Turn 必须落在持久父会话，而不是回退绑定主会话");
        Assert.AreNotEqual("agent-b-main-session", submit.Commands[0].ConversationId);
        Assert.IsFalse(
            submit.Commands[0].ConversationId!.StartsWith("msg-", StringComparison.Ordinal),
            "父忙场景也绝不落 msg-* 会话");
        Assert.IsEmpty(inbox.Deferred, "受理后再无 legacy deferred 中间态");
        Assert.IsEmpty(inbox.Retried, "父忙不是失败，不得进入 retry");
        Assert.IsEmpty(inbox.DeadLettered, "父忙不是失败，不得死信");
        Assert.IsEmpty(runtime.StreamRequests, "不得回退 legacy 流式路径");
        Assert.IsEmpty(runtime.Requests, "不得回退 legacy 直连路径");
    }

    // ═════════════════════════════════════════════════════════════════════
    // A01-slice-4b · canonical 受理用例（T8~T12）
    // 用户可见语义：一个 result 只触发一次业务接续，接续 Turn 落在持久父会话，
    // 且不再回退到 legacy 流式 dispatch / agent main session / msg-* 会话。
    // ═════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task A01_T8_SubAgentResult_HandsOffCanonicalTurnAndAcks()
    {
        const string parentSessionId = "parent-conversation-t8";
        var metadata = SubAgentResultMetadata("run-t8", parentSessionId);
        var resultId = metadata["result_id"];
        var inbox = new RecordingMessageInbox
        {
            ClaimMetadata = metadata,
            ClaimContent = SubAgentResultEnvelopeContent,
            ClaimFrom = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "sub-1" },
            ClaimMessageId = resultId,
            ClaimDeliveryId = "delivery-t8",
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var submit = new RecordingSubmitTurnHandler();
        var dispatcher = CreateDispatcher(inbox, runtime, submitTurnHandler: submit);

        await dispatcher.HandleAsync(
            CreateSubAgentResultDeliveryEvent(eventSessionId: "session-1", metadata),
            CancellationToken.None);

        Assert.HasCount(1, submit.Commands);
        var command = submit.Commands[0];
        Assert.AreEqual(parentSessionId, command.ConversationId, "接续 Turn 必须落在持久父会话");
        Assert.AreNotEqual("agent-b-main-session", command.ConversationId);
        Assert.AreEqual(FabricId("fabric-subagent-result", resultId), command.ClientRequestId);
        Assert.AreEqual(FabricId("fabric-subagent-result-message", resultId), command.ClientMessageId);
        Assert.IsFalse(
            command.ClientRequestId.Contains("delivery-t8", StringComparison.Ordinal),
            "幂等键必须由结果身份派生，不得包含 delivery 身份");
        Assert.AreNotEqual(
            FabricId("fabric-turn-request", "delivery-t8"),
            command.ClientRequestId,
            "不得再用 delivery 级幂等键受理 sub-agent 结果");
        Assert.HasCount(1, inbox.Acked);
        Assert.IsEmpty(runtime.StreamRequests);
        Assert.IsEmpty(runtime.Requests);
    }

    [TestMethod]
    public async Task A01_T9_SubAgentResult_DeliveryRebuilt_StillSingleCanonicalCommand()
    {
        const string parentSessionId = "parent-conversation-t9";
        var metadata = SubAgentResultMetadata("run-t9", parentSessionId);
        var resultId = metadata["result_id"];
        await using var harness = await MessageFabricHarness.CreateAsync();

        await DispatchWithRebuiltDeliveryAsync(harness, metadata, resultId, "delivery-t9-a", parentSessionId);
        await DispatchWithRebuiltDeliveryAsync(harness, metadata, resultId, "delivery-t9-b", parentSessionId);

        await using var db = harness.CreateContext();
        Assert.AreEqual(
            1,
            await db.AcceptanceBatches.CountAsync(),
            "同一 result 重建 delivery 后 acceptance 必须幂等命中，只允许 1 个 batch");
        Assert.AreEqual(
            1,
            await db.ChatExecutionCommands.CountAsync(),
            "同一 result 重建 delivery 后只允许 1 个 canonical command（否则父级被接续两次）");
        Assert.AreEqual(1, await db.ChatMessages.CountAsync());
    }

    [TestMethod]
    public async Task A01_T10_SubAgentResult_NoMainSession_DeadLettersWithoutMsgStarConversation()
    {
        // 无持久父身份且 agent 无绑定主会话：必须失败可观察（retry/dead-letter），
        // 既不伪造 msg-* 会话，也不静默回退 main session。
        var inbox = new RecordingMessageInbox
        {
            ClaimMetadata = new Dictionary<string, string>
            {
                ["source"] = "subagent",
                ["intent"] = "subagent_result",
            },
            ClaimContent = SubAgentResultEnvelopeContent,
            ClaimFrom = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "sub-1" },
            ClaimMessageId = "subresult:t10-no-parent",
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var submit = new RecordingSubmitTurnHandler();
        var dispatcher = CreateDispatcher(
            inbox,
            runtime,
            submitTurnHandler: submit,
            catalog: new RecordingWorkspaceAgentCatalog(Agent("agent-b", mainSessionId: null)));

        await dispatcher.HandleAsync(CreateSubAgentResultEvent(), CancellationToken.None);

        Assert.IsNotNull(inbox.LastClaim);
        Assert.IsEmpty(submit.Commands, "受理失败不得产生 canonical command");
        Assert.IsEmpty(inbox.Acked, "受理成功前不得 ACK");
        Assert.IsTrue(
            inbox.Retried.Count + inbox.DeadLettered.Count > 0,
            "无法解析父会话必须进入 retry/dead-letter，不得静默丢弃");
        Assert.IsEmpty(runtime.StreamRequests);
        Assert.IsEmpty(runtime.Requests);
    }

    [TestMethod]
    public async Task A01_T11_SubAgentResult_EventSessionMissing_TargetsPersistedParentSession()
    {
        const string parentSessionId = "parent-conversation-t11";
        var metadata = SubAgentResultMetadata("run-t11", parentSessionId);
        var inbox = new RecordingMessageInbox
        {
            ClaimContent = SubAgentResultEnvelopeContent,
            ClaimFrom = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "sub-1" },
            ClaimMessageId = metadata["result_id"],
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var submit = new RecordingSubmitTurnHandler();
        var dispatcher = CreateDispatcher(inbox, runtime, submitTurnHandler: submit);

        await dispatcher.HandleAsync(
            CreateSubAgentResultDeliveryEvent(eventSessionId: null, metadata),
            CancellationToken.None);

        Assert.HasCount(1, submit.Commands);
        Assert.AreEqual(
            parentSessionId,
            submit.Commands[0].ConversationId,
            "事件 session 缺失时必须回落到持久父身份");
        Assert.AreNotEqual("session-1", submit.Commands[0].ConversationId);
        Assert.IsFalse(
            submit.Commands[0].ConversationId.StartsWith("msg-", StringComparison.Ordinal),
            "绝不落 msg-* 会话");
        Assert.HasCount(1, inbox.Acked);
    }

    [TestMethod]
    public async Task A01_T12_SubAgentResult_DoesNotFallBackToLegacyStreamDispatch()
    {
        const string parentSessionId = "parent-conversation-t12";
        var metadata = SubAgentResultMetadata("run-t12", parentSessionId);
        var inbox = new RecordingMessageInbox
        {
            ClaimMetadata = metadata,
            ClaimContent = SubAgentResultEnvelopeContent,
            ClaimFrom = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "sub-1" },
            ClaimMessageId = metadata["result_id"],
        };
        var runtime = new RecordingRuntimeAgentDispatcher();
        var submit = new RecordingSubmitTurnHandler();
        var dispatcher = CreateDispatcher(inbox, runtime, submitTurnHandler: submit);

        await dispatcher.HandleAsync(
            CreateSubAgentResultDeliveryEvent(eventSessionId: "session-1", metadata),
            CancellationToken.None);

        Assert.IsEmpty(runtime.StreamRequests, "不得回退 legacy 流式 dispatch");
        Assert.IsEmpty(runtime.Requests, "不得回退 legacy 直接 dispatch");
        Assert.HasCount(1, submit.Commands, "只能有一个 canonical 入口");
        Assert.AreEqual(
            "true",
            submit.Commands[0].Metadata![MessageFabricTurnMetadata.IsIngress],
            "受理必须走受信 Message Fabric ingress 信封");
        Assert.AreEqual(parentSessionId, submit.Commands[0].ConversationId);
    }

    /// <summary>A01-slice-4b：以同一 resultId 重建新 delivery 行，验证两层幂等键同源。</summary>
    private static async Task DispatchWithRebuiltDeliveryAsync(
        MessageFabricHarness harness,
        IReadOnlyDictionary<string, string> metadata,
        string resultId,
        string deliveryId,
        string parentSessionId)
    {
        var inbox = new RecordingMessageInbox
        {
            ClaimMetadata = metadata,
            ClaimContent = SubAgentResultEnvelopeContent,
            ClaimFrom = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "sub-1" },
            ClaimMessageId = resultId,
            ClaimDeliveryId = deliveryId,
        };
        await using var provider = CreateDispatcherProvider(
            inbox,
            new RecordingRuntimeAgentDispatcher(),
            harness.ConnectionString,
            useAcceptanceStore: true);
        var dispatcher = CreateDispatcherFromProvider(provider);

        await dispatcher.HandleAsync(
            CreateSubAgentResultDeliveryEvent(eventSessionId: null, metadata),
            CancellationToken.None);

        Assert.HasCount(1, inbox.Acked);
        Assert.IsNotNull(inbox.LastClaimedItem);
        Assert.AreEqual(resultId, inbox.LastClaimedItem!.MessageId);
        Assert.AreEqual(deliveryId, inbox.LastClaimedItem!.DeliveryId);
        Assert.AreEqual(
            parentSessionId,
            AgentInvocationDispatchFactory.ResolvePersistedParentConversationId(metadata),
            "接续 Turn 的会话归属必须来自持久父身份");
    }

    private const string SubAgentResultEnvelopeContent = """
    {
      "schema": "pudding-message",
      "version": 1,
      "message_id": "msg-sub-result",
      "message_type": "subagent_result",
      "from": { "kind": "agent", "id": "sub-1", "display_name": "Sub Agent" },
      "to": [{ "kind": "agent", "id": "agent-b" }],
      "context": { "format": "text/markdown", "text": "child completed" }
    }
    """;

    private static Dictionary<string, string> SubAgentResultMetadata(
        string childRunId,
        string parentSessionId,
        string terminalStatus = "completed") =>
        new()
        {
            ["source"] = "subagent",
            ["intent"] = "subagent_result",
            ["parent_session"] = parentSessionId,
            ["parent_agent"] = "agent-b",
            ["child_run_id"] = childRunId,
            ["result_id"] = SubAgentResultIdentity.Compute(childRunId, terminalStatus, parentSessionId),
        };

    private static MessageRoutePlan SubAgentResultRoutePlan(
        string resultId,
        string parentSessionId,
        string deliveryId) =>
        new()
        {
            MessageId = resultId,
            RoomMessage = new RoomMessageDraft
            {
                RoomId = "room-default",
                MessageId = resultId,
                From = new MessageAddress
                {
                    Kind = MessageEndpointKinds.Agent,
                    Id = "sub-1",
                    WorkspaceId = "default",
                    DisplayName = "Sub Agent",
                },
                Audience = MessageAudiences.Direct,
                Visibility = MessageVisibilities.System,
                Content = SubAgentResultEnvelopeContent,
                ConversationId = parentSessionId,
                CreatedAt = 1_700_000_000_000,
                Metadata = SubAgentResultMetadata("run-route", parentSessionId),
            },
            Deliveries =
            [
                new MessageDeliveryDraft
                {
                    DeliveryId = deliveryId,
                    MessageId = resultId,
                    Target = new MessageAddress
                    {
                        Kind = MessageEndpointKinds.Agent,
                        Id = "agent-b",
                        WorkspaceId = "default",
                    },
                    Priority = 5,
                },
            ],
        };

    private static InternalEvent CreateSubAgentResultDeliveryEvent(
        string? eventSessionId,
        IReadOnlyDictionary<string, string> metadata) =>
        new()
        {
            Type = "message.deliver",
            SessionId = eventSessionId,
            WorkspaceId = "default",
            Source = new EventSource { SourceType = "message", SourceId = "m-sub-result" },
            Payload = new MessageDeliverEventPayload
            {
                MessageId = "m-sub-result",
                DeliveryId = "d-sub-result",
                WorkspaceId = "default",
                RoomId = "room-default",
                From = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "sub-1" },
                Target = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "agent-b" },
                Content = SubAgentResultEnvelopeContent,
                HandlingMode = MessageDeliveryHandlingModes.Execute,
                Metadata = metadata,
            },
        };

    private static ServiceProvider CreateDispatcherProvider(
        IMessageInbox inbox,
        IRuntimeAgentDispatcher runtime,
        string? connectionString = null,
        List<string>? logSink = null,
        string? mainSessionId = "agent-b-main-session",
        bool useAcceptanceStore = false)
    {
        var services = new ServiceCollection();
        var catalog = new RecordingWorkspaceAgentCatalog(
            Agent("agent-b", mainSessionId: mainSessionId));
        services.AddScoped<IMessageInbox>(_ => inbox);
        services.AddScoped<IRuntimeAgentDispatcher>(_ => runtime);
        services.AddScoped<IWorkspaceAgentCatalog>(_ => catalog);
        services.AddScoped<IAgentRuntimeProfileResolver>(
            _ => new RecordingAgentRuntimeProfileResolver(catalog.Agents));
        services.AddScoped<IAgentInvocationDispatchFactory, AgentInvocationDispatchFactory>();
        if (useAcceptanceStore)
        {
            services.AddScoped<IConversationAcceptanceStore>(sp => new ConversationAcceptanceStore(
                sp.GetRequiredService<PlatformDbContext>(),
                new NoopCommittedEventSignal(),
                NullLogger<ConversationAcceptanceStore>.Instance));
            services.AddScoped<ISubmitTurnHandler>(sp => new AcceptanceStoreSubmitTurnHandler(
                sp.GetRequiredService<IConversationAcceptanceStore>()));
        }
        else
        {
            services.AddScoped<ISubmitTurnHandler>(_ => new RecordingSubmitTurnHandler());
        }

        services.AddScoped<IConversationNotificationStore>(_ => new RecordingConversationNotificationStore());
        if (connectionString is not null)
            services.AddDbContext<PlatformDbContext>(options => options.UseSqlite(connectionString));
        if (logSink is not null)
            services.AddLogging(builder => builder.AddProvider(new SinkLoggerProvider(logSink)));
        else
            services.AddLogging();
        return services.BuildServiceProvider();
    }

    private static MessageDeliveryDispatcher CreateDispatcherFromProvider(IServiceProvider provider) =>
        new(
            new RecordingInternalEventBus(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new AgentWakeQueue(NullLogger<AgentWakeQueue>.Instance),
            new AgentExecutionAdmissionCoordinator(),
            NullLogger<MessageDeliveryDispatcher>.Instance);

    private static string JoinedLog(List<string> logSink)
    {
        lock (logSink)
            return string.Join("\n", logSink);
    }

    /// <summary>A01-slice-4b：镜像 MessageDeliveryDispatcher.StableMessageFabricId 的确定性 id 口径。</summary>
    private static string FabricId(string prefix, string value)
    {
        var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"{prefix}\n{value}")))
            .ToLowerInvariant();
        return $"{prefix}:{hash[..32]}";
    }

    /// <summary>A01-slice-4b：把 canonical 受理直接落到真实 ConversationAcceptanceStore，
    /// 用于断言 command/turn/batch 的持久化事实（不新增生产依赖）。</summary>
    private sealed class AcceptanceStoreSubmitTurnHandler(IConversationAcceptanceStore store)
        : ISubmitTurnHandler
    {
        public Task<AcceptanceResult> HandleAsync(SubmitTurnCommand command, CancellationToken ct) =>
            store.AcceptBatchAsync(
                new SubmitTurnRequest
                {
                    ClientRequestId = command.ClientRequestId,
                    ClientMessageId = command.ClientMessageId,
                    Recipients = command.Recipients,
                    Content = command.Content,
                    Metadata = command.Metadata,
                },
                command.WorkspaceId,
                command.ConversationId,
                command.UserId,
                ct);
    }

    private sealed class NoopCommittedEventSignal : ICommittedEventSignal
    {
        public ValueTask WaitForChangeAsync(string conversationId, long knownHead, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public void Signal(string conversationId, long committedThroughSequence)
        {
        }
    }

    private sealed class SinkLogger(List<string> sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = $"{category}|{logLevel}|{formatter(state, exception)}";
            lock (sink)
                sink.Add(line);
        }
    }

    private sealed class SinkLogger<TCategory>(ILogger inner) : ILogger<TCategory>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class SinkLoggerProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new SinkLogger(sink, categoryName);

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// 共享缓存内存 SQLite 夹具：每个 PlatformDbContext 使用独立连接（避免单连接并发），
    /// keep-alive 连接保证内存库在测试期间存活。库名中的 Guid 仅用于测试隔离，
    /// 不参与任何幂等键派生。
    /// </summary>
    private sealed class MessageFabricHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _keepAlive;

        private MessageFabricHarness(string connectionString, SqliteConnection keepAlive)
        {
            ConnectionString = connectionString;
            _keepAlive = keepAlive;
        }

        public string ConnectionString { get; }

        public static async Task<MessageFabricHarness> CreateAsync()
        {
            var connectionString =
                $"Data Source=a01slice2_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var keepAlive = new SqliteConnection(connectionString);
            await keepAlive.OpenAsync();
            var harness = new MessageFabricHarness(connectionString, keepAlive);
            await using (var db = harness.CreateContext())
                await db.Database.EnsureCreatedAsync();
            return harness;
        }

        public PlatformDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(ConnectionString)
                .Options);

        public async ValueTask DisposeAsync() => await _keepAlive.DisposeAsync();
    }

    /// <summary>
    /// A01-slice-4b R14：本夹具验证的是 <see cref="ChatTranscriptWriter"/> 的写侧 thinking 转录，
    /// 与 canonical 受理无关。载具改为非 sub-agent（普通用户消息 + 空 metadata），
    /// 以继续走 legacy 流式路径（MessageDeliveryDispatcher.cs:683）；断言未做任何削弱。
    /// </summary>
    private static async Task<ChatMessageEntity> PersistTranscriptAsync(
        IReadOnlyList<ServerSentEventFrame> frames)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<PlatformDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton(new RecordingMessageInbox
        {
            // A01-slice-4b R14：非 sub-agent 载具。
            ClaimContent = "please continue",
        });
        services.AddSingleton(new RecordingRuntimeAgentDispatcher { StreamFrames = frames });
        services.AddScoped<IMessageInbox>(sp => sp.GetRequiredService<RecordingMessageInbox>());
        services.AddScoped<IRuntimeAgentDispatcher>(sp => sp.GetRequiredService<RecordingRuntimeAgentDispatcher>());
        // A01-slice-4b R21：用户载具（metadata 为空）走非 stream dispatch，会话身份取自
        // profile.MainSessionId（AgentInvocationDispatchFactory.cs:69-83），不再是事件 session。
        // 因此把绑定主会话对齐为转录断言会话 session-1；断言本身保持不变。
        services.AddScoped<IWorkspaceAgentCatalog>(_ => new RecordingWorkspaceAgentCatalog(
            Agent("agent-b", mainSessionId: "session-1")));
        services.AddScoped<IAgentRuntimeProfileResolver>(_ => new RecordingAgentRuntimeProfileResolver(
            [Agent("agent-b", mainSessionId: "session-1")]));
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

        await dispatcher.HandleAsync(CreateTranscriptCarrierEvent(), CancellationToken.None);

        await using var assertScope = provider.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await db.ChatMessages.SingleAsync(m => m.SessionId == "session-1" && m.Role == "agent");
    }

        private static MessageDeliveryDispatcher CreateDispatcher(
        IMessageInbox inbox,
        IRuntimeAgentDispatcher runtime,
        RecordingAgentExecutionAvailabilityProvider? availability = null,
        RecordingInternalEventBus? eventBus = null,
        RecordingWorkspaceAgentCatalog? catalog = null,
        RecordingMessageSystem? messageSystem = null,
        RecordingSubmitTurnHandler? submitTurnHandler = null,
        RecordingConversationNotificationStore? notificationStore = null,
        AgentExecutionAdmissionCoordinator? admissionCoordinator = null,
        List<string>? logSink = null)
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
        if (logSink is not null)
            services.AddLogging(builder => builder.AddProvider(new SinkLoggerProvider(logSink)));
        else
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

    /// <summary>
    /// A01-slice-4b R13/R14：非 sub-agent 载具事件（用户 → agent），驱动 legacy 流式转录。
    /// metadata 必须为空：intent / fabric turn / gateway ingress 任一键都会把消息
    /// 拉回 canonical 受理，从而不再产生任何 ChatMessages 行。
    /// </summary>
    private static InternalEvent CreateTranscriptCarrierEvent() =>
        new()
        {
            Type = "message.deliver",
            SessionId = "session-1",
            WorkspaceId = "default",
            Source = new EventSource { SourceType = "message", SourceId = "m-transcript" },
            Payload = new MessageDeliverEventPayload
            {
                MessageId = "m-transcript",
                DeliveryId = "d-transcript",
                WorkspaceId = "default",
                RoomId = "room-default",
                From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner" },
                Target = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "agent-b" },
                Content = "please continue",
                HandlingMode = MessageDeliveryHandlingModes.Execute,
                Metadata = new Dictionary<string, string>(),
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
        // A01-slice-4b：claimed 的 message/delivery 身份在返回项上（入参 MessageClaimRequest 不含 MessageId）。
        public MessageInboxItem? LastClaimedItem { get; private set; }
        public int ClaimCount => Volatile.Read(ref _claimCount);
        public int ClaimAttemptCount { get; init; } = 1;
        public int? MaxClaimCount { get; init; }
        public IReadOnlyDictionary<string, string>? ClaimMetadata { get; init; }
        public string ClaimHandlingMode { get; init; } = MessageDeliveryHandlingModes.Execute;
        public string? ClaimConversationId { get; init; }
        public string? ClaimContent { get; init; }
        // A01-slice-4b：canonical 受理身份断言需要可控的 message/delivery 身份。
        public string ClaimMessageId { get; init; } = "m1";
        public string ClaimDeliveryId { get; init; } = "d1";
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

            LastClaimedItem = new MessageInboxItem
            {
                DeliveryId = ClaimDeliveryId,
                MessageId = ClaimMessageId,
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
            };
            return Task.FromResult<MessageInboxItem?>(LastClaimedItem);
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
