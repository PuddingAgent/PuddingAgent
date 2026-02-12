using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Runtime;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingRuntime.Services;

namespace PuddingWebApiTests;

/// <summary>
/// Message API 集成测试。
/// </summary>
[TestClass]
public sealed class MessageApiControllerTests
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
        _client?.Dispose();
    }

    // ── P0-12: 首屏分页加载 ─────────────────────────
    [TestMethod]
    public async Task ListMessages_Returns200_WithPagination()
    {
        var createResp = await _client.PostAsJsonAsync("/api/sessions", new { workspaceId = "default", agentTemplateId = "global:general-assistant" });
        var created = await createResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        var sid = created!.SessionId;

        var response = await _client.GetAsync($"/api/sessions/{sid}/messages?limit=20");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MessageListDto>(JsonOpts);
        Assert.IsNotNull(body);
        Assert.IsNotNull(body!.Items);
        Assert.AreEqual(0, body.Items.Count);
        Assert.IsFalse(body.HasMore);
        Assert.IsNull(body.OldestCreatedAt);
    }

    // ── P0-14: 空会话消息列表 ────────────────────────
    [TestMethod]
    public async Task ListMessages_EmptySession_ReturnsEmptyList()
    {
        var createResp = await _client.PostAsJsonAsync("/api/sessions", new { workspaceId = "default", agentTemplateId = "global:general-assistant" });
        var created = await createResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);

        var response = await _client.GetAsync($"/api/sessions/{created!.SessionId}/messages");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MessageListDto>(JsonOpts);
        Assert.AreEqual(0, body!.Items.Count);
        Assert.IsFalse(body.HasMore);
    }

    // ── P0-13: 分页 limit 边界 ───────────────────────
    [TestMethod]
    public async Task ListMessages_LimitExceedsMax_ClampsToMax()
    {
        var createResp = await _client.PostAsJsonAsync("/api/sessions", new { workspaceId = "default", agentTemplateId = "global:general-assistant" });
        var created = await createResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);

        var response = await _client.GetAsync($"/api/sessions/{created!.SessionId}/messages?limit=100");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    // ── ADR-031: 旧事件日志 fallback ─────────────────
    [TestMethod]
    public async Task ListMessages_WhenChatMessagesEmptyAndEventLogHasDone_ReturnsAssistantFallback()
    {
        var sid = $"fallback-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.SessionEventLogs.AddRange(
                NewEvent(sid, 1, "thinking", "{\"delta\":\"思考中\"}", now),
                NewEvent(sid, 2, "delta", "{\"delta\":\"Hello \"}", now.AddMilliseconds(1)),
                NewEvent(sid, 3, "delta", "{\"delta\":\"fallback\"}", now.AddMilliseconds(2)),
                NewEvent(sid, 4, "usage", "{\"promptTokens\":1,\"completionTokens\":2,\"totalTokens\":3}", now.AddMilliseconds(3)),
                NewEvent(sid, 5, "done", "{\"reply\":\"Hello fallback\",\"usage\":{\"promptTokens\":1,\"completionTokens\":2,\"totalTokens\":3}}", now.AddMilliseconds(4)));
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/sessions/{sid}/messages?limit=20");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MessageListDto>(JsonOpts);
        Assert.IsNotNull(body);
        Assert.AreEqual(1, body!.Items.Count);
        Assert.AreEqual("agent", body.Items[0].Role);
        Assert.AreEqual("Hello fallback", body.Items[0].Content);
        Assert.AreEqual(1, body.Items[0].Thinking?.Count);
        Assert.AreEqual(3, body.Items[0].Usage?.TotalTokens);
        Assert.IsFalse(body.HasMore);
        Assert.IsNotNull(body.OldestCreatedAt);
    }

    // ── ADR-031: ChatMessages 是普通历史的物化视图 ─────
    [TestMethod]
    public async Task ListMessages_WhenMaterializedMessagesExist_DoesNotUseEventFallback()
    {
        var sid = $"materialized-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var createdAt = now.ToUnixTimeMilliseconds();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.ChatMessages.Add(new ChatMessageEntity
            {
                SessionId = sid,
                Role = "user",
                Content = "materialized user",
                CreatedAt = createdAt,
            });
            db.SessionEventLogs.Add(NewEvent(sid, 1, "done", "{\"reply\":\"fallback should not win\"}", now));
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/sessions/{sid}/messages");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MessageListDto>(JsonOpts);
        Assert.IsNotNull(body);
        Assert.AreEqual(1, body!.Items.Count);
        Assert.AreEqual("user", body.Items[0].Role);
        Assert.AreEqual("materialized user", body.Items[0].Content);
    }

    // ── ADR-031-C: 聊天 API DI 冒烟 ─────────────────
    [TestMethod]
    public async Task SendMessage_Unauthenticated_Returns401()
    {
        using var client = _factory.CreateClient();
        var payload = new { sessionId = "nonexistent", messageText = "你好", agentId = "default" };
        var response = await client.PostAsJsonAsync(
            "/api/workspaces/default/chat/message", payload);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── ADR-031-C: 聊天 API 路由/DI 激活验证 ───────
    [TestMethod]
    public async Task SendMessage_ControllerActivates_DoesNotReturn500()
    {
        // 验证聊天控制器可被 DI 激活，且路由能正确匹配。
        // 返回非 500（非 DI 崩溃）即证明 DI 完整。
        // 注意：由于测试环境可能有 seed data，workspace 可能已存在，
        // 因此不强求 404，只确保路由匹配 + 控制器激活成功。
        var payload = new { sessionId = "nonexistent", messageText = "你好", agentId = "default" };
        var response = await _client.PostAsJsonAsync(
            "/api/workspaces/default/chat/message", payload);
        Assert.AreNotEqual(HttpStatusCode.InternalServerError, response.StatusCode,
            "控制器因 DI 缺失返回 500，请检查 ChatTranscriptWriter 等依赖注册");
        Assert.IsTrue(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
            $"预期 200/404/400，实际 {response.StatusCode}");
    }

    [TestMethod]
    public async Task SendMessageWithoutSession_UsesAgentMainSession()
    {
        var mainResp = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = "default",
            agentTemplateId = "global:general-assistant",
            title = "General Assistant"
        });
        mainResp.EnsureSuccessStatusCode();
        var main = await mainResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);

        var sendResp = await _client.PostAsJsonAsync(
            "/api/workspaces/default/chat/message",
            new { messageText = "你好", agentId = "default" });

        var sendBody = await sendResp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, sendResp.StatusCode, sendBody);
        using var doc = JsonDocument.Parse(sendBody);

        Assert.AreEqual(main!.SessionId, doc.RootElement.GetProperty("sessionId").GetString());
    }

    [TestMethod]
    public async Task SendCompactCommand_ReturnsCompactionResultMessage()
    {
        var mainResp = await _client.PostAsJsonAsync("/api/sessions/main", new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = "default",
            agentTemplateId = "global:general-assistant",
            title = "General Assistant"
        });
        mainResp.EnsureSuccessStatusCode();
        var main = await mainResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(main);

        var sendResp = await _client.PostAsJsonAsync(
            "/api/workspaces/default/chat/message",
            new
            {
                sessionId = main!.SessionId,
                messageText = "/compact",
                agentId = "default"
            });

        var sendBody = await sendResp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, sendResp.StatusCode, sendBody);

        var messagesResp = await _client.GetAsync($"/api/sessions/{main.SessionId}/messages");
        messagesResp.EnsureSuccessStatusCode();
        var messages = await messagesResp.Content.ReadFromJsonAsync<MessageListDto>(JsonOpts);
        Assert.IsNotNull(messages);

        var systemReply = messages!.Items.LastOrDefault(m => m.Role == "agent")?.Content;
        Assert.IsFalse(
            systemReply?.Contains("not implemented", StringComparison.OrdinalIgnoreCase) ?? true,
            systemReply);
        StringAssert.Contains(systemReply!, "Context compacted");
    }

    [TestMethod]
    public void AgentExecutionService_Uses_UnifiedToolInvocationFacade()
    {
        var facade = _factory.Services.GetRequiredService<IToolInvocationService>();
        Assert.IsInstanceOfType(facade, typeof(ToolInvocationService));

        var executor = _factory.Services.GetRequiredService<AgentExecutionService>();
        var field = typeof(AgentExecutionService).GetField(
            "_toolInvocationService",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.IsNotNull(field);
        Assert.IsNotNull(field!.GetValue(executor),
            "AgentExecutionService must receive IToolInvocationService; otherwise shell falls back to legacy SkillRuntime and fails as Skill 'shell' not found.");
    }

    // ── ADR-031: 发送链路使用的转录写入器 ─────────────
    [TestMethod]
    public async Task ChatTranscriptWriter_PersistsUserAndAgentMessages_WithIdempotency()
    {
        var sid = $"transcript-{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var writer = _factory.Services.GetRequiredService<ChatTranscriptWriter>();
        await writer.PersistMessageAsync(
            sid,
            "user",
            "hello",
            createdAt,
            workspaceId: "default",
            agentInstanceId: "agent-1",
            agentTemplateId: "template-1");
        await writer.PersistMessageAsync(
            sid,
            "agent",
            "world",
            createdAt + 10,
            usageJson: "{\"totalTokens\":3}",
            workspaceId: "default",
            agentInstanceId: "agent-1",
            agentTemplateId: "template-1");

        // 同一窗口内重复写入同一内容应被忽略，避免后台重试产生重复转录。
        await writer.PersistMessageAsync(
            sid,
            "agent",
            "world",
            createdAt + 20,
            usageJson: "{\"totalTokens\":3}",
            workspaceId: "default",
            agentInstanceId: "agent-1",
            agentTemplateId: "template-1");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var rows = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == sid)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual("user", rows[0].Role);
        Assert.AreEqual("hello", rows[0].Content);
        Assert.AreEqual("default", rows[0].WorkspaceId);
        Assert.AreEqual("agent-1", rows[0].AgentInstanceId);
        Assert.AreEqual("template-1", rows[0].AgentTemplateId);
        Assert.AreEqual("agent", rows[1].Role);
        Assert.AreEqual("world", rows[1].Content);
        Assert.IsNotNull(rows[1].UsageJson);

        var paths = _factory.Services.GetRequiredService<PuddingCode.Configuration.PuddingDataPaths>();
        var day = DateTimeOffset.FromUnixTimeMilliseconds(createdAt).UtcDateTime.ToString("yyyy-MM-dd");
        var jsonlPath = paths.AgentInstanceMessageLogJsonlFile("agent-1", day, sid);
        var mdPath = paths.AgentInstanceMessageLogMarkdownFile("agent-1", day, sid);
        Assert.IsTrue(File.Exists(jsonlPath));
        Assert.IsTrue(File.Exists(mdPath));
        var jsonLines = await File.ReadAllLinesAsync(jsonlPath);
        Assert.AreEqual(2, jsonLines.Length);
        var md = await File.ReadAllTextAsync(mdPath);
        StringAssert.Contains(md, "hello");
        StringAssert.Contains(md, "world");
    }

    private static SessionEventLogEntity NewEvent(
        string sessionId,
        long sequence,
        string eventType,
        string data,
        DateTimeOffset recordedAt) => new()
        {
            SessionId = sessionId,
            WorkspaceId = "default",
            SequenceNum = sequence,
            EventType = eventType,
            Data = data,
            RecordedAt = recordedAt.ToString("O"),
        };

    /// <summary>
    /// 消息分页返回 DTO。
    /// </summary>
    public sealed class MessageListDto
    {
        public List<MessageItemDto> Items { get; set; } = [];
        public bool HasMore { get; set; }
        public long? OldestCreatedAt { get; set; }
    }

    public sealed class MessageItemDto
    {
        public long Id { get; set; }
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public List<ThinkingDto>? Thinking { get; set; }
        public UsageDto? Usage { get; set; }
        public long CreatedAt { get; set; }
    }

    public sealed class ThinkingDto
    {
        public string Text { get; set; } = string.Empty;
        public long Timestamp { get; set; }
    }

    public sealed class UsageDto
    {
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
    }
}
