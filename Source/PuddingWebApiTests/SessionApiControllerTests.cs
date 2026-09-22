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
            db.ConversationCatalogs.Add(new ConversationCatalogEntity
            {
                ConversationId = sid,
                WorkspaceId = "default",
                PrincipalId = "agent-tester",
                Title = "测试助手历史会话",
                Status = "idle",
                CreatedAt = now.ToString("O"),
                LastActiveAt = now.AddSeconds(1).ToString("O"),
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
            db.ConversationCatalogs.Add(new ConversationCatalogEntity
            {
                ConversationId = sid,
                WorkspaceId = "default",
                PrincipalId = "agent-tester",
                Title = "待删除会话",
                Status = "idle",
                CreatedAt = now.ToString("O"),
                LastActiveAt = now.AddSeconds(1).ToString("O"),
            });
            await db.SaveChangesAsync();
        }

        var deleteResp = await _client.DeleteAsync($"/api/sessions/{sid}");
        Assert.AreEqual(HttpStatusCode.NoContent, deleteResp.StatusCode);

        using (var verifyScope = _factory.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            Assert.IsFalse(db.ConversationCatalogs.Any(c => c.ConversationId == sid));
        }

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
        // 现行 /compact 契约：AgentId 必填（空值直接 400 agent_id_required，
        // SessionEventsController.cs:715），且后继会话要求源会话真实存在
        // （CompactionSessionSuccessor.cs:26）。因此先物化一份可解析的 Agent 身份 + 一个真实会话；
        // 用例本意（字符串 level 绑定 + 完整 envelope 契约）不变。
        var agentId = await EnsureCompactTestAgentAsync();
        var sessionId = await CreateCompactSessionAsync("compact-string-level");

        var response = await _client.PostAsJsonAsync($"/api/sessions/{sessionId}/compact", new
        {
            workspaceId = "default",
            agentId,
            level = "Full",
            reason = "test compact",
        });

        Assert.AreEqual(
            HttpStatusCode.OK,
            response.StatusCode,
            await response.Content.ReadAsStringAsync());
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

        // 同因：/compact 现行契约需要 agentId，且服务端会用真实运行时档案解析它。
        var agentId = await EnsureCompactTestAgentAsync();

        var response = await _client.PostAsJsonAsync($"/api/sessions/{oldSession!.SessionId}/compact", new
        {
            workspaceId = "default",
            agentId,
            level = "Full",
            reason = "test compact",
        });

        Assert.AreEqual(
            HttpStatusCode.OK,
            response.StatusCode,
            await response.Content.ReadAsStringAsync());
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

    /// <summary>
    /// /compact 现行契约要求请求携带 agentId，服务端会用真实 AgentRuntimeProfileResolver
    /// 解析该 Agent 的运行时档案，而档案里的 preferredProviderId / preferredModelId 必须
    /// 已注册在 data/config/llm.providers.json（否则 AgentConfigurationException ⇒ 400）。
    /// 测试宿主是全新隔离数据根，默认既无 Agent 也无 Provider，因此这里通过公开 API 现场
    /// 物化一份最小可解析身份：provider → 模型 → 引用它们的 workspace agent 实例。
    /// 只做测试夹具准备，不改变任何被验证的产品语义。
    /// </summary>
    private async Task<string> EnsureCompactTestAgentAsync()
    {
        if (_compactTestAgentId is not null)
            return _compactTestAgentId;

        const string providerId = "compact-test-provider";
        const string modelId = "compact-test-model";

        var providerResponse = await _client.PostAsJsonAsync("/api/llm/providers", new
        {
            providerId,
            name = "Compact Test Provider",
            baseUrl = "https://api.example.com/v1",
            apiKey = "test-key",
            description = "压缩契约测试用 provider",
            isEnabled = true,
        });
        Assert.IsTrue(
            providerResponse.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
            $"物化压缩测试 provider 失败：{(int)providerResponse.StatusCode} {await providerResponse.Content.ReadAsStringAsync()}");

        var modelResponse = await _client.PostAsJsonAsync(
            $"/api/llm/providers/{providerId}/models",
            new
            {
                modelId,
                name = modelId,
                protocol = "openai",
                description = "压缩契约测试用模型",
                maxContextTokens = 8192,
                maxOutputTokens = 2048,
                inputPricePer1MTokens = 0m,
                outputPricePer1MTokens = 0m,
                cacheHitPricePer1MTokens = 0m,
                capabilityTags = Array.Empty<string>(),
                isDeprecated = false,
                isDefault = true,
                isEmbedding = false,
                sortOrder = 0,
            });
        Assert.IsTrue(
            modelResponse.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
            $"物化压缩测试模型失败：{(int)modelResponse.StatusCode} {await modelResponse.Content.ReadAsStringAsync()}");

        var agentResponse = await _client.PostAsJsonAsync("/api/workspaces/default/agents", new
        {
            name = "compact-contract-agent",
            displayName = "compact-contract-agent",
            sourceTemplateId = "global:general-assistant",
            preferredProviderId = providerId,
            preferredModelId = modelId,
        });
        Assert.AreEqual(
            HttpStatusCode.Created,
            agentResponse.StatusCode,
            await agentResponse.Content.ReadAsStringAsync());
        var agent = await agentResponse.Content.ReadFromJsonAsync<WorkspaceAgentIdDto>(JsonOpts);
        Assert.IsNotNull(agent);
        Assert.IsFalse(string.IsNullOrWhiteSpace(agent!.AgentId));
        _compactTestAgentId = agent.AgentId;
        return _compactTestAgentId;
    }

    private async Task<string> CreateCompactSessionAsync(string titlePrefix)
    {
        var createResponse = await _client.PostAsJsonAsync("/api/sessions", new
        {
            workspaceId = "default",
            agentTemplateId = "global:general-assistant",
            title = $"{titlePrefix}-{Guid.NewGuid():N}",
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(created);
        return created!.SessionId;
    }

    private static string? _compactTestAgentId;

    private sealed record WorkspaceAgentIdDto(string AgentId);
}
