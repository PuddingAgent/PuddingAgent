using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PuddingFinance.Biying;

namespace PuddingFinanceTests;

/// <summary>
/// 工具面回归：view → 端点/参数/投影 的映射。
/// 这层最容易出的不是崩溃，而是**映射错**（把 K 线接到指数端点、把复权价当不复权），
/// 所以断言要看真实请求的 URL 和回执里的键集合，而不是"没抛异常就算过"。
/// </summary>
[TestClass]
public sealed class BiyingToolTests
{
    private const string Licence = "08AF0000-0000-0000-0000-000000000000";

    [TestMethod]
    public async Task Quote_HitsRealtimeEndpoint_AndProjectsToValuationFields()
    {
        var handler = new RecordingHandler(_ => Json("""[{"t":"2026-10-23 15:35:12","p":11.2,"pc":-0.88,"pe":4.23,"sjl":0.47,"sz":217346283818,"lt":217343671899,"junk_field":"x"}]"""));
        var tool = BuildMarket(handler);

        var result = await tool.QueryAsync(new BiyingMarketRequest { View = BiyingMarketView.Quote, Code = "000001" });

        StringAssert.Contains(handler.LastUrl, "hsrl/ssjy/000001");
        var row = FirstRowObject(result.Json);
        Assert.IsTrue(row.ContainsKey("pe"), "估值字段必须保留");
        Assert.IsTrue(row.ContainsKey("sz"), "总市值必须保留");
        Assert.IsFalse(row.ContainsKey("junk_field"), "投影之外的字面量字段必须被丢弃");
        Assert.IsLessThanOrEqualTo(16, row.Count);
    }

    [TestMethod]
    public async Task Index_UsesIndexEndpoint_NotStockEndpoint()
    {
        var handler = new RecordingHandler(_ => Json("""[{"t":"2026-10-23","o":3300,"c":3312,"pc":0.4}]"""));
        var tool = BuildMarket(handler);

        await tool.QueryAsync(new BiyingMarketRequest { View = BiyingMarketView.Index, Code = "000001.SH" });

        StringAssert.Contains(handler.LastUrl, "hsindex/latest/000001.SH/d/");
        Assert.DoesNotContain("hsrl/ssjy", handler.LastUrl, "指数不能用个股实时端点");
    }

    [TestMethod]
    public async Task Kline_DefaultsToUnadjustedPrices()
    {
        // 复权价会让"历史高点"看起来比实际低，估值判断直接错。默认必须是 n。
        var handler = new RecordingHandler(_ => Json("""[{"t":"2026-10-23","o":11.2,"c":11.3,"v":100}]"""));
        var tool = BuildMarket(handler);

        await tool.QueryAsync(new BiyingMarketRequest
        {
            View = BiyingMarketView.Kline,
            Code = "000001.SZ",
            Limit = 5,
        });

        StringAssert.Contains(handler.LastUrl, "/hsstock/pro/history/000001.SZ/1/n/");
        StringAssert.Contains(handler.LastUrl, "lt=5");
    }

    [TestMethod]
    public async Task Kline_RejectsCodeWithoutMarketSuffix()
    {
        var tool = BuildMarket(new RecordingHandler(_ => Json("[]")));

        var error = await Assert.ThrowsExactlyAsync<BiyingEndpointException>(
            () => tool.QueryAsync(new BiyingMarketRequest { View = BiyingMarketView.Kline, Code = "000001" }));

        StringAssert.Contains(error.Message, "市场后缀");
    }

    [TestMethod]
    public async Task Quote_WithoutCode_FailsBeforeAnyRequest()
    {
        var handler = new RecordingHandler(_ => Json("[]"));
        var tool = BuildMarket(handler);

        await Assert.ThrowsExactlyAsync<BiyingEndpointException>(
            () => tool.QueryAsync(new BiyingMarketRequest { View = BiyingMarketView.Quote }));

        Assert.AreEqual(0, handler.CallCount, "参数不合法时不得发出请求（那会白扣配额）");
    }

    [TestMethod]
    public async Task Balance_KeepsFullStatement_MinusProjection()
    {
        // 三表不做字段投影：砍字段会看不到"其他应收款异常膨胀"这类信号
        var raw = """[{"jzrq":"2026-06-30","yszk":"100","ch":"200","sy":"300","rarely_used_field":"400"}]""";
        var handler = new RecordingHandler(_ => Json(raw));
        var tool = BuildFinancial(handler);

        var result = await tool.QueryAsync(new BiyingFinancialRequest
        {
            View = BiyingFinancialView.Balance,
            Code = "000001",
        });

        StringAssert.Contains(handler.LastUrl, "hsstock/financial/balance/000001");
        var row = FirstRowObject(result.Json);
        Assert.IsTrue(row.ContainsKey("rarely_used_field"), "三表必须保留全部字段");
    }

    [TestMethod]
    public async Task Indicators_ProjectToTheTwentySomethingCoreMetrics()
    {
        var raw = """[{"date":"2026-06-30","mgsy":"1.24","jzsy":"9.8","xsml":"20.1","noise_field":"x"}]""";
        var handler = new RecordingHandler(_ => Json(raw));
        var tool = BuildFinancial(handler);

        var result = await tool.QueryAsync(new BiyingFinancialRequest
        {
            View = BiyingFinancialView.Indicators,
            Code = "000001",
        });

        var row = FirstRowObject(result.Json);
        Assert.IsTrue(row.ContainsKey("jzsy"), "ROE 必须保留");
        Assert.IsFalse(row.ContainsKey("noise_field"), "指标必须投影");
    }

    [TestMethod]
    public async Task Years_ControlsHowManyPeriodsAreReturned()
    {
        var raw = BuildSeries(rows: 20);
        var handler = new RecordingHandler(_ => Json(raw));
        var tool = BuildFinancial(handler);

        var result = await tool.QueryAsync(new BiyingFinancialRequest
        {
            View = BiyingFinancialView.Balance,
            Code = "000001",
            Years = 3,
        });

        var payload = JsonNode.Parse(result.Json)!.AsObject();
        Assert.AreEqual(3, payload["returned"]!.GetValue<int>());
        Assert.IsTrue(payload["truncated"]!.GetValue<bool>());
    }

    [TestMethod]
    public void UnknownView_IsRejectedWithValidOptions()
    {
        var error = Assert.ThrowsExactly<BiyingEndpointException>(
            () => BiyingFinancialTool.Resolve((BiyingFinancialView)99));

        StringAssert.Contains(error.Message, "Balance");
    }

    private static JsonObject FirstRowObject(string json)
        => JsonNode.Parse(json)!["rows"]!.AsArray()[0]!.AsObject();

    private static string BuildSeries(int rows)
    {
        var arr = new JsonArray();
        for (var i = 0; i < rows; i++)
            arr.Add(new JsonObject { ["jzrq"] = $"20{20 + i}-12-31", ["v{i}"] = i });
        return arr.ToJsonString();
    }

    private static BiyingMarketTool BuildMarket(RecordingHandler handler)
        => new(BuildClient(handler));

    private static BiyingFinancialTool BuildFinancial(RecordingHandler handler)
        => new(BuildClient(handler));

    private static BiyingClient BuildClient(RecordingHandler handler)
    {
        var http = new HttpClient(handler);
        return new BiyingClient(http, BiyingEndpointCatalog.LoadEmbedded(),
            new BiyingOptions { Licence = Licence });
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public string LastUrl { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastUrl = request.RequestUri!.ToString();
            return Task.FromResult(respond(request));
        }
    }
}
