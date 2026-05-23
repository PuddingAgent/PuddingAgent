using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PuddingWebApiTests;

/// <summary>
/// Session API 集成测试。
/// </summary>
[TestClass]
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
    public async Task GetContextHealth_Returns200_WithSessionId()
    {
        var sessionId = "health-session-1";

        var response = await _client.GetAsync($"/api/sessions/{sessionId}/context-health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(sessionId, doc.RootElement.GetProperty("sessionId").GetString());
        Assert.IsTrue(doc.RootElement.TryGetProperty("state", out _));
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
        Assert.AreEqual(sessionId, doc.RootElement.GetProperty("sessionId").GetString());
        Assert.AreEqual("Manual", doc.RootElement.GetProperty("mode").GetString());
        Assert.AreEqual("Full", doc.RootElement.GetProperty("level").GetString());
        Assert.AreEqual(0, doc.RootElement.GetProperty("compactedMessageCount").GetInt32());
    }
}
