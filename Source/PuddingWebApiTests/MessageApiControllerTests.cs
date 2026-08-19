using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Runtime;
using PuddingCode.Services;
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

    // ── P1-3 T3: ThinkingJson 读侧双格式解码 ───────────
    [TestMethod]
    public async Task ListMessages_DecodesLegacyThinkingJson_WithExactTextAndTimestamp()
    {
        var sid = $"thinking-legacy-{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.ChatMessages.Add(new ChatMessageEntity
            {
                MessageId = $"legacy-{Guid.NewGuid():N}",
                SessionId = sid,
                Role = "agent",
                Content = "legacy answer",
                ThinkingJson = """[{"text":"第一步分析","timestamp":1000},{"text":"第二步检索","timestamp":2000}]""",
                CreatedAt = createdAt,
            });
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/sessions/{sid}/messages");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MessageListDto>(JsonOpts);
        Assert.IsNotNull(body);
        var item = body!.Items.Single();
        Assert.IsNotNull(item.Thinking);
        Assert.HasCount(2, item.Thinking!);
        Assert.AreEqual("第一步分析", item.Thinking![0].Text);
        Assert.AreEqual(1000, item.Thinking[0].Timestamp);
        Assert.AreEqual("第二步检索", item.Thinking[1].Text);
        Assert.AreEqual(2000, item.Thinking[1].Timestamp);
    }

    [TestMethod]
    public async Task ListMessages_DecodesCompactThinkingJson_WithChineseUtf8Boundaries()
    {
        var sid = $"thinking-compact-{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 中文多字节用例：chunk 边界经过汉字（UTF-8 3 字节/字），必须按字节偏移还原不产生乱码。
        var compactJson = ReasoningCompactCodec.Encode(
            "分析用户需求并检索文件",
            new[]
            {
                new ReasoningCompactCodec.ThinkingChunk("分析", 1000),
                new ReasoningCompactCodec.ThinkingChunk("用户需求", 2500),
                new ReasoningCompactCodec.ThinkingChunk("并检索文件", 3100),
            });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.ChatMessages.Add(new ChatMessageEntity
            {
                MessageId = $"compact-{Guid.NewGuid():N}",
                SessionId = sid,
                Role = "agent",
                Content = "compact answer",
                ThinkingJson = compactJson,
                CreatedAt = createdAt,
            });
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/sessions/{sid}/messages");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MessageListDto>(JsonOpts);
        Assert.IsNotNull(body);
        var item = body!.Items.Single();
        Assert.IsNotNull(item.Thinking);
        Assert.HasCount(3, item.Thinking!);
        Assert.AreEqual("分析", item.Thinking![0].Text);
        Assert.AreEqual(1000, item.Thinking[0].Timestamp);
        Assert.AreEqual("用户需求", item.Thinking[1].Text);
        Assert.AreEqual(2500, item.Thinking[1].Timestamp);
        Assert.AreEqual("并检索文件", item.Thinking[2].Text);
        Assert.AreEqual(3100, item.Thinking[2].Timestamp);
    }

    [TestMethod]
    public async Task ListMessages_CompactHashMismatch_FailsOpenWithEmptyThinking()
    {
        var sid = $"thinking-badhash-{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // v2 结构合法但 hash 与 text 不匹配 → 必须 fail-open：200 + 空 thinking，不抛异常。
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.ChatMessages.Add(new ChatMessageEntity
            {
                MessageId = $"badhash-{Guid.NewGuid():N}",
                SessionId = sid,
                Role = "agent",
                Content = "tampered answer",
                ThinkingJson = """{"v":2,"text":"被篡改内容","chunks":[{"o":0,"t":1000}],"hash":"deadbeefdeadbeefdeadbeefdeadbeef"}""",
                CreatedAt = createdAt,
            });
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/sessions/{sid}/messages");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MessageListDto>(JsonOpts);
        Assert.IsNotNull(body);
        var item = body!.Items.Single();
        Assert.IsNotNull(item.Thinking);
        Assert.AreEqual(0, item.Thinking!.Count);
    }

    // ── ADR-059: Conversation command endpoint authentication ──
    [TestMethod]
    public async Task SendMessage_Unauthenticated_Returns401()
    {
        using var client = _factory.CreateClient();
        var payload = NewTurnPayload("你好");
        var response = await client.PostAsJsonAsync(
            $"/api/v1/conversations/{NewConversationId()}/turns", payload);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── ADR-059: Conversation command endpoint route/DI validation ──
    [TestMethod]
    public async Task SendMessage_ControllerActivates_DoesNotReturn500()
    {
        var conversationId = NewConversationId();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/turns");
        request.Headers.Add("X-Workspace-Id", "default");
        request.Content = JsonContent.Create(NewTurnPayload("你好"));

        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, body);
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
        StringAssert.StartsWith(rows[0].MessageId, "transcript-");
        Assert.AreEqual("default", rows[0].WorkspaceId);
        Assert.AreEqual("agent-1", rows[0].AgentInstanceId);
        Assert.AreEqual("template-1", rows[0].AgentTemplateId);
        Assert.AreEqual("agent", rows[1].Role);
        Assert.AreEqual("world", rows[1].Content);
        StringAssert.StartsWith(rows[1].MessageId, "transcript-");
        Assert.AreNotEqual(rows[0].MessageId, rows[1].MessageId);
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

    private static object NewTurnPayload(string text) => new
    {
        clientRequestId = $"request-{Guid.NewGuid():N}",
        clientMessageId = $"message-{Guid.NewGuid():N}",
        recipients = new { type = "agent", agentIds = new[] { "default" } },
        content = new[] { new { type = "text", text } },
    };

    private static string NewConversationId() => $"conversation-{Guid.NewGuid():N}";

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
