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
        var logSink = new List<string>();
        var dispatcher = CreateDispatcher(inbox, runtime, logSink: logSink);

        Assert.AreEqual(
            parentSessionId,
            AgentInvocationDispatchFactory.ResolvePersistedParentConversationId(metadata),
            "持久父身份必须可从 delivery metadata 解析（键优先级 parent_conversation_id→parent_session_id→parent_session→conversation_id）");

        await dispatcher.HandleAsync(
            CreateSubAgentResultDeliveryEvent(eventSessionId: null, metadata),
            CancellationToken.None);

        Assert.AreEqual(1, runtime.StreamRequests.Count, "终态结果必须走 sub-agent 续行流式路径");
        Assert.AreEqual(
            parentSessionId,
            runtime.StreamRequests[0].SessionId,
            "事件 session 为空时仍必须续接持久父会话，不得落 main_session 兜底");
        Assert.AreNotEqual(
            "agent-b-main-session",
            runtime.StreamRequests[0].SessionId,
            "不得退化为绑定主会话（slice-1 不变式 I1）");
        Assert.IsFalse(
            runtime.StreamRequests[0].SessionId!.StartsWith("msg-", StringComparison.Ordinal),
            "不得新建/落到 msg-* 会话");

        var logs = JoinedLog(logSink);
        // 注：slice-2 在 MessageDeliveryDispatcher.cs:667-672 已把持久父身份显式传入
        // ParentConversationId，因此解析来源固定为 parent_param（优先级最高）；
        // parent_identity 分支由 A01_T4b 直接覆盖 AgentInvocationDispatchFactory。
        StringAssert.Contains(logs, "sessionSource=parent_param");
        Assert.IsFalse(
            logs.Contains("sessionSource=main_session", StringComparison.Ordinal),
            "事件 session 为空时必须从持久父身份续行，绝不能出现 sessionSource=main_session");
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
        await using var provider = CreateDispatcherProvider(store, runtime, harness.ConnectionString, logSink);
        var dispatcher = CreateDispatcherFromProvider(provider);

        // periodic-recovery 形态：sessionId: null / metadata: null
        // （MessageDeliveryDispatcher.cs:1993-2005 的 TryDispatchKnownTargetsAsync）。
        await dispatcher.RunRecoveryPassOnceAsync(CancellationToken.None);

        Assert.AreEqual(1, runtime.StreamRequests.Count, "恢复路径必须接续执行，不得静默丢弃已持久化的终态结果");
        Assert.AreEqual(
            parentSessionId,
            runtime.StreamRequests[0].SessionId,
            "恢复路径必须回到原父会话");
        var logs = JoinedLog(logSink);
        StringAssert.Contains(logs, "sessionSource=parent_param");
        Assert.IsFalse(
            logs.Contains("sessionSource=main_session", StringComparison.Ordinal),
            "恢复路径不得退化为 sessionSource=main_session");
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
        await using var provider = CreateDispatcherProvider(store, runtime, harness.ConnectionString);
        var dispatcher = CreateDispatcherFromProvider(provider);

        await dispatcher.RunRecoveryPassOnceAsync(CancellationToken.None);
        Assert.AreEqual(1, runtime.StreamRequests.Count, "首次投递必须触发一次父级接续");

        // ACK 前崩溃 → 同一终态 result 被重放：确定性 MessageId 必须命中去重，不产生第二条投递。
        Assert.IsFalse(
            await store.PersistRouteAsync("default", plan, CancellationToken.None),
            "重放的同一终态结果必须命中去重，不得新增投递");
        await dispatcher.RunRecoveryPassOnceAsync(CancellationToken.None);

        Assert.AreEqual(
            1,
            runtime.StreamRequests.Count,
            "一个 result 只允许触发一次父级业务接续");
        Assert.AreEqual(1, await storeDb.RoomMessages.CountAsync(), "重放不得新增 room_messages");
        Assert.AreEqual(1, await storeDb.MessageDeliveries.CountAsync(), "重放不得新增 message_deliveries");
    }

    [TestMethod]
    public async Task A01_T7_BusyParent_SubAgentResultDeferredWithoutFakeCompletion()
    {
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
        var dispatcher = CreateDispatcher(inbox, runtime);

        await dispatcher.HandleAsync(
            CreateSubAgentResultDeliveryEvent(eventSessionId: "session-1", metadata),
            CancellationToken.None);

        Assert.AreEqual(1, inbox.Deferred.Count, "父忙时终态结果必须延后排队（MessageDeliveryDispatcher.cs:831-857）");
        Assert.AreEqual(0, inbox.Acked.Count, "父忙不得 ACK，否则终态结果丢失");
        Assert.AreEqual(0, inbox.DeadLettered.Count, "父忙不是失败，不得死信");
        Assert.AreEqual(0, inbox.Retried.Count, "父忙是排队而非失败退避，不得进入 retry");
        Assert.AreEqual(1, runtime.StreamRequests.Count, "延后前必须已把父会话目标解析出来");
        Assert.AreEqual(
            parentSessionId,
            runtime.StreamRequests[0].SessionId,
            "父忙延后后目标仍必须是父会话");
        Assert.IsFalse(
            runtime.StreamRequests[0].SessionId!.StartsWith("msg-", StringComparison.Ordinal),
            "父忙场景也绝不落 msg-* 会话");
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
        List<string>? logSink = null)
    {
        var services = new ServiceCollection();
        var catalog = new RecordingWorkspaceAgentCatalog(
            Agent("agent-b", mainSessionId: "agent-b-main-session"));
        services.AddScoped<IMessageInbox>(_ => inbox);
        services.AddScoped<IRuntimeAgentDispatcher>(_ => runtime);
        services.AddScoped<IWorkspaceAgentCatalog>(_ => catalog);
        services.AddScoped<IAgentRuntimeProfileResolver>(
            _ => new RecordingAgentRuntimeProfileResolver(catalog.Agents));
        services.AddScoped<IAgentInvocationDispatchFactory, AgentInvocationDispatchFactory>();
        services.AddScoped<ISubmitTurnHandler>(_ => new RecordingSubmitTurnHandler());
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
