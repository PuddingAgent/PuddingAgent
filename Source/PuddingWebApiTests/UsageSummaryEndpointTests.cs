using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingWebApiTests;

/// <summary>
/// <c>GET /api/admin/usage/summary</c> 的**真实宿主**验收（可观测性 P0-c）。
/// <para>
/// 走 <see cref="CustomWebApplicationFactory"/> 打真 HTTP，而不是直接调方法：
/// 只有这样才能证明「路由被 MVC 发现」「<c>[Authorize]</c> 真的挂在管道上」
/// 「响应是真 camelCase JSON」，这三件事恰恰是前端联调最先撞上的。
/// </para>
/// <para>
/// 隔离手法：每个用例用自己的**历史时间窗**（2024~2025 年的固定区间）取数，
/// 于是同一共享夹具里其他用例写入的行不会落进本用例的窗口，断言保持确定性。
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class UsageSummaryEndpointTests
{
    private const string SummaryPath = "/api/admin/usage/summary";

    private static CustomWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

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

    // ───────────────────────────── 用例 1：model 聚合数值 + 加权命中率 ─────────────────────────────

    /// <summary>
    /// <c>groupBy=model</c> 按 provider/model 聚合，且各列数值正确。
    /// <para>
    /// 同一用例同时钉住「加权命中率」：两行的 per-row <c>CacheHitRate</c> 列被故意写成
    /// 0.9 / 0.1（算术平均 0.5、按 hit/(hit+miss) 算是 0.0333），而正确答案是
    /// sum(hit)/sum(eligible) = 10/1000 = 0.01。三种口径互不相等，任何「换个算法」的改动都会红。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task GroupByModel_AggregatesColumnsAndUsesWeightedCacheHitRate()
    {
        await SeedAsync(
            // p-a/m-a：两行，命中/可算 token 严重不均衡 —— 专门用来区分加权与算术平均。
            NewRow("p-a", "m-a", createdAt: At(2025, 1, 1, 1), prompt: 100, completion: 10,
                hit: 10, miss: 90, eligible: 1000,
                input: 1.0m, output: 0.5m, hitCost: 0.25m, total: 1.75m, hitRate: 0.9),
            NewRow("p-a", "m-a", createdAt: At(2025, 1, 1, 2), prompt: 200, completion: 20,
                hit: 0, miss: 200, eligible: 0,
                input: 0m, output: 0m, hitCost: 0m, total: 0m, hitRate: 0.1),
            // p-b/m-b：可算 token 为 0 —— 命中率必须 null。
            NewRow("p-b", "m-b", createdAt: At(2025, 1, 1, 3), prompt: 50, completion: 5,
                hit: 0, miss: 0, eligible: 0,
                input: 0.5m, output: 0.1m, hitCost: 0m, total: 0.6m, hitRate: null));

        var body = await GetSummaryAsync("fromUtc=2025-01-01&toUtc=2025-01-02&groupBy=model");
        using var doc = JsonDocument.Parse(body);

        Assert.AreEqual("model", doc.RootElement.GetProperty("groupBy").GetString());
        var rows = doc.RootElement.GetProperty("rows");
        Assert.AreEqual(2, rows.GetArrayLength(), $"窗口内应只有 2 个分组模型；实际响应: {body}");

        var groupA = FindRow(rows, "p-a/m-a", body);
        Assert.AreEqual("p-a", groupA.GetProperty("providerId").GetString());
        Assert.AreEqual("m-a", groupA.GetProperty("modelId").GetString());
        Assert.AreEqual(2L, groupA.GetProperty("requestCount").GetInt64());
        Assert.AreEqual(300L, groupA.GetProperty("promptTokens").GetInt64());
        Assert.AreEqual(30L, groupA.GetProperty("completionTokens").GetInt64());
        Assert.AreEqual(330L, groupA.GetProperty("totalTokens").GetInt64());
        Assert.AreEqual(10L, groupA.GetProperty("cacheHitTokens").GetInt64());
        Assert.AreEqual(290L, groupA.GetProperty("cacheMissTokens").GetInt64());
        Assert.AreEqual(1.75m, groupA.GetProperty("totalCost").GetDecimal());
        Assert.AreEqual(1.0m, groupA.GetProperty("inputCost").GetDecimal());
        Assert.AreEqual(0.5m, groupA.GetProperty("outputCost").GetDecimal());
        Assert.AreEqual(0.25m, groupA.GetProperty("cacheHitCost").GetDecimal());

        // 加权命中率 = 10/1000；算术平均(0.9,0.1)=0.5、hit/(hit+miss)=0.0333，均会在此失败。
        var rateA = groupA.GetProperty("cacheHitRate");
        Assert.AreEqual(JsonValueKind.Number, rateA.ValueKind, "可算 token > 0 时命中率必须是数字");
        Assert.AreEqual(0.01, rateA.GetDouble(), 1e-9,
            "cacheHitRate 必须是 sum(hit)/sum(eligible)，不是逐行命中率的算术平均");

        // 部分请求成本为 0（但有 token）⇒ 组成本仍可信，但要报出缺失条数。
        Assert.IsTrue(groupA.GetProperty("costKnown").GetBoolean());
        Assert.AreEqual(1L, groupA.GetProperty("unknownCostRequestCount").GetInt64());

        var groupB = FindRow(rows, "p-b/m-b", body);
        Assert.AreEqual(JsonValueKind.Null, groupB.GetProperty("cacheHitRate").ValueKind,
            "sum(eligible)=0 时必须返回 null（算不出），不能返回 0（0% 命中）");
        Assert.AreEqual(1L, groupB.GetProperty("requestCount").GetInt64());
        Assert.IsTrue(groupB.GetProperty("costKnown").GetBoolean());
    }

    // ───────────────────────────── 用例 2：时间过滤边界 ─────────────────────────────

    /// <summary>
    /// <c>fromUtc</c>/<c>toUtc</c> 过滤生效，两套边界语义各自钉死：
    /// <list type="bullet">
    /// <item>纯日期形式是<b>含当天</b>：<c>toUtc=2025-02-01</c> 覆盖 2025-02-01 全天。</item>
    /// <item>日期时间形式是半开区间：<c>[from, to)</c>，下界含、上界不含。</item>
    /// </list>
    /// </summary>
    [TestMethod]
    public async Task TimeRange_ExcludesRowsOutsideHalfOpenBounds()
    {
        await SeedAsync(
            NewRow("p-x", "m-x", createdAt: At(2025, 1, 31, 23, 59, 59), prompt: 1, completion: 0),
            NewRow("p-x", "m-x", createdAt: At(2025, 2, 1, 0, 0, 0), prompt: 10, completion: 0),
            NewRow("p-x", "m-x", createdAt: At(2025, 2, 1, 12, 0, 0), prompt: 100, completion: 0),
            NewRow("p-x", "m-x", createdAt: At(2025, 2, 1, 23, 59, 59), prompt: 1000, completion: 0),
            NewRow("p-x", "m-x", createdAt: At(2025, 2, 2, 0, 0, 0), prompt: 10000, completion: 0));

        // 纯日期区间：fromUtc=toUtc=2025-02-01 ⇒ 含 2/1 全天（00:00:00、12:00:00、23:59:59），
        // 不含前一天 23:59:59，也不含次日 00:00:00。
        var wholeDay = await GetSummaryAsync("fromUtc=2025-02-01&toUtc=2025-02-01&groupBy=model");
        using (var doc = JsonDocument.Parse(wholeDay))
        {
            var row = FindRow(doc.RootElement.GetProperty("rows"), "p-x/m-x", wholeDay);
            Assert.AreEqual(3L, row.GetProperty("requestCount").GetInt64(),
                "纯日期 toUtc 必须含当天全天：00:00:00、12:00:00、23:59:59 三条");
            Assert.AreEqual(1110L, row.GetProperty("totalTokens").GetInt64());
        }

        // 日期时间区间：[12:00:00, 次日 00:00) ⇒ 12:00:00 与 23:59:59 两条（下界含、上界不含）。
        var halfDay = await GetSummaryAsync("fromUtc=2025-02-01T12:00:00&toUtc=2025-02-02T00:00:00&groupBy=model");
        using (var doc = JsonDocument.Parse(halfDay))
        {
            var row = FindRow(doc.RootElement.GetProperty("rows"), "p-x/m-x", halfDay);
            Assert.AreEqual(2L, row.GetProperty("requestCount").GetInt64(),
                "下界含（12:00:00 计入）、上界不含（次日 00:00:00 排除）");
            Assert.AreEqual(1100L, row.GetProperty("totalTokens").GetInt64());
        }
    }

    // ───────────────────────────── 用例 3：day 分组按 UTC 切分 ─────────────────────────────

    /// <summary>
    /// <c>groupBy=day</c> 按 <b>UTC</b> 日期切分，且带非零偏移的写入也归一化到 UTC 日期。
    /// </summary>
    [TestMethod]
    public async Task GroupByDay_SplitsByUtcDateAndOrdersAscending()
    {
        await SeedAsync(
            NewRow("p-y", "m-y", createdAt: At(2025, 3, 1, 23, 59, 59), prompt: 1, completion: 0),
            // +08:00 的 3/2 07:00 在 UTC 是 3/1 23:00 ⇒ 必须落到 2025-03-01，而不是 03-02。
            NewRow("p-y", "m-y", createdAt: new DateTimeOffset(2025, 3, 2, 7, 0, 0, TimeSpan.FromHours(8)),
                prompt: 2, completion: 0),
            NewRow("p-y", "m-y", createdAt: At(2025, 3, 2, 0, 0, 0), prompt: 4, completion: 0),
            NewRow("p-y", "m-y", createdAt: At(2025, 3, 2, 5, 0, 0), prompt: 8, completion: 0));

        var body = await GetSummaryAsync("fromUtc=2025-03-01&toUtc=2025-03-03&groupBy=day");
        using var doc = JsonDocument.Parse(body);

        Assert.AreEqual("day", doc.RootElement.GetProperty("groupBy").GetString());
        var rows = doc.RootElement.GetProperty("rows");
        Assert.AreEqual(2, rows.GetArrayLength(), $"窗口内应只有 2 个 UTC 日期；实际响应: {body}");

        var first = rows[0];
        var second = rows[1];
        Assert.AreEqual("2025-03-01", first.GetProperty("key").GetString(), "日分组必须按日期升序");
        Assert.AreEqual("2025-03-02", second.GetProperty("key").GetString());
        Assert.AreEqual(2L, first.GetProperty("requestCount").GetInt64(),
            "+08:00 的 3/2 07:00 属于 UTC 的 3/1，必须计入 2025-03-01");
        Assert.AreEqual(2L, second.GetProperty("requestCount").GetInt64());
        Assert.AreEqual(JsonValueKind.Null, first.GetProperty("providerId").ValueKind,
            "day 分组没有单一 provider，必须为 null");
        Assert.AreEqual(JsonValueKind.Null, first.GetProperty("modelId").ValueKind);
    }

    // ───────────────────────────── 用例 4：空结果 ─────────────────────────────

    /// <summary>没有任何数据时必须 200 + 空数组，而不是 404。</summary>
    [TestMethod]
    public async Task EmptyWindow_Returns200WithEmptyRows()
    {
        var body = await GetSummaryAsync("fromUtc=2024-01-01&toUtc=2024-01-02&groupBy=model");
        using var doc = JsonDocument.Parse(body);

        var rows = doc.RootElement.GetProperty("rows");
        Assert.AreEqual(JsonValueKind.Array, rows.ValueKind);
        Assert.AreEqual(0, rows.GetArrayLength(), $"空区间必须返回空数组；实际响应: {body}");
        Assert.AreEqual(0L, doc.RootElement.GetProperty("total").GetInt64());
        Assert.IsFalse(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    // ───────────────────────────── 用例 6：成本未知 vs 零 ─────────────────────────────

    /// <summary>
    /// <c>TotalCost</c> 全 0 且 tokens &gt; 0 ⇒ <c>costKnown=false</c>（登记缺失，不是免费）；
    /// 只有部分为 0 ⇒ <c>costKnown=true</c> 且 <c>unknownCostRequestCount</c> 报出缺失条数。
    /// </summary>
    [TestMethod]
    public async Task CostAllZeroWithTokens_MarksCostUnknown()
    {
        await SeedAsync(
            // 价格登记缺失的模型：两行成本都是 0，但都有 token。
            NewRow("p-c", "m-c", createdAt: At(2025, 4, 1, 1), prompt: 100, completion: 50, total: 0m),
            NewRow("p-c", "m-c", createdAt: At(2025, 4, 1, 2), prompt: 100, completion: 50, total: 0m),
            // 成本部分缺失：一行有成本、一行 0。
            NewRow("p-d", "m-d", createdAt: At(2025, 4, 1, 3), prompt: 100, completion: 50, total: 5m),
            NewRow("p-d", "m-d", createdAt: At(2025, 4, 1, 4), prompt: 100, completion: 50, total: 0m));

        var body = await GetSummaryAsync("fromUtc=2025-04-01&toUtc=2025-04-02&groupBy=model");
        using var doc = JsonDocument.Parse(body);
        var rows = doc.RootElement.GetProperty("rows");

        var unknown = FindRow(rows, "p-c/m-c", body);
        Assert.AreEqual(0m, unknown.GetProperty("totalCost").GetDecimal());
        Assert.IsFalse(unknown.GetProperty("costKnown").GetBoolean(),
            "TotalCost 全 0 但 tokens>0 时必须 costKnown=false —— 那是登记缺失，不是免费");
        Assert.AreEqual(2L, unknown.GetProperty("unknownCostRequestCount").GetInt64());

        var partial = FindRow(rows, "p-d/m-d", body);
        Assert.AreEqual(5m, partial.GetProperty("totalCost").GetDecimal());
        Assert.IsTrue(partial.GetProperty("costKnown").GetBoolean(), "有非零成本时该组成本可信");
        Assert.AreEqual(1L, partial.GetProperty("unknownCostRequestCount").GetInt64(),
            "部分缺失只报缺失条数，不把整组标成未知");
    }

    // ───────────────────────────── 用例 7：subagent 截断 ─────────────────────────────

    /// <summary>
    /// <c>groupBy=subagent</c> 超过上限时 <c>truncated=true</c>、返回 200 行，
    /// 而 <c>total</c> 仍是真实分组总数（201），不会因为截断而少报。
    /// </summary>
    [TestMethod]
    public async Task GroupBySubagent_TruncatesAt200ButReportsRealTotal()
    {
        var rows = new List<TokenUsageEventEntity>(201);
        for (var i = 0; i < 201; i++)
        {
            rows.Add(NewRow(
                "p-s",
                "m-s",
                subAgentId: $"sa-{i:D3}",
                createdAt: At(2025, 5, 1, 0, 0, 0).AddSeconds(i),
                prompt: i + 1,
                completion: 0));
        }

        await SeedAsync(rows);

        var body = await GetSummaryAsync("fromUtc=2025-05-01&toUtc=2025-05-02&groupBy=subagent");
        using var doc = JsonDocument.Parse(body);

        Assert.AreEqual("subagent", doc.RootElement.GetProperty("groupBy").GetString());
        Assert.AreEqual(201L, doc.RootElement.GetProperty("total").GetInt64(),
            "total 必须是真实分组总数，不受 limit 影响");
        Assert.AreEqual(200, doc.RootElement.GetProperty("returned").GetInt32());
        Assert.AreEqual(200, doc.RootElement.GetProperty("limit").GetInt32());
        Assert.IsTrue(doc.RootElement.GetProperty("truncated").GetBoolean());

        var items = doc.RootElement.GetProperty("rows");
        Assert.AreEqual(200, items.GetArrayLength(), "被截断时只返回前 200 行");

        Assert.AreEqual(JsonValueKind.Null, items[0].GetProperty("providerId").ValueKind,
            "subagent 分组没有单一 provider，必须为 null");
        Assert.IsTrue(items[0].GetProperty("key").GetString()!.StartsWith("sa-", StringComparison.Ordinal),
            "subagent 分组的 key 就是 SubAgentId");
    }

    // ───────────────────────────── 契约与鉴权 ─────────────────────────────

    /// <summary>未带 JWT ⇒ 401。用量含成本与子代理归因，不能匿名读。</summary>
    [TestMethod]
    public async Task Get_WithoutToken_Returns401()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync($"{SummaryPath}?groupBy=model");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, "未带 Admin JWT 必须 401");
    }

    /// <summary>未知 groupBy ⇒ 400 且给出可读错误码（不静默回退到 model）。</summary>
    [TestMethod]
    public async Task Get_InvalidGroupBy_Returns400()
    {
        var response = await _client.GetAsync($"{SummaryPath}?groupBy=session");
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, $"响应体: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual("usage.invalid_group_by", doc.RootElement.GetProperty("code").GetString());
    }

    // ───────────────────────────── 夹具 ─────────────────────────────

    private async Task<string> GetSummaryAsync(string queryString)
    {
        var response = await _client.GetAsync($"{SummaryPath}?{queryString}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"响应体: {body}");
        return body;
    }

    private static JsonElement FindRow(JsonElement rows, string key, string body)
    {
        foreach (var row in rows.EnumerateArray())
        {
            if (row.GetProperty("key").GetString() == key)
                return row;
        }

        Assert.Fail($"响应里找不到分组 '{key}'；实际响应: {body}");
        return default;
    }

    private static DateTimeOffset At(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        => new(year, month, day, hour, minute, second, TimeSpan.Zero);

    private static TokenUsageEventEntity NewRow(
        string providerId,
        string modelId,
        DateTimeOffset createdAt,
        string? subAgentId = null,
        long prompt = 0,
        long completion = 0,
        long hit = 0,
        long miss = 0,
        long eligible = 0,
        decimal input = 0m,
        decimal output = 0m,
        decimal hitCost = 0m,
        decimal total = 0m,
        double? hitRate = null)
        => new()
        {
            SourceType = "test",
            SourceId = $"p0c-{Guid.NewGuid():N}",
            ProviderId = providerId,
            ModelId = modelId,
            SubAgentId = subAgentId,
            OccurredAtUtc = createdAt,
            CreatedAtUtc = createdAt,
            YearMonth = createdAt.ToString("yyyy-MM"),
            PromptTokens = prompt,
            CompletionTokens = completion,
            TotalTokens = prompt + completion,
            CacheHitTokens = hit,
            CacheMissTokens = miss,
            CacheEligibleTokens = eligible,
            CacheHitRate = hitRate,
            InputCost = input,
            OutputCost = output,
            CacheHitCost = hitCost,
            TotalCost = total,
        };

    private static async Task SeedAsync(params TokenUsageEventEntity[] rows)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.TokenUsageEvents.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static async Task SeedAsync(List<TokenUsageEventEntity> rows)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.TokenUsageEvents.AddRange(rows);
        await db.SaveChangesAsync();
    }
}
