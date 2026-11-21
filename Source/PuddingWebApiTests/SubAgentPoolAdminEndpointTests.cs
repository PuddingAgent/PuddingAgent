using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingWebApiTests;

/// <summary>
/// <c>GET /api/admin/sub-agent-pool</c> 的**真实宿主**验收：控制器单测（
/// <see cref="SubAgentPoolAdminControllerTests"/>）只能证明方法体行为，
/// 证明不了「这个路由真的被 MVC 发现」「鉴权真的挂在管道上」「序列化出来的是 camelCase」。
/// 这三件事恰恰是前端联调最先撞上的，所以必须经 <see cref="CustomWebApplicationFactory"/> 打一次真 HTTP。
/// <para>
/// 与控制器单测的分工：**空池 200 的语义归单测**（确定性，不受共享夹具影响），
/// 这里只断言「200 且响应体是数组」，以及「字段名与三态值原样到 JSON」。
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SubAgentPoolAdminEndpointTests
{
    private static CustomWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    private const string Endpoint = "/api/admin/sub-agent-pool";

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
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _client?.Dispose();
    }

    /// <summary>不带 JWT ⇒ 401。池条目是运维面数据，不能匿名读。</summary>
    [TestMethod]
    public async Task Get_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync(Endpoint);

        Assert.AreEqual(
            HttpStatusCode.Unauthorized,
            response.StatusCode,
            "未带 Admin JWT 时必须 401，而不是 200 或 404");
    }

    /// <summary>带 Admin JWT ⇒ 200，且响应体是数组（空池也不例外，绝不 404）。</summary>
    [TestMethod]
    public async Task Get_WithAdminJwt_Returns200AndJsonArray()
    {
        JwtHelper.SetBearerToken(_client);

        var response = await _client.GetAsync(Endpoint);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"响应体: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual(
            JsonValueKind.Array,
            doc.RootElement.ValueKind,
            $"响应体必须是 JSON 数组（空池也是 []）；实际: {body}");
    }

    /// <summary>
    /// 预置一行后经真实管道读回：证明三列（modelId / avatarKey / responsibility）与
    /// <c>lastSuccess</c> 三态都能原样到前端，且**不存在** <c>lastSuccessAt</c>
    /// （库里没有该列，凭空返回恒 null 的时间字段会让界面永远显示「从未成功」）。
    /// </summary>
    [TestMethod]
    public async Task Get_WithSeededEntry_MapsContractFieldsToCamelCase()
    {
        var poolId = $"e2e-pool-{Guid.NewGuid():N}";
        var subSessionId = $"e2e-sub-{Guid.NewGuid():N}";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.AgentPool.Add(new AgentPoolEntity
            {
                PoolId = poolId,
                OwnerWorkspaceId = "default",
                Name = poolId,
                SubSessionId = subSessionId,
                TemplateId = "global:general-assistant",
                Role = "coding",
                ModelId = "deepseek/deepseek-flash",
                AvatarKey = "avatar-fox",
                Responsibility = "端到端夹具条目",
                Status = AgentPoolEntity.StatusIdle,
                TaskCount = 3,
                LastSuccess = true,
                CreatedAt = "2026-11-21T00:00:00Z",
                LastUsedAt = "2026-11-21T01:00:00Z",
            });
            await db.SaveChangesAsync();
        }

        JwtHelper.SetBearerToken(_client);

        var response = await _client.GetAsync(Endpoint);
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"响应体: {body}");

        using var doc = JsonDocument.Parse(body);
        var entry = doc.RootElement.EnumerateArray()
            .FirstOrDefault(e => e.TryGetProperty("poolId", out var id) && id.GetString() == poolId);

        Assert.AreNotEqual(
            JsonValueKind.Undefined,
            entry.ValueKind,
            $"预置条目必须出现在列表里；响应体: {body}");

        Assert.AreEqual("coding", entry.GetProperty("role").GetString());
        Assert.AreEqual("端到端夹具条目", entry.GetProperty("responsibility").GetString());
        Assert.AreEqual("avatar-fox", entry.GetProperty("avatarKey").GetString());
        Assert.AreEqual("deepseek/deepseek-flash", entry.GetProperty("modelId").GetString());
        Assert.AreEqual(AgentPoolEntity.StatusIdle, entry.GetProperty("status").GetString());
        Assert.AreEqual(3, entry.GetProperty("taskCount").GetInt32());
        Assert.IsTrue(entry.GetProperty("lastSuccess").GetBoolean(), "lastSuccess 三态必须原样透出 true");
        Assert.IsFalse(
            entry.TryGetProperty("lastSuccessAt", out _),
            "库里没有 last_success_at 列，不得凭空返回该字段（恒 null 会被界面读成「从未成功」）");
        Assert.AreEqual(subSessionId, entry.GetProperty("subSessionId").GetString());
    }
}
