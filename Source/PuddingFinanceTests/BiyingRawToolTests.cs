using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PuddingFinance.Biying;

namespace PuddingFinanceTests;

/// <summary>
/// 任意端点直通的回归。三条安全边界各有对应断言：
/// 端点白名单、查询键白名单、以及「被拒时不得发出请求」（配额是日累计的，白烧一次就是一次）。
/// </summary>
[TestClass]
public sealed class BiyingRawToolTests
{
    private const string Licence = "012345678901234567890123456789012345";

    [TestMethod]
    public async Task UnknownEndpointId_IsRejectedWithoutSendingRequest()
    {
        var handler = new StubHandler(_ => Json("{}"));
        var tool = new BiyingRawTool(BuildClient(handler));

        var error = await ThrowsAsync<BiyingEndpointException>(
            () => tool.QueryAsync(new BiyingRawRequest { EndpointId = "99999" }));

        Assert.AreEqual(0, handler.CallCount, "端点不在白名单时不得发出任何请求（SSRF 边界）");
        StringAssert.Contains(error.Message, "99999");
    }

    [TestMethod]
    public async Task QueryKeyOutsideEndpointWhitelist_IsRejectedWithoutSendingRequest()
    {
        // 端点 54（实时交易）不声明任何查询参数；给了就必须当场拒绝。
        var handler = new StubHandler(_ => Json("[]"));
        var tool = new BiyingRawTool(BuildClient(handler));

        var error = await ThrowsAsync<BiyingEndpointException>(
            () => tool.QueryAsync(new BiyingRawRequest
            {
                EndpointId = "54",
                PathArgs = ["000001"],
                Query = new Dictionary<string, string> { ["bogus"] = "1" },
            }));

        Assert.AreEqual(0, handler.CallCount, "参数被拒时不得发出请求（配额不该被白烧）");
        StringAssert.Contains(error.Message, "bogus");
    }

    [TestMethod]
    public async Task ValidEndpoint_RequestsExpectedUrlAndShapesResponse()
    {
        var handler = new StubHandler(_ => Json("""[{"t":"2026-10-23 15:35:12","p":11.2,"pe":4.23}]"""));
        var tool = new BiyingRawTool(BuildClient(handler));

        var result = await tool.QueryAsync(new BiyingRawRequest
        {
            EndpointId = "54",
            PathArgs = ["000001"],
        });

        Assert.AreEqual(1, handler.CallCount);
        StringAssert.Contains(handler.LastUrl, "hsrl/ssjy/000001/");

        var payload = JsonNode.Parse(result.Json)!.AsObject();
        Assert.AreEqual(1, payload["returned"]!.GetValue<int>());
        Assert.IsTrue(payload["legend"]!.AsObject().ContainsKey("pe"), "整形层的词典必须仍然生效");
    }

    [TestMethod]
    public async Task DefaultLimit_IsConservativeForUnknownShapes()
    {
        // raw 的响应形状不可预知，默认只给 3 行——比前四个工具更保守。
        var rows = string.Join(",", Enumerable.Range(0, 5).Select(i => $"{{\"i\":{i}}}"));
        var handler = new StubHandler(_ => Json($"[{rows}]"));
        var tool = new BiyingRawTool(BuildClient(handler));

        var result = await tool.QueryAsync(new BiyingRawRequest { EndpointId = "54", PathArgs = ["000001"] });
        var payload = JsonNode.Parse(result.Json)!.AsObject();

        Assert.AreEqual(3, payload["returned"]!.GetValue<int>());
        Assert.IsTrue(payload["truncated"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task FieldProjection_NarrowsUnknownShapes()
    {
        var handler = new StubHandler(_ => Json("""[{"a":1,"b":2,"c":3}]"""));
        var tool = new BiyingRawTool(BuildClient(handler));

        var result = await tool.QueryAsync(new BiyingRawRequest
        {
            EndpointId = "54",
            PathArgs = ["000001"],
            Fields = ["a"],
        });

        var row = JsonNode.Parse(result.Json)!["rows"]!.AsArray()[0]!.AsObject();
        Assert.AreEqual(1, row.Count, "投影后只应保留请求的键");
        Assert.IsTrue(row.ContainsKey("a"));
    }

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException exception)
        {
            return exception;
        }

        Assert.Fail($"预期抛出 {typeof(TException).Name}，实际没有抛出。");
        throw new InvalidOperationException("unreachable");
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