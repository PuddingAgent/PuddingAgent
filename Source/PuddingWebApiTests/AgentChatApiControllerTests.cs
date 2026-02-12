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
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = "agent-idle-active",
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
        var projection = list!.Single(item => item.AgentId == "agent-idle-active");
        Assert.AreEqual("idle", projection.Status);
    }

    [TestMethod]
    public async Task AgentStatusEndpoint_MapsUnfinishedExecutionEventsToRunning()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = "agent-running-events",
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
            db.SessionEventLogs.Add(new SessionEventLogEntity
            {
                SessionId = session.SessionId,
                WorkspaceId = "default",
                SequenceNum = 1,
                EventType = "delta",
                Data = "{\"delta\":\"working\"}",
                RecordedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/status");
        response.EnsureSuccessStatusCode();

        var list = await response.Content.ReadFromJsonAsync<List<AgentStatusProjectionDto>>(JsonOpts);
        var projection = list!.Single(item => item.AgentId == "agent-running-events");
        Assert.AreEqual("running", projection.Status);
    }

    [TestMethod]
    public async Task AgentStatusEndpoint_MapsStaleUnfinishedExecutionEventsToIdle()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = "agent-stale-running-events",
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
            db.SessionEventLogs.Add(new SessionEventLogEntity
            {
                SessionId = session.SessionId,
                WorkspaceId = "default",
                SequenceNum = 1,
                EventType = "delta",
                Data = "{\"delta\":\"stale\"}",
                RecordedAt = DateTimeOffset.UtcNow.AddMinutes(-15).ToString("O"),
            });
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/status");
        response.EnsureSuccessStatusCode();

        var list = await response.Content.ReadFromJsonAsync<List<AgentStatusProjectionDto>>(JsonOpts);
        var projection = list!.Single(item => item.AgentId == "agent-stale-running-events");
        Assert.AreEqual("idle", projection.Status);
    }

    [TestMethod]
    public async Task AgentStatusEndpoint_MapsTerminalExecutionEventsToIdle()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = "agent-terminal-events",
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
            db.SessionEventLogs.AddRange(
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 1,
                    EventType = "delta",
                    Data = "{\"delta\":\"done\"}",
                    RecordedAt = now.ToString("O"),
                },
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 2,
                    EventType = "done",
                    Data = "{\"reply\":\"done\"}",
                    RecordedAt = now.AddMilliseconds(1).ToString("O"),
                });
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/status");
        response.EnsureSuccessStatusCode();

        var list = await response.Content.ReadFromJsonAsync<List<AgentStatusProjectionDto>>(JsonOpts);
        var projection = list!.Single(item => item.AgentId == "agent-terminal-events");
        Assert.AreEqual("idle", projection.Status);
    }

    [TestMethod]
    public async Task AgentStatusEndpoint_IgnoresUsageEventsAfterTerminalExecution()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = "agent-terminal-usage-events",
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
            db.SessionEventLogs.AddRange(
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 1,
                    EventType = "delta",
                    Data = "{\"delta\":\"done\"}",
                    RecordedAt = now.ToString("O"),
                },
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 2,
                    EventType = "done",
                    Data = "{\"reply\":\"done\"}",
                    RecordedAt = now.AddMilliseconds(1).ToString("O"),
                },
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 3,
                    EventType = "usage",
                    Data = "{\"totalTokens\":10}",
                    RecordedAt = now.AddMilliseconds(2).ToString("O"),
                });
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/status");
        response.EnsureSuccessStatusCode();

        var list = await response.Content.ReadFromJsonAsync<List<AgentStatusProjectionDto>>(JsonOpts);
        var projection = list!.Single(item => item.AgentId == "agent-terminal-usage-events");
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
            db.ChatMessages.Add(new ChatMessageEntity
            {
                SessionId = session!.SessionId,
                Role = "user",
                Content = "hello agent",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
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
        Assert.AreEqual("hello agent", view.Messages.Single().Content);
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
    public async Task AgentConversationEndpoint_ProjectsHistoricalToolProcessItemsFromSessionEvents()
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
                SessionId = session!.SessionId,
                Role = "agent",
                Content = "tool final answer",
                CreatedAt = now.ToUnixTimeMilliseconds(),
            });
            db.SessionEventLogs.AddRange(
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 1,
                    EventType = "thinking",
                    Data = $"{{\"messageId\":\"{externalMessageId}\",\"delta\":\"准备查找文件\"}}",
                    RecordedAt = now.ToString("O"),
                },
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 2,
                    EventType = "tool_call",
                    Data = $"{{\"messageId\":\"{externalMessageId}\",\"name\":\"file_search\",\"arguments\":\"{{\\\"pattern\\\":\\\"*.md\\\"}}\"}}",
                    RecordedAt = now.AddMilliseconds(1).ToString("O"),
                },
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 3,
                    EventType = "tool_result",
                    Data = $"{{\"messageId\":\"{externalMessageId}\",\"name\":\"file_search\",\"exitCode\":0,\"output\":\"README.md\"}}",
                    RecordedAt = now.AddMilliseconds(2).ToString("O"),
                },
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 4,
                    EventType = "done",
                    Data = $"{{\"messageId\":\"{externalMessageId}\",\"reply\":\"tool final answer\"}}",
                    RecordedAt = now.AddMilliseconds(3).ToString("O"),
                });
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/workspaces/default/agents/agent-conversation-tools/conversation");
        response.EnsureSuccessStatusCode();

        var view = await response.Content.ReadFromJsonAsync<AgentConversationViewDto>(JsonOpts);
        Assert.IsNotNull(view);
        var message = view!.Messages.Single();
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
    public async Task AgentConversationEndpoint_ReturnsActiveRunOutputFromSessionEvents()
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
            db.SessionEventLogs.AddRange(
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 1,
                    EventType = "thinking",
                    Data = "{\"delta\":\"分析需求\"}",
                    RecordedAt = now.ToString("O"),
                },
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 2,
                    EventType = "tool_call",
                    Data = "{\"name\":\"file_search\",\"arguments\":\"{\\\"pattern\\\":\\\"*.md\\\"}\"}",
                    RecordedAt = now.AddMilliseconds(1).ToString("O"),
                },
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 3,
                    EventType = "tool_result",
                    Data = "{\"name\":\"file_search\",\"exitCode\":0,\"output\":\"README.md\"}",
                    RecordedAt = now.AddMilliseconds(2).ToString("O"),
                },
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 4,
                    EventType = "delta",
                    Data = "{\"delta\":\"partial \"}",
                    RecordedAt = now.AddMilliseconds(3).ToString("O"),
                },
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 5,
                    EventType = "delta",
                    Data = "{\"delta\":\"answer\"}",
                    RecordedAt = now.AddMilliseconds(4).ToString("O"),
                });
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
            db.SessionEventLogs.AddRange(
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 1,
                    EventType = "metadata",
                    Data = "{\"messageId\":\"welcome-message\"}",
                    RecordedAt = now.ToString("O"),
                },
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 2,
                    EventType = "delta",
                    Data = "{\"messageId\":\"welcome-message\",\"delta\":\"old welcome\"}",
                    RecordedAt = now.AddMilliseconds(1).ToString("O"),
                },
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 3,
                    EventType = "done",
                    Data = "{\"messageId\":\"welcome-message\",\"reply\":\"old welcome\"}",
                    RecordedAt = now.AddMilliseconds(2).ToString("O"),
                },
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 4,
                    EventType = "metadata",
                    Data = "{\"messageId\":\"current-message\"}",
                    RecordedAt = now.AddMilliseconds(3).ToString("O"),
                },
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 5,
                    EventType = "delta",
                    Data = "{\"messageId\":\"current-message\",\"delta\":\"current \"}",
                    RecordedAt = now.AddMilliseconds(4).ToString("O"),
                },
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 6,
                    EventType = "delta",
                    Data = "{\"messageId\":\"current-message\",\"delta\":\"answer\"}",
                    RecordedAt = now.AddMilliseconds(5).ToString("O"),
                });
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
            db.SessionEventLogs.AddRange(
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 1,
                    EventType = "metadata",
                    Data = "{\"messageId\":\"stale-message\"}",
                    RecordedAt = stale.ToString("O"),
                },
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 2,
                    EventType = "delta",
                    Data = "{\"messageId\":\"stale-message\",\"delta\":\"stale output\"}",
                    RecordedAt = stale.AddSeconds(1).ToString("O"),
                },
                new SessionEventLogEntity
                {
                    SessionId = session.SessionId,
                    WorkspaceId = "default",
                    SequenceNum = 3,
                    EventType = "tool_call",
                    Data = "{\"messageId\":\"stale-message\",\"name\":\"file_search\"}",
                    RecordedAt = stale.AddSeconds(2).ToString("O"),
                });
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

    private sealed record AgentConversationViewDto(
        string WorkspaceId,
        string OwnerUserId,
        string AgentId,
        string MainSessionId,
        List<ConversationMessageViewDto> Messages,
        AgentRunViewDto? ActiveRun,
        long EventCursor,
        string UpdatedAt);

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
        string? RunId,
        string Role,
        string SourceId,
        string SourceName,
        string CreatedAt,
        string Content,
        string Status,
        List<ProcessSummaryItemDto> ProcessItems);
}
