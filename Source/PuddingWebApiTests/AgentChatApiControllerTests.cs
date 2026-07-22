using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingWebApiTests;

/// <summary>
/// Agent-first chat API contract tests.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AgentChatApiControllerTests
{
    private static CustomWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        _factory = new CustomWebApplicationFactory();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _factory.Dispose();
    }

    [TestInitialize]
    public void TestInit()
    {
        _client = _factory.CreateClient();
        JwtHelper.SetBearerToken(_client);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _client.Dispose();
    }

    [TestMethod]
    public async Task AgentStatusEndpoint_ReturnsWorkspaceAgentProjectionShape()
    {
        var response = await _client.GetAsync("/api/workspaces/default/agents/status");

        Assert.AreNotEqual(HttpStatusCode.NotFound, response.StatusCode);
        response.EnsureSuccessStatusCode();

        var list = await response.Content.ReadFromJsonAsync<List<AgentStatusProjectionDto>>(JsonOpts);
        Assert.IsNotNull(list);
    }

    [TestMethod]
    public async Task AgentStatusEndpoint_MapsActiveMainSessionWithoutRunningEventsToIdle()
    {
        var agentId = await CreateWorkspaceAgentAsync("agent-idle-active");
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = agentId,
            agentTemplateId = "global:general-assistant",
            title = "Idle Active Agent"
        });
        createResponse.EnsureSuccessStatusCode();
        var session = await createResponse.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(session);

        using (var scope = _factory.Services.CreateScope())
        {
            var api = scope.ServiceProvider.GetRequiredService<PlatformApiClient>();
            var updated = await api.UpdateSessionAsync(
                session!.SessionId,
                new UpdateSessionRequest { Status = SessionStatus.Active },
                CancellationToken.None);
            Assert.IsNotNull(updated);
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/status");
        response.EnsureSuccessStatusCode();

        var list = await response.Content.ReadFromJsonAsync<List<AgentStatusProjectionDto>>(JsonOpts);
        var projection = list!.Single(item => item.AgentId == agentId);
        Assert.AreEqual("idle", projection.Status);
    }

    [TestMethod]
    public async Task AgentStatusEndpoint_MapsUnfinishedExecutionEventsToRunning()
    {
        var agentId = await CreateWorkspaceAgentAsync("agent-running-events");
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = agentId,
            agentTemplateId = "global:general-assistant",
            title = "Running Events Agent"
        });
        createResponse.EnsureSuccessStatusCode();
        var session = await createResponse.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(session);

        using (var scope = _factory.Services.CreateScope())
        {
            var api = scope.ServiceProvider.GetRequiredService<PlatformApiClient>();
            await api.UpdateSessionAsync(
                session!.SessionId,
                new UpdateSessionRequest { Status = SessionStatus.Active },
                CancellationToken.None);

            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.ConversationEvents.Add(NewConversationEvent(
                session.SessionId,
                1,
                ConversationEventTypes.MessageContentAppended,
                "{\"delta\":\"working\"}",
                DateTimeOffset.UtcNow,
                runId: "run-status-running"));
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/status");
        response.EnsureSuccessStatusCode();

        var list = await response.Content.ReadFromJsonAsync<List<AgentStatusProjectionDto>>(JsonOpts);
        var projection = list!.Single(item => item.AgentId == agentId);
        Assert.AreEqual("running", projection.Status);
    }

    [TestMethod]
    public async Task AgentStatusEndpoint_MapsStaleUnfinishedExecutionEventsToIdle()
    {
        var agentId = await CreateWorkspaceAgentAsync("agent-stale-running-events");
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = agentId,
            agentTemplateId = "global:general-assistant",
            title = "Stale Running Events Agent"
        });
        createResponse.EnsureSuccessStatusCode();
        var session = await createResponse.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(session);

        using (var scope = _factory.Services.CreateScope())
        {
            var api = scope.ServiceProvider.GetRequiredService<PlatformApiClient>();
            await api.UpdateSessionAsync(
                session!.SessionId,
                new UpdateSessionRequest { Status = SessionStatus.Active },
                CancellationToken.None);

            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.ConversationEvents.Add(NewConversationEvent(
                session.SessionId,
                1,
                ConversationEventTypes.MessageContentAppended,
                "{\"delta\":\"stale\"}",
                DateTimeOffset.UtcNow.AddMinutes(-15),
                runId: "run-status-stale"));
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/status");
        response.EnsureSuccessStatusCode();

        var list = await response.Content.ReadFromJsonAsync<List<AgentStatusProjectionDto>>(JsonOpts);
        var projection = list!.Single(item => item.AgentId == agentId);
        Assert.AreEqual("idle", projection.Status);
    }

    [TestMethod]
    public async Task AgentStatusEndpoint_MapsTerminalExecutionEventsToIdle()
    {
        var agentId = await CreateWorkspaceAgentAsync("agent-terminal-events");
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = agentId,
            agentTemplateId = "global:general-assistant",
            title = "Terminal Events Agent"
        });
        createResponse.EnsureSuccessStatusCode();
        var session = await createResponse.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(session);

        using (var scope = _factory.Services.CreateScope())
        {
            var api = scope.ServiceProvider.GetRequiredService<PlatformApiClient>();
            await api.UpdateSessionAsync(
                session!.SessionId,
                new UpdateSessionRequest { Status = SessionStatus.Active },
                CancellationToken.None);

            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.ConversationEvents.AddRange(
                NewConversationEvent(
                    session.SessionId,
                    1,
                    ConversationEventTypes.MessageContentAppended,
                    "{\"delta\":\"done\"}",
                    now,
                    runId: "run-status-terminal"),
                NewConversationEvent(
                    session.SessionId,
                    2,
                    ConversationEventTypes.TurnCompleted,
                    "{\"reply\":\"done\"}",
                    now.AddMilliseconds(1),
                    runId: "run-status-terminal"));
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/status");
        response.EnsureSuccessStatusCode();

        var list = await response.Content.ReadFromJsonAsync<List<AgentStatusProjectionDto>>(JsonOpts);
        var projection = list!.Single(item => item.AgentId == agentId);
        Assert.AreEqual("idle", projection.Status);
    }

    [TestMethod]
    public async Task AgentStatusEndpoint_MapsFailedTurnTerminalToIdle()
    {
        var agentId = await CreateWorkspaceAgentAsync("agent-failed-terminal");
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = agentId,
            agentTemplateId = "global:general-assistant",
            title = "Failed Terminal Agent"
        });
        createResponse.EnsureSuccessStatusCode();
        var session = await createResponse.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(session);

        using (var scope = _factory.Services.CreateScope())
        {
            var api = scope.ServiceProvider.GetRequiredService<PlatformApiClient>();
            await api.UpdateSessionAsync(
                session!.SessionId,
                new UpdateSessionRequest { Status = SessionStatus.Failed },
                CancellationToken.None);

            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.ConversationEvents.AddRange(
                NewConversationEvent(
                    session.SessionId,
                    1,
                    ConversationEventTypes.MessageContentAppended,
                    "{\"delta\":\"partial\"}",
                    now,
                    runId: "run-status-failed"),
                NewConversationEvent(
                    session.SessionId,
                    2,
                    ConversationEventTypes.TurnFailed,
                    "{\"message\":\"provider transport failed\"}",
                    now.AddMilliseconds(1),
                    runId: "run-status-failed"));
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/status");
        response.EnsureSuccessStatusCode();

        var list = await response.Content.ReadFromJsonAsync<List<AgentStatusProjectionDto>>(JsonOpts);
        var projection = list!.Single(item => item.AgentId == agentId);
        Assert.AreEqual("idle", projection.Status);
    }

    [TestMethod]
    public async Task AgentStatusEndpoint_IgnoresUsageEventsAfterTerminalExecution()
    {
        var agentId = await CreateWorkspaceAgentAsync("agent-terminal-usage-events");
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = agentId,
            agentTemplateId = "global:general-assistant",
            title = "Terminal Usage Events Agent"
        });
        createResponse.EnsureSuccessStatusCode();
        var session = await createResponse.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(session);

        using (var scope = _factory.Services.CreateScope())
        {
            var api = scope.ServiceProvider.GetRequiredService<PlatformApiClient>();
            await api.UpdateSessionAsync(
                session!.SessionId,
                new UpdateSessionRequest { Status = SessionStatus.Active },
                CancellationToken.None);

            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.ConversationEvents.AddRange(
                NewConversationEvent(
                    session.SessionId,
                    1,
                    ConversationEventTypes.MessageContentAppended,
                    "{\"delta\":\"done\"}",
                    now,
                    runId: "run-status-usage"),
                NewConversationEvent(
                    session.SessionId,
                    2,
                    ConversationEventTypes.TurnCompleted,
                    "{\"reply\":\"done\"}",
                    now.AddMilliseconds(1),
                    runId: "run-status-usage"),
                NewConversationEvent(
                    session.SessionId,
                    3,
                    ConversationEventTypes.UsageRecorded,
                    "{\"totalTokens\":10}",
                    now.AddMilliseconds(2),
                    runId: "run-status-usage"));
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/status");
        response.EnsureSuccessStatusCode();

        var list = await response.Content.ReadFromJsonAsync<List<AgentStatusProjectionDto>>(JsonOpts);
        var projection = list!.Single(item => item.AgentId == agentId);
        Assert.AreEqual("idle", projection.Status);
    }

    [TestMethod]
    public async Task AgentConversationEndpoint_ReturnsRenderableConversationView()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = "agent-conversation",
            agentTemplateId = "global:general-assistant",
            title = "Conversation Agent"
        });
        createResponse.EnsureSuccessStatusCode();
        var session = await createResponse.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(session);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            const string turnId = "conversation-renderable-turn";
            const string userMessageId = "conversation-renderable-user";
            const string agentMessageId = "conversation-renderable-agent";
            db.ChatMessages.AddRange(
                new ChatMessageEntity
                {
                    MessageId = userMessageId,
                    SessionId = session!.SessionId,
                    Role = "user",
                    Content = "hello agent",
                    CreatedAt = createdAt,
                },
                new ChatMessageEntity
                {
                    MessageId = agentMessageId,
                    SessionId = session.SessionId,
                    Role = "agent",
                    Content = "hello user",
                    TurnId = turnId,
                    CreatedAt = createdAt + 1,
                });
            db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
            {
                CommandId = "conversation-renderable-command",
                BatchId = "conversation-renderable-batch",
                ClientRequestId = "conversation-renderable-request",
                WorkspaceId = "default",
                SessionId = session.SessionId,
                MessageId = agentMessageId,
                UserMessageId = userMessageId,
                TurnId = turnId,
                AgentInstanceId = "agent-conversation",
                UserId = "admin",
                Status = "succeeded",
                CreatedAt = createdAt,
            });
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/agent-conversation/conversation");

        Assert.AreNotEqual(HttpStatusCode.NotFound, response.StatusCode);
        response.EnsureSuccessStatusCode();

        var view = await response.Content.ReadFromJsonAsync<AgentConversationViewDto>(JsonOpts);
        Assert.IsNotNull(view);
        Assert.AreEqual("default", view!.WorkspaceId);
        Assert.AreEqual("single-user", view.OwnerUserId);
        Assert.AreEqual("agent-conversation", view.AgentId);
        Assert.AreEqual(session.SessionId, view.MainSessionId);
        Assert.HasCount(2, view.Messages);
        Assert.AreEqual("hello agent", view.Messages[0].Content);
        Assert.AreEqual("hello user", view.Messages[1].Content);
        Assert.IsTrue(view.Messages.All(message => message.TurnId == "conversation-renderable-turn"));
    }

    [TestMethod]
    public async Task AgentConversationEndpoint_ReturnsMostRecentMessagesWhenTranscriptExceedsLimit()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = "agent-conversation-latest-window",
            agentTemplateId = "global:general-assistant",
            title = "Conversation Latest Window Agent"
        });
        createResponse.EnsureSuccessStatusCode();
        var session = await createResponse.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(session);

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            for (var i = 0; i < 120; i++)
            {
                db.ChatMessages.Add(new ChatMessageEntity
                {
                    MessageId = $"conversation-window-{i:000}",
                    SessionId = session!.SessionId,
                    Role = i % 2 == 0 ? "user" : "agent",
                    Content = $"message-{i:000}",
                    CreatedAt = baseTime + i,
                });
            }
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/agent-conversation-latest-window/conversation");
        response.EnsureSuccessStatusCode();

        var view = await response.Content.ReadFromJsonAsync<AgentConversationViewDto>(JsonOpts);
        Assert.IsNotNull(view);
        Assert.HasCount(100, view!.Messages);
        Assert.AreEqual("message-020", view.Messages[0].Content);
        Assert.AreEqual("message-119", view.Messages[^1].Content);
        Assert.IsFalse(view.Messages.Any(message => message.Content == "message-000"));
    }

    [TestMethod]
    public async Task AgentConversationEndpoint_ProjectsHistoricalThinkingItems()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = "agent-conversation-thinking",
            agentTemplateId = "global:general-assistant",
            title = "Conversation Thinking Agent"
        });
        createResponse.EnsureSuccessStatusCode();
        var session = await createResponse.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(session);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.ChatMessages.Add(new ChatMessageEntity
            {
                MessageId = "conversation-thinking-agent",
                SessionId = session!.SessionId,
                Role = "agent",
                Content = "final answer",
                ThinkingJson = """[{"text":"分析用户需求","timestamp":2000}]""",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/agent-conversation-thinking/conversation");
        response.EnsureSuccessStatusCode();

        var view = await response.Content.ReadFromJsonAsync<AgentConversationViewDto>(JsonOpts);
        Assert.IsNotNull(view);
        var message = view!.Messages.Single();
        Assert.AreEqual("final answer", message.Content);
        Assert.HasCount(1, message.ProcessItems);
        Assert.AreEqual("thinking", message.ProcessItems[0].Kind);
        Assert.AreEqual("分析用户需求", message.ProcessItems[0].Text);
    }

    [TestMethod]
    public async Task AgentConversationEndpoint_ProjectsHistoricalToolProcessItemsFromConversationEvents()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = "agent-conversation-tools",
            agentTemplateId = "global:general-assistant",
            title = "Conversation Tool Agent"
        });
        createResponse.EnsureSuccessStatusCode();
        var session = await createResponse.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(session);

        const string externalMessageId = "historical-tool-message";
        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.ChatMessages.Add(new ChatMessageEntity
            {
                MessageId = externalMessageId,
                SessionId = session!.SessionId,
                Role = "agent",
                Content = "tool final answer",
                CreatedAt = now.ToUnixTimeMilliseconds(),
            });
            db.ConversationEvents.AddRange(
                NewConversationEvent(
                    session.SessionId,
                    1,
                    ConversationEventTypes.MessageThinkingSummaryAppended,
                    "{\"delta\":\"准备查找文件\"}",
                    now,
                    runId: "run-historical-tools",
                    messageId: externalMessageId),
                NewConversationEvent(
                    session.SessionId,
                    2,
                    ConversationEventTypes.ToolCallRequested,
                    "{\"name\":\"file_search\",\"arguments\":\"{\\\"pattern\\\":\\\"*.md\\\"}\"}",
                    now.AddMilliseconds(1),
                    runId: "run-historical-tools",
                    messageId: externalMessageId),
                NewConversationEvent(
                    session.SessionId,
                    3,
                    ConversationEventTypes.ToolCallCompleted,
                    "{\"name\":\"file_search\",\"exitCode\":0,\"output\":\"README.md\"}",
                    now.AddMilliseconds(2),
                    runId: "run-historical-tools",
                    messageId: externalMessageId),
                NewConversationEvent(
                    session.SessionId,
                    4,
                    ConversationEventTypes.TurnCompleted,
                    "{\"reply\":\"tool final answer\"}",
                    now.AddMilliseconds(3),
                    runId: "run-historical-tools",
                    messageId: externalMessageId));
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/agent-conversation-tools/conversation");
        response.EnsureSuccessStatusCode();

        var view = await response.Content.ReadFromJsonAsync<AgentConversationViewDto>(JsonOpts);
        Assert.IsNotNull(view);
        var message = view!.Messages.Single();
        Assert.AreEqual(externalMessageId, message.MessageId);
        Assert.AreEqual("run-historical-tools", message.RunId);
        Assert.AreEqual("tool final answer", message.Content);
        Assert.HasCount(3, message.ProcessItems);
        Assert.AreEqual("thinking", message.ProcessItems[0].Kind);
        Assert.AreEqual("准备查找文件", message.ProcessItems[0].Text);
        Assert.AreEqual("tool_call", message.ProcessItems[1].Kind);
        Assert.AreEqual("file_search", message.ProcessItems[1].Name);
        Assert.AreEqual("{\"pattern\":\"*.md\"}", message.ProcessItems[1].Arguments);
        Assert.AreEqual("tool_result", message.ProcessItems[2].Kind);
        Assert.AreEqual("README.md", message.ProcessItems[2].Output);
        Assert.AreEqual(0, message.ProcessItems[2].ExitCode);
    }

    [TestMethod]
    public async Task AgentConversationEndpoint_CorrelatesProcessByMessageIdAndCompletedRun()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = "agent-process-correlation",
            agentTemplateId = "global:general-assistant",
            title = "Process Correlation Agent"
        });
        createResponse.EnsureSuccessStatusCode();
        var session = await createResponse.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(session);

        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.ChatMessages.AddRange(
                new ChatMessageEntity
                {
                    MessageId = "same-reply-message-a",
                    SessionId = session!.SessionId,
                    Role = "agent",
                    Content = "same reply",
                    CreatedAt = now.ToUnixTimeMilliseconds(),
                },
                new ChatMessageEntity
                {
                    MessageId = "same-reply-message-b",
                    SessionId = session.SessionId,
                    Role = "agent",
                    Content = "same reply",
                    CreatedAt = now.AddMilliseconds(1).ToUnixTimeMilliseconds(),
                });
            db.ConversationEvents.AddRange(
                NewConversationEvent(
                    session.SessionId,
                    1,
                    ConversationEventTypes.MessageThinkingSummaryAppended,
                    "{\"delta\":\"failed attempt\"}",
                    now,
                    runId: "run-a-failed",
                    messageId: "same-reply-message-a"),
                NewConversationEvent(
                    session.SessionId,
                    2,
                    ConversationEventTypes.TurnFailed,
                    "{\"message\":\"retry\"}",
                    now.AddMilliseconds(1),
                    runId: "run-a-failed",
                    messageId: "same-reply-message-a"),
                NewConversationEvent(
                    session.SessionId,
                    3,
                    ConversationEventTypes.MessageThinkingSummaryAppended,
                    "{\"delta\":\"successful attempt\"}",
                    now.AddMilliseconds(2),
                    runId: "run-a-completed",
                    messageId: "same-reply-message-a"),
                NewConversationEvent(
                    session.SessionId,
                    4,
                    ConversationEventTypes.TurnCompleted,
                    "{\"reply\":\"same reply\"}",
                    now.AddMilliseconds(3),
                    runId: "run-a-completed",
                    messageId: "same-reply-message-a"),
                NewConversationEvent(
                    session.SessionId,
                    5,
                    ConversationEventTypes.MessageThinkingSummaryAppended,
                    "{\"delta\":\"second message process\"}",
                    now.AddMilliseconds(4),
                    runId: "run-b-completed",
                    messageId: "same-reply-message-b"),
                NewConversationEvent(
                    session.SessionId,
                    6,
                    ConversationEventTypes.TurnCompleted,
                    "{\"reply\":\"same reply\"}",
                    now.AddMilliseconds(5),
                    runId: "run-b-completed",
                    messageId: "same-reply-message-b"));
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/agent-process-correlation/conversation");
        response.EnsureSuccessStatusCode();

        var view = await response.Content.ReadFromJsonAsync<AgentConversationViewDto>(JsonOpts);
        Assert.IsNotNull(view);
        Assert.HasCount(2, view!.Messages);
        Assert.AreEqual("run-a-completed", view.Messages[0].RunId);
        Assert.HasCount(1, view.Messages[0].ProcessItems);
        Assert.AreEqual("successful attempt", view.Messages[0].ProcessItems[0].Text);
        Assert.AreEqual("run-b-completed", view.Messages[1].RunId);
        Assert.HasCount(1, view.Messages[1].ProcessItems);
        Assert.AreEqual("second message process", view.Messages[1].ProcessItems[0].Text);
    }

    [TestMethod]
    public async Task AgentConversationEndpoint_ReturnsActiveRunOutputFromConversationEvents()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = "agent-active-output",
            agentTemplateId = "global:general-assistant",
            title = "Active Output Agent"
        });
        createResponse.EnsureSuccessStatusCode();
        var session = await createResponse.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(session);

        using (var scope = _factory.Services.CreateScope())
        {
            var api = scope.ServiceProvider.GetRequiredService<PlatformApiClient>();
            await api.UpdateSessionAsync(
                session!.SessionId,
                new UpdateSessionRequest { Status = SessionStatus.Active },
                CancellationToken.None);

            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.ConversationEvents.AddRange(
                NewConversationEvent(
                    session.SessionId,
                    1,
                    ConversationEventTypes.MessageThinkingSummaryAppended,
                    "{\"delta\":\"分析需求\"}",
                    now,
                    runId: "run-active-output"),
                NewConversationEvent(
                    session.SessionId,
                    2,
                    ConversationEventTypes.ToolCallRequested,
                    "{\"name\":\"file_search\",\"arguments\":\"{\\\"pattern\\\":\\\"*.md\\\"}\"}",
                    now.AddMilliseconds(1),
                    runId: "run-active-output"),
                NewConversationEvent(
                    session.SessionId,
                    3,
                    ConversationEventTypes.ToolCallCompleted,
                    "{\"name\":\"file_search\",\"exitCode\":0,\"output\":\"README.md\"}",
                    now.AddMilliseconds(2),
                    runId: "run-active-output"),
                NewConversationEvent(
                    session.SessionId,
                    4,
                    ConversationEventTypes.MessageContentAppended,
                    "{\"delta\":\"partial \"}",
                    now.AddMilliseconds(3),
                    runId: "run-active-output"),
                NewConversationEvent(
                    session.SessionId,
                    5,
                    ConversationEventTypes.MessageContentAppended,
                    "{\"delta\":\"answer\"}",
                    now.AddMilliseconds(4),
                    runId: "run-active-output"));
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/agent-active-output/conversation");
        response.EnsureSuccessStatusCode();

        var view = await response.Content.ReadFromJsonAsync<AgentConversationViewDto>(JsonOpts);
        Assert.IsNotNull(view);
        Assert.IsNotNull(view!.ActiveRun);
        Assert.AreEqual("running", view.ActiveRun!.Status);
        Assert.AreEqual("partial answer", view.ActiveRun.OutputSnapshot.Markdown);
        Assert.AreEqual(5, view.EventCursor);
        Assert.HasCount(3, view.ActiveRun.OutputSnapshot.ProcessItems);
        Assert.AreEqual("thinking", view.ActiveRun.OutputSnapshot.ProcessItems[0].Kind);
        Assert.AreEqual("分析需求", view.ActiveRun.OutputSnapshot.ProcessItems[0].Text);
        Assert.AreEqual("tool_call", view.ActiveRun.OutputSnapshot.ProcessItems[1].Kind);
        Assert.AreEqual("file_search", view.ActiveRun.OutputSnapshot.ProcessItems[1].Name);
        Assert.AreEqual("{\"pattern\":\"*.md\"}", view.ActiveRun.OutputSnapshot.ProcessItems[1].Arguments);
        Assert.AreEqual("tool_result", view.ActiveRun.OutputSnapshot.ProcessItems[2].Kind);
        Assert.AreEqual("README.md", view.ActiveRun.OutputSnapshot.ProcessItems[2].Output);
        Assert.AreEqual(0, view.ActiveRun.OutputSnapshot.ProcessItems[2].ExitCode);
    }

    [TestMethod]
    public async Task AgentConversationEndpoint_ActiveRunOnlyIncludesLatestActiveMessageEvents()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = "agent-active-current-only",
            agentTemplateId = "global:general-assistant",
            title = "Active Current Only Agent"
        });
        createResponse.EnsureSuccessStatusCode();
        var session = await createResponse.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(session);

        using (var scope = _factory.Services.CreateScope())
        {
            var api = scope.ServiceProvider.GetRequiredService<PlatformApiClient>();
            await api.UpdateSessionAsync(
                session!.SessionId,
                new UpdateSessionRequest { Status = SessionStatus.Active },
                CancellationToken.None);

            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.ConversationEvents.AddRange(
                NewConversationEvent(
                    session.SessionId,
                    1,
                    ConversationEventTypes.TurnStarted,
                    "{}",
                    now,
                    runId: "run-old",
                    messageId: "welcome-message"),
                NewConversationEvent(
                    session.SessionId,
                    2,
                    ConversationEventTypes.MessageContentAppended,
                    "{\"delta\":\"old welcome\"}",
                    now.AddMilliseconds(1),
                    runId: "run-old",
                    messageId: "welcome-message"),
                NewConversationEvent(
                    session.SessionId,
                    3,
                    ConversationEventTypes.TurnCompleted,
                    "{\"reply\":\"old welcome\"}",
                    now.AddMilliseconds(2),
                    runId: "run-old",
                    messageId: "welcome-message"),
                NewConversationEvent(
                    session.SessionId,
                    4,
                    ConversationEventTypes.TurnStarted,
                    "{}",
                    now.AddMilliseconds(3),
                    runId: "run-current",
                    messageId: "current-message"),
                NewConversationEvent(
                    session.SessionId,
                    5,
                    ConversationEventTypes.MessageContentAppended,
                    "{\"delta\":\"current \"}",
                    now.AddMilliseconds(4),
                    runId: "run-current",
                    messageId: "current-message"),
                NewConversationEvent(
                    session.SessionId,
                    6,
                    ConversationEventTypes.MessageContentAppended,
                    "{\"delta\":\"answer\"}",
                    now.AddMilliseconds(5),
                    runId: "run-current",
                    messageId: "current-message"));
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/agent-active-current-only/conversation");
        response.EnsureSuccessStatusCode();

        var view = await response.Content.ReadFromJsonAsync<AgentConversationViewDto>(JsonOpts);
        Assert.IsNotNull(view);
        Assert.IsNotNull(view!.ActiveRun);
        Assert.AreEqual("current answer", view.ActiveRun!.OutputSnapshot.Markdown);
        Assert.IsFalse(view.ActiveRun.OutputSnapshot.Markdown.Contains("old welcome", StringComparison.Ordinal));
        Assert.AreEqual(6, view.EventCursor);
    }

    [TestMethod]
    public async Task AgentConversationEndpoint_DoesNotProjectStaleUnfinishedEventsAsActiveRun()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = "agent-stale-unfinished",
            agentTemplateId = "global:general-assistant",
            title = "Stale Unfinished Agent"
        });
        createResponse.EnsureSuccessStatusCode();
        var session = await createResponse.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(session);

        using (var scope = _factory.Services.CreateScope())
        {
            var api = scope.ServiceProvider.GetRequiredService<PlatformApiClient>();
            await api.UpdateSessionAsync(
                session!.SessionId,
                new UpdateSessionRequest { Status = SessionStatus.Active },
                CancellationToken.None);

            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var stale = DateTimeOffset.UtcNow.AddMinutes(-15);
            db.ConversationEvents.AddRange(
                NewConversationEvent(
                    session.SessionId,
                    1,
                    ConversationEventTypes.TurnStarted,
                    "{}",
                    stale,
                    runId: "run-stale",
                    messageId: "stale-message"),
                NewConversationEvent(
                    session.SessionId,
                    2,
                    ConversationEventTypes.MessageContentAppended,
                    "{\"delta\":\"stale output\"}",
                    stale.AddSeconds(1),
                    runId: "run-stale",
                    messageId: "stale-message"),
                NewConversationEvent(
                    session.SessionId,
                    3,
                    ConversationEventTypes.ToolCallRequested,
                    "{\"name\":\"file_search\"}",
                    stale.AddSeconds(2),
                    runId: "run-stale",
                    messageId: "stale-message"));
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/agent-stale-unfinished/conversation");
        response.EnsureSuccessStatusCode();

        var view = await response.Content.ReadFromJsonAsync<AgentConversationViewDto>(JsonOpts);
        Assert.IsNotNull(view);
        Assert.IsNull(view!.ActiveRun);
        Assert.AreEqual(3, view.EventCursor);
    }

    private sealed record AgentStatusProjectionDto(
        string WorkspaceId,
        string OwnerUserId,
        string AgentId,
        string MainSessionId,
        string Status,
        string Summary,
        long EventCursor,
        string UpdatedAt);

    private async Task<string> CreateWorkspaceAgentAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/workspaces/default/agents", new
        {
            name,
            displayName = name,
            sourceTemplateId = "general-assistant",
        });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<WorkspaceAgentIdDto>(JsonOpts);
        Assert.IsNotNull(created);
        return created.AgentId;
    }

    private static ConversationEventEntity NewConversationEvent(
        string conversationId,
        long sequence,
        string type,
        string payload,
        DateTimeOffset occurredAt,
        string? runId = null,
        string messageId = "assistant-message",
        string commandId = "command")
        => new()
        {
            ConversationId = conversationId,
            Sequence = sequence,
            EventId = $"{conversationId}:{sequence}:{Guid.NewGuid():N}",
            WorkspaceId = "default",
            TurnId = $"turn:{runId ?? "accepted"}",
            CommandId = commandId,
            RunId = runId,
            MessageId = messageId,
            Type = type,
            SchemaVersion = 1,
            Payload = payload,
            OccurredAt = occurredAt.ToString("O"),
            CommittedAt = occurredAt.ToString("O"),
            CorrelationId = conversationId,
        };

    private sealed record AgentConversationViewDto(
        string WorkspaceId,
        string OwnerUserId,
        string AgentId,
        string MainSessionId,
        List<ConversationMessageViewDto> Messages,
        AgentRunViewDto? ActiveRun,
        long EventCursor,
        string UpdatedAt);

    private sealed record WorkspaceAgentIdDto(string AgentId);

    private sealed record AgentRunViewDto(
        string RunId,
        string Status,
        AgentOutputSnapshotDto OutputSnapshot);

    private sealed record AgentOutputSnapshotDto(
        string Markdown,
        List<ProcessSummaryItemDto> ProcessItems);

    private sealed record ProcessSummaryItemDto(
        string Id,
        string Kind,
        string Status,
        string Text,
        string Timestamp,
        string? Name,
        string? Arguments,
        string? Output,
        int? ExitCode,
        string? Message);

    private sealed record ConversationMessageViewDto(
        string MessageId,
        string? TurnId,
        string? RunId,
        string Role,
        string SourceId,
        string SourceName,
        string CreatedAt,
        string Content,
        string Status,
        List<ProcessSummaryItemDto> ProcessItems);
}
