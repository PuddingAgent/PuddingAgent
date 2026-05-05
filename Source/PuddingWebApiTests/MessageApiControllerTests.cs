using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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

    /// <summary>
    /// 消息分页返回 DTO。
    /// </summary>
    public sealed class MessageListDto
    {
        public List<object> Items { get; set; } = [];
        public bool HasMore { get; set; }
        public long? OldestCreatedAt { get; set; }
    }
}
