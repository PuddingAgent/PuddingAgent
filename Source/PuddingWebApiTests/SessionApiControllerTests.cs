using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingWebApiTests;

/// <summary>
/// Session API 集成测试。
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SessionApiControllerTests
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

    // ── P0-1: 创建 Session — 正常 ─────────────────────
    [TestMethod]
    public async Task CreateSession_Returns201_WithValidData()
    {
        var payload = new { workspaceId = "default", agentTemplateId = "global:general-assistant", title = "测试会话" };
        var response = await _client.PostAsJsonAsync("/api/sessions", payload);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(session);
        Assert.IsNotNull(session!.SessionId);
        Assert.AreEqual("default", session.WorkspaceId);
        Assert.AreEqual("global:general-assistant", session.AgentTemplateId);
        Assert.AreEqual("测试会话", session.Title);
    }

    // ── P0-2: 创建 Session — 缺少必填字段 ─────────────
    [TestMethod]
    public async Task CreateSession_Returns400_WhenMissingRequiredFields()
    {
        var payload = new { title = "无必填字段" };
        var response = await _client.PostAsJsonAsync("/api/sessions", payload);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── P0-3: 创建 Session — 无鉴权 ──────────────────
    [TestMethod]
    public async Task CreateSession_Returns401_WithoutAuth()
    {
        using var client = _factory.CreateClient();
        var payload = new { workspaceId = "default", agentTemplateId = "global:general-assistant", title = "测试" };
        var response = await client.PostAsJsonAsync("/api/sessions", payload);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task EnsureMainSession_ReturnsWorkspaceAgentMainSession()
    {
        var payload = new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = "agent-alpha",
            agentTemplateId = "global:general-assistant",
            title = "General Assistant"
        };

        var response = await _client.PostAsJsonAsync("/api/sessions/main", payload);

        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(session);
        Assert.AreEqual("default", session!.WorkspaceId);
        Assert.AreEqual("agent-alpha", session.AgentInstanceId);
        Assert.AreEqual("Main", session.SessionRole);
        Assert.AreEqual("agent", session.PrincipalKind);
        Assert.AreEqual("agent-alpha", session.PrincipalId);
    }

    [TestMethod]
    public async Task EnsureMainSession_IsIdempotentForWorkspaceAgent()
    {
        var payload = new
        {
            workspaceId = "default",
            principalKind = "agent",
            principalId = "agent-alpha",
            agentTemplateId = "global:general-assistant",
            title = "General Assistant"
        };

        var firstResp = await _client.PostAsJsonAsync("/api/sessions/main", payload);
        var secondResp = await _client.PostAsJsonAsync("/api/sessions/main", payload);

        firstResp.EnsureSuccessStatusCode();
        secondResp.EnsureSuccessStatusCode();

        var first = await firstResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        var second = await secondResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);

        Assert.AreEqual(first!.SessionId, second!.SessionId);
    }

    // ── P0-4: 查询会话列表 ──────────────────────────
    [TestMethod]
    public async Task ListSessions_Returns200_WithItems()
    {
        await _client.PostAsJsonAsync("/api/sessions", new { workspaceId = "default", agentTemplateId = "global:general-assistant" });

        var response = await _client.GetAsync("/api/sessions?workspaceId=default");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<SessionDto>>(JsonOpts);
        Assert.IsNotNull(list);
        Assert.IsTrue(list!.Count >= 1);
    }

    [TestMethod]
    public async Task ListSessions_BackfillsTranscriptPrincipalFromMetadata()
    {
        var sid = $"transcript-principal-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.SessionEventLogs.AddRange(
                new SessionEventLogEntity
                {
                    SessionId = sid,
                    WorkspaceId = "default",
                    SequenceNum = 1,
                    EventType = "metadata",
                    Data = "{\"agent_id\":\"agent-tester\",\"source_id\":\"agent-tester\",\"source_name\":\"测试助手\"}",
                    RecordedAt = now.ToString("O"),
                },
                new SessionEventLogEntity
                {
                    SessionId = sid,
                    WorkspaceId = "default",
                    SequenceNum = 2,
                    EventType = "done",
                    Data = "{\"reply\":\"ok\"}",
                    RecordedAt = now.AddSeconds(1).ToString("O"),
                });
            db.ChatMessages.Add(new ChatMessageEntity
            {
                SessionId = sid,
                Role = "user",
                Content = "测试助手历史会话",
                CreatedAt = now.ToUnixTimeMilliseconds(),
            });
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/sessions?workspaceId=default");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<SessionDto>>(JsonOpts);
        var session = list!.FirstOrDefault(s => s.SessionId == sid);
        Assert.IsNotNull(session);
        Assert.AreEqual("agent", session!.PrincipalKind);
        Assert.AreEqual("agent-tester", session.PrincipalId);
    }

    // ── P0-5: 查询会话列表 — 过滤 Frozen ─────────────
    [TestMethod]
    public async Task ListSessions_ExcludesFrozen()
    {
        var createResp = await _client.PostAsJsonAsync("/api/sessions", new { workspaceId = "default", agentTemplateId = "global:general-assistant" });
        var created = await createResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        await _client.PostAsync($"/api/sessions/{created!.SessionId}/archive", null);

        var listResp = await _client.GetAsync("/api/sessions?workspaceId=default");
        var list = await listResp.Content.ReadFromJsonAsync<List<SessionDto>>(JsonOpts);
        Assert.IsFalse(list!.Any(s => s.SessionId == created.SessionId));
    }

    // ── P0-6: 查询单个会话 ──────────────────────────
    [TestMethod]
    public async Task GetSession_Returns200_WithCorrectData()
    {
        var createResp = await _client.PostAsJsonAsync("/api/sessions", new { workspaceId = "default", agentTemplateId = "global:general-assistant", title = "查找测试" });
        var created = await createResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);

        var response = await _client.GetAsync($"/api/sessions/{created!.SessionId}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.AreEqual("查找测试", session!.Title);
    }

    // ── P0-7: 查询单个会话 — 不存在 ─────────────────
    [TestMethod]
    public async Task GetSession_Returns404_WhenNotFound()
    {
        var response = await _client.GetAsync("/api/sessions/nonexistent-id-12345");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── P0-8: 重命名会话 ────────────────────────────
    [TestMethod]
    public async Task RenameSession_Returns200_WithUpdatedTitle()
    {
        var createResp = await _client.PostAsJsonAsync("/api/sessions", new { workspaceId = "default", agentTemplateId = "global:general-assistant", title = "旧标题" });
        var created = await createResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);

        var renameResp = await _client.PutAsJsonAsync($"/api/sessions/{created!.SessionId}/title", new { title = "新标题" });
        Assert.AreEqual(HttpStatusCode.OK, renameResp.StatusCode);
        var updated = await renameResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.AreEqual("新标题", updated!.Title);
    }

    // ── P0-9: 归档会话 ──────────────────────────────
    [TestMethod]
    public async Task ArchiveSession_Returns200_WithFrozenStatus()
    {
        var createResp = await _client.PostAsJsonAsync("/api/sessions", new { workspaceId = "default", agentTemplateId = "global:general-assistant" });
        var created = await createResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);

        var archiveResp = await _client.PostAsync($"/api/sessions/{created!.SessionId}/archive", null);
        Assert.AreEqual(HttpStatusCode.OK, archiveResp.StatusCode);
        var archived = await archiveResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.AreEqual(4, archived!.Status);
    }

    // ── P0-10: 删除会话 ─────────────────────────────
    [TestMethod]
    public async Task DeleteSession_Returns204()
    {
        var createResp = await _client.PostAsJsonAsync("/api/sessions", new { workspaceId = "default", agentTemplateId = "global:general-assistant" });
        var created = await createResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);

        var deleteResp = await _client.DeleteAsync($"/api/sessions/{created!.SessionId}");
        Assert.AreEqual(HttpStatusCode.NoContent, deleteResp.StatusCode);
    }

    // ── P0-11: 删除后查询返回 404 ───────────────────
    [TestMethod]
    public async Task GetSession_Returns404_AfterDelete()
    {
        var createResp = await _client.PostAsJsonAsync("/api/sessions", new { workspaceId = "default", agentTemplateId = "global:general-assistant" });
        var created = await createResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        await _client.DeleteAsync($"/api/sessions/{created!.SessionId}");

        var response = await _client.GetAsync($"/api/sessions/{created.SessionId}");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task DeleteSession_RemovesTranscriptBackfillArtifacts()
    {
        var createResp = await _client.PostAsJsonAsync("/api/sessions", new { workspaceId = "default", agentTemplateId = "global:general-assistant", title = "待删除会话" });
        var created = await createResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        var sid = created!.SessionId;
        var now = DateTimeOffset.UtcNow;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.ChatMessages.Add(new ChatMessageEntity
            {
                SessionId = sid,
                Role = "user",
                Content = "删除后不应通过 transcript backfill 恢复",
                CreatedAt = now.ToUnixTimeMilliseconds(),
            });
            db.SessionEventLogs.Add(new SessionEventLogEntity
            {
                SessionId = sid,
                WorkspaceId = "default",
                SequenceNum = 1,
                EventType = "done",
                Data = "{\"reply\":\"deleted\"}",
                RecordedAt = now.ToString("O"),
            });
            await db.SaveChangesAsync();
        }

        var deleteResp = await _client.DeleteAsync($"/api/sessions/{sid}");
        Assert.AreEqual(HttpStatusCode.NoContent, deleteResp.StatusCode);

        var listResp = await _client.GetAsync("/api/sessions?workspaceId=default");
        Assert.AreEqual(HttpStatusCode.OK, listResp.StatusCode);
        var list = await listResp.Content.ReadFromJsonAsync<List<SessionDto>>(JsonOpts);
        Assert.IsFalse(list!.Any(s => s.SessionId == sid));

        var getResp = await _client.GetAsync($"/api/sessions/{sid}");
        Assert.AreEqual(HttpStatusCode.NotFound, getResp.StatusCode);
    }

    // ── P0-18: 重复删除 — 幂等 ──────────────────────
    [TestMethod]
    public async Task DeleteSession_IsIdempotent()
    {
        var createResp = await _client.PostAsJsonAsync("/api/sessions", new { workspaceId = "default", agentTemplateId = "global:general-assistant" });
        var created = await createResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        await _client.DeleteAsync($"/api/sessions/{created!.SessionId}");

        var secondDelete = await _client.DeleteAsync($"/api/sessions/{created.SessionId}");
        Assert.AreEqual(HttpStatusCode.NoContent, secondDelete.StatusCode);
    }

    [TestMethod]
    public async Task GetContextHealth_Returns409_WhenContextWindowCannotBeResolved()
    {
        var sessionId = "health-session-1";

        var response = await _client.GetAsync($"/api/sessions/{sessionId}/context-health");

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("context_window_unresolved", doc.RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task CompactSession_Returns200_WithStringLevel()
    {
        var sessionId = "compact-session-1";

        var response = await _client.PostAsJsonAsync($"/api/sessions/{sessionId}/compact", new
        {
            workspaceId = "default",
            level = "Full",
            reason = "test compact",
        });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var compaction = doc.RootElement.GetProperty("compaction");
        Assert.AreEqual(sessionId, compaction.GetProperty("sessionId").GetString());
        Assert.AreEqual("Manual", compaction.GetProperty("mode").GetString());
        Assert.AreEqual("Full", compaction.GetProperty("level").GetString());
        Assert.AreEqual(0, compaction.GetProperty("compactedMessageCount").GetInt32());

        var diagnostics = compaction.GetProperty("diagnostics");
        Assert.AreEqual(sessionId, diagnostics.GetProperty("previousSessionId").GetString());
        Assert.AreEqual(0, diagnostics.GetProperty("compactedMessageCount").GetInt32());
        Assert.IsFalse(string.IsNullOrWhiteSpace(diagnostics.GetProperty("compactionId").GetString()));
        Assert.IsFalse(string.IsNullOrWhiteSpace(diagnostics.GetProperty("completedAtUtc").GetString()));
        Assert.AreEqual(doc.RootElement.GetProperty("newSessionId").GetString(), diagnostics.GetProperty("newSessionId").GetString());
    }

    [TestMethod]
    public async Task CompactSession_DoesNotStackCompactionPrefixInNewSessionTitle()
    {
        var createResp = await _client.PostAsJsonAsync("/api/sessions", new
        {
            workspaceId = "default",
            agentTemplateId = "global:general-assistant",
            title = "压缩 - 压缩 - mimo"
        });
        createResp.EnsureSuccessStatusCode();
        var oldSession = await createResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);

        var response = await _client.PostAsJsonAsync($"/api/sessions/{oldSession!.SessionId}/compact", new
        {
            workspaceId = "default",
            level = "Full",
            reason = "test compact",
        });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("压缩 - mimo", doc.RootElement.GetProperty("newSessionTitle").GetString());
        Assert.AreEqual(
            "压缩 - mimo",
            doc.RootElement
                .GetProperty("compaction")
                .GetProperty("diagnostics")
                .GetProperty("newSessionTitle")
                .GetString());
    }
}
