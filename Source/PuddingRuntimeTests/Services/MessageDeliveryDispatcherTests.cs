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
using PuddingPlatform.Services;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Messaging;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class MessageDeliveryDispatcherTests
{
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
    public async Task HandleAsync_OrdinaryAgentDeliveryFromAgent_IncludesSenderContext()
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
        var runtime = new RecordingRuntimeAgentDispatcher();
        var dispatcher = CreateDispatcher(inbox, runtime);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        Assert.HasCount(1, runtime.StreamRequests);
        Assert.AreEqual("hello", runtime.StreamRequests[0].MessageText);
        Assert.IsNotNull(runtime.StreamRequests[0].Origin);
        Assert.AreEqual(MessageEndpointKinds.Agent, runtime.StreamRequests[0].Origin!.FromKind);
        Assert.AreEqual("agent-a", runtime.StreamRequests[0].Origin!.FromId);
        Assert.AreEqual("Agent A", runtime.StreamRequests[0].Origin!.FromDisplayName);
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
    public async Task HandleAsync_HeartbeatDeliveryWhenRuntimeBusy_AcksWithoutRetry()
    {
        var heartbeatFrom = new MessageAddress { Kind = MessageEndpointKinds.System, Id = "heartbeat" };
        var inbox = new RecordingMessageInbox
        {
            ClaimFrom = heartbeatFrom,
            ClaimContent = "── 系统心跳 ──\n\n[系统心跳] 你醒来了。",
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
            CreateEvent(MessageEndpointKinds.Agent, "agent-b", from: heartbeatFrom),
            CancellationToken.None);

        Assert.HasCount(1, runtime.StreamRequests);
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
        Assert.HasCount(1, inbox.Retried);
        Assert.AreEqual("d1", inbox.Retried[0].DeliveryId);
        Assert.IsEmpty(inbox.DeadLettered);
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
    public async Task HandleAsync_OrdinaryAgentDeliveryFromAgent_SendsReplyBackToSender()
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
            StreamFrames =
            [
                ServerSentEventFrame.Json("delta", new { delta = "reply " }),
                ServerSentEventFrame.Json("done", new { reply = "reply from target" }),
            ],
        };
        var messageSystem = new RecordingMessageSystem();
        var dispatcher = CreateDispatcher(inbox, runtime, messageSystem: messageSystem);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        Assert.HasCount(1, messageSystem.Sent);
        var reply = messageSystem.Sent[0];
        Assert.AreEqual(MessageEndpointKinds.Agent, reply.From.Kind);
        Assert.AreEqual("agent-b", reply.From.Id);
        Assert.HasCount(1, reply.To);
        Assert.AreEqual(MessageEndpointKinds.Agent, reply.To[0].Kind);
        Assert.AreEqual("agent-a", reply.To[0].Id);
        Assert.AreEqual("reply from target", reply.Content);
        Assert.AreEqual("agent_reply", reply.Metadata["intent"]);
        Assert.AreEqual("m1", reply.ReplyToMessageId);
        Assert.HasCount(1, inbox.Acked);
    }

    [TestMethod]
    public async Task HandleAsync_ReplyRoutingFailure_DoesNotRetryCompletedInboundDelivery()
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
        var runtime = new RecordingRuntimeAgentDispatcher
        {
            StreamFrames = [ServerSentEventFrame.Json("done", new { reply = "completed work" })],
        };
        var messageSystem = new RecordingMessageSystem
        {
            Failure = new InvalidOperationException("Sender no longer accepts messages."),
        };
        var dispatcher = CreateDispatcher(inbox, runtime, messageSystem: messageSystem);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        Assert.HasCount(1, messageSystem.Sent);
        Assert.HasCount(1, inbox.Acked);
        Assert.IsEmpty(inbox.Retried);
        Assert.IsEmpty(inbox.DeadLettered);
    }

    [TestMethod]
    public async Task HandleAsync_BatchedRuntimeFailure_RetriesEveryClaimedDelivery()
    {
        var inbox = new RecordingMessageInbox
        {
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
        var runtime = new RecordingRuntimeAgentDispatcher
        {
            StreamFrames = [ServerSentEventFrame.Json("error", new { message = "model failed" })],
        };
        var dispatcher = CreateDispatcher(inbox, runtime);

        await dispatcher.HandleAsync(CreateEvent(MessageEndpointKinds.Agent, "agent-b"), CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[] { "d1", "d2" },
            inbox.Retried.Select(item => item.DeliveryId).ToArray());
        Assert.IsEmpty(inbox.Acked);
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
        var dispatcher = CreateDispatcher(inbox, runtime, messageSystem: messageSystem);

        await dispatcher.HandleAsync(
            CreateEvent(
                MessageEndpointKinds.Agent,
                "agent-b",
                metadata: new Dictionary<string, string> { ["intent"] = "agent_reply" }),
            CancellationToken.None);

        Assert.IsEmpty(messageSystem.Sent);
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
            NullLogger<MessageDeliveryDispatcher>.Instance);

        await dispatcher.HandleAsync(CreateSubAgentResultEvent(), CancellationToken.None);

        await using var assertScope = provider.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var transcript = await db.ChatMessages.SingleAsync(m => m.SessionId == "session-1" && m.Role == "agent");
        Assert.AreEqual("parent continuation", transcript.Content);
        Assert.IsNotNull(transcript.ThinkingJson);
        StringAssert.Contains(transcript.ThinkingJson!, "thinking about child result");
        Assert.IsNotNull(transcript.UsageJson);
        StringAssert.Contains(transcript.UsageJson!, "totalTokens");
        var runtime = provider.GetRequiredService<RecordingRuntimeAgentDispatcher>();
        Assert.HasCount(1, runtime.StreamRequests);
        StringAssert.Contains(runtime.StreamRequests[0].MessageText, "\"schema\": \"pudding-message\"");
        StringAssert.Contains(runtime.StreamRequests[0].MessageText, "\"message_type\": \"subagent_result\"");
    }

    private static MessageDeliveryDispatcher CreateDispatcher(
        RecordingMessageInbox inbox,
        RecordingRuntimeAgentDispatcher runtime,
        RecordingAgentExecutionAvailabilityProvider? availability = null,
        RecordingInternalEventBus? eventBus = null,
        RecordingWorkspaceAgentCatalog? catalog = null,
        RecordingMessageSystem? messageSystem = null)
    {
        var services = new ServiceCollection();
        var effectiveCatalog = catalog ?? new RecordingWorkspaceAgentCatalog(
            Agent("agent-b", mainSessionId: "agent-b-main-session"));
        services.AddScoped<IMessageInbox>(_ => inbox);
        services.AddScoped<IRuntimeAgentDispatcher>(_ => runtime);
        if (availability is not null)
            services.AddScoped<IAgentExecutionAvailabilityProvider>(_ => availability);
        services.AddScoped<IWorkspaceAgentCatalog>(_ => effectiveCatalog);
        services.AddScoped<IAgentRuntimeProfileResolver>(_ => new RecordingAgentRuntimeProfileResolver(effectiveCatalog.Agents));
        services.AddScoped<IAgentInvocationDispatchFactory, AgentInvocationDispatchFactory>();
        services.AddLogging();
        if (messageSystem is not null)
            services.AddScoped<IMessageSystem>(_ => messageSystem);

        var provider = services.BuildServiceProvider();
        return new MessageDeliveryDispatcher(
            eventBus ?? new RecordingInternalEventBus(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new AgentWakeQueue(NullLogger<AgentWakeQueue>.Instance),
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
        MessageAddress? from = null) =>
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
        public MessageClaimRequest? LastClaim { get; private set; }
        public int ClaimAttemptCount { get; init; } = 1;
        public IReadOnlyDictionary<string, string>? ClaimMetadata { get; init; }
        public string? ClaimContent { get; init; }
        public MessageAddress? ClaimFrom { get; init; }
        public IReadOnlyList<MessageInboxItem> BatchClaims { get; init; } = [];
        public IReadOnlyList<MessageDeliveryTarget> PendingTargets { get; init; } = [];
        public List<string> PendingTargetKinds { get; } = [];
        public List<(string DeliveryId, string ExecutionId)> Acked { get; } = [];
        public List<(string DeliveryId, string ExecutionId, string Error, DateTimeOffset AvailableAt)> Retried { get; } = [];
        public List<(string DeliveryId, string ExecutionId, string Error)> DeadLettered { get; } = [];

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
            return Task.FromResult<MessageInboxItem?>(new MessageInboxItem
            {
                DeliveryId = "d1",
                MessageId = "m1",
                WorkspaceId = "default",
                RoomId = "room-default",
                From = ClaimFrom ?? new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner" },
                Target = request.Endpoint,
                Content = ClaimContent ?? (ClaimMetadata is null ? "hello" : "subagent result"),
                Status = MessageDeliveryStatuses.Delivering,
                Priority = 0,
                AttemptCount = ClaimAttemptCount,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ClaimedByExecutionId = request.ExecutionId,
            });
        }

        public Task<IReadOnlyList<MessageInboxItem>> ClaimBatchAsync(
            MessageClaimRequest request,
            int maxBatch,
            CancellationToken ct = default) =>
            Task.FromResult(BatchClaims);

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

        public Task DeadLetterAsync(string deliveryId, string executionId, string error, CancellationToken ct = default)
        {
            DeadLettered.Add((deliveryId, executionId, error));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRuntimeAgentDispatcher : IRuntimeAgentDispatcher
    {
        public List<RuntimeDispatchRequest> Requests { get; } = [];
        public List<RuntimeDispatchRequest> StreamRequests { get; } = [];
        public IReadOnlyList<ServerSentEventFrame>? StreamFrames { get; init; }
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
