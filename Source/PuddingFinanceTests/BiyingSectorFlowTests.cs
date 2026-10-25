using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PuddingFinance.Biying;

namespace PuddingFinanceTests;

/// <summary>
/// S4 回归：板块/概念与资金流。
///
/// 这层的核心不是"能不能查到"，而是**拒绝的顺序**：
/// 全量股票列表 250,566 字符，必须在发请求之前就拒答——先查再截断等于白烧一次配额。
/// </summary>
[TestClass]
public sealed class BiyingSectorFlowTests
{
    private const string Licence = "08AF0000-0000-0000-0000-000000000000";

    [TestMethod]
    public async Task StockList_WithoutKeyword_RefusesBeforeAnyRequest()
    {
        var handler = new StubHandler(_ => Json("[]"));
        var tool = new BiyingSectorTool(BuildClient(handler));

        var error = await Assert.ThrowsExactlyAsync<BiyingEndpointException>(
            () => tool.QueryAsync(new BiyingSectorRequest { View = BiyingSectorView.StockList }));

        StringAssert.Contains(error.Message, "keyword");
        StringAssert.Contains(error.Message, "250,566");
        Assert.AreEqual(0, handler.CallCount, "拒答必须发生在发请求之前，否则白烧配额");
    }

    [TestMethod]
    public async Task StockList_WithKeyword_FiltersToMatchingRows()
    {
        var body = """
        [
          {"dm":"000001","mc":"平安银行","jys":"sz"},
          {"dm":"000002","mc":"万科A","jys":"sz"},
          {"dm":"600000","mc":"浦发银行","jys":"sh"}
        ]
        """;
        var handler = new StubHandler(_ => Json(body));
        var tool = new BiyingSectorTool(BuildClient(handler));

        var result = await tool.QueryAsync(new BiyingSectorRequest
        {
            View = BiyingSectorView.StockList,
            Keyword = "银行",
        });

        var rows = JsonNode.Parse(result.Json)!["rows"]!.AsArray();
        Assert.AreEqual(2, rows.Count, "只应保留名称含「银行」的两行");
        Assert.DoesNotContain("万科", result.Json, "不含关键词的行必须被过滤掉");
    }

    [TestMethod]
    public async Task StockList_KeywordWithoutMatch_SaysSoInsteadOfEmptyTable()
    {
        var handler = new StubHandler(_ => Json("""[{"dm":"000001","mc":"平安银行"}]"""));
        var tool = new BiyingSectorTool(BuildClient(handler));

        var result = await tool.QueryAsync(new BiyingSectorRequest
        {
            View = BiyingSectorView.StockList,
            Keyword = "不存在的公司",
        });

        StringAssert.Contains(result.Json, "没有匹配");
        Assert.DoesNotContain("\"rows\"", result.Json, "无匹配时不应给出空 rows，要给出原因");
    }

    [TestMethod]
    public async Task Constituents_RequiresSectorCode()
    {
        var tool = new BiyingSectorTool(BuildClient(new StubHandler(_ => Json("[]"))));

        await Assert.ThrowsExactlyAsync<BiyingEndpointException>(
            () => tool.QueryAsync(new BiyingSectorRequest { View = BiyingSectorView.Constituents }));
    }

    [TestMethod]
    public async Task Constituents_CallsSectorEndpointWithCode()
    {
        var handler = new StubHandler(_ => Json("""[{"dm":"000001","mc":"平安银行"}]"""));
        var tool = new BiyingSectorTool(BuildClient(handler));

        await tool.QueryAsync(new BiyingSectorRequest
        {
            View = BiyingSectorView.Constituents,
            Code = "BK0475",
        });

        StringAssert.Contains(handler.LastUrl, "hszg/gg/BK0475/");
    }

    [TestMethod]
    public async Task LhbStock_PassesDayCountAsPathArgument()
    {
        var handler = new StubHandler(_ => Json("""[{"dm":"000001"}]"""));
        var tool = new BiyingFlowTool(BuildClient(handler));

        await tool.QueryAsync(new BiyingFlowRequest { View = BiyingFlowView.LhbStock, Days = 7 });

        StringAssert.Contains(handler.LastUrl, "hilh/ggsb/7/");
    }

    [TestMethod]
    public async Task StockFlowHistory_RequiresBothDateAndCode()
    {
        var tool = new BiyingFlowTool(BuildClient(new StubHandler(_ => Json("[]"))));

        await Assert.ThrowsExactlyAsync<BiyingEndpointException>(
            () => tool.QueryAsync(new BiyingFlowRequest
            {
                View = BiyingFlowView.StockFlowHistory,
                Code = "000001",
            }));

        await Assert.ThrowsExactlyAsync<BiyingEndpointException>(
            () => tool.QueryAsync(new BiyingFlowRequest
            {
                View = BiyingFlowView.StockFlowHistory,
                Date = "20261023",
            }));
    }

    [TestMethod]
    public async Task StockFlowHistory_PlacesDateBeforeCode_InTemplateOrder()
    {
        // 303 模板是 /transaction/{yyyyMMdd}/{stock_code}/{licence}，顺序错了会 404
        var handler = new StubHandler(_ => Json("""[{"t":"20261023"}]"""));
        var tool = new BiyingFlowTool(BuildClient(handler));

        await tool.QueryAsync(new BiyingFlowRequest
        {
            View = BiyingFlowView.StockFlowHistory,
            Date = "20261023",
            Code = "000001",
        });

        StringAssert.Contains(handler.LastUrl, "hsstock/history/transaction/20261023/000001/");
    }

    [TestMethod]
    public async Task StockFlow_UsesTransactionEndpoint()
    {
        var handler = new StubHandler(_ => Json("""[{"t":"20261023"}]"""));
        var tool = new BiyingFlowTool(BuildClient(handler));

        await tool.QueryAsync(new BiyingFlowRequest { View = BiyingFlowView.StockFlow, Code = "000001" });

        StringAssert.Contains(handler.LastUrl, "hsstock/history/transaction/000001/");
    }

    [TestMethod]
    public void Shape_KeywordFilterIsCaseInsensitive()
    {
        var catalog = BiyingEndpointCatalog.LoadEmbedded();
        var raw = """[{"dm":"000001","mc":"PA Bank"},{"dm":"000002","mc":"other"}]""";

        var shaped = BiyingShaper.Shape(catalog.Get("1"), raw,
            new BiyingShapeOptions { Keyword = "pa bank" });

        var payload = JsonNode.Parse(shaped.Json)!.AsObject();
        Assert.AreEqual(1, payload["returned"]!.GetValue<int>());
    }

    private static BiyingClient BuildClient(StubHandler handler)
        => new(new HttpClient(handler), BiyingEndpointCatalog.LoadEmbedded(),
            new BiyingOptions { Licence = Licence });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
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
