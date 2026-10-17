using System.Text.Json;
using PuddingDeepSeekWeb;

namespace PuddingDeepSeekWebTests;

/// <summary>
/// S2/S3 独立测试：组件协议语义完全由假传输驱动，不需要网络、宿主或配置文件。
/// <para>
/// 覆盖：请求形状（URL / 方法 / 鉴权头 / 线上 JSON / 省略规则 / Base URL 归一）、
/// 校验矩阵（失败不产生出站请求）、成功解析（含未知字段容忍）、HTTP 错误映射、
/// 有界重试（指数退避 + Retry-After）、传输异常、取消传播。
/// </para>
/// </summary>
[TestClass]
public sealed class DeepSeekWebServiceTests
{
    private const string ApiKey = "sk-test-secret-123";
    private const string BaseUrl = "https://api.deepseek.com";
    private const string SearchUrl = "https://api.deepseek.com/v1/tools/search";
    private const string FetchUrl = "https://api.deepseek.com/v1/tools/fetch";
    private const string EmptySearchJson = """{"results":[]}""";

    // =====================================================================
    // Search 请求形状
    // =====================================================================

    [TestMethod]
    public async Task SearchAsync_Builds_Official_Request_Shape()
    {
        var transport = new FakeTransport().Respond(200, EmptySearchJson);
        var service = CreateService(transport);
        var connection = Connection(timeoutSeconds: 45);
        var request = new DeepSeekSearchRequest
        {
            Query = "DeepSeek 官方文档",
            Count = 3,
            OriQuestion = "DeepSeek 官方文档在哪里",
            Localization = "203.0.113.7",
            NeedContent = false,
        };

        var result = await service.SearchAsync(connection, request);

        Assert.IsTrue(result.IsSuccess, FailureMessage(result));
        Assert.AreEqual(1, transport.CallCount, "exactly one transport call expected");

        var sent = transport.Requests[0];
        Assert.AreEqual(SearchUrl, sent.Url);
        Assert.AreEqual("POST", sent.Method);
        Assert.AreEqual("Bearer " + ApiKey, sent.Headers["Authorization"]);
        Assert.AreEqual("application/json", sent.ContentType);
        Assert.AreEqual(45, sent.TimeoutSeconds);

        var body = ParseJson(sent.Body);
        Assert.AreEqual("DeepSeek 官方文档", body.GetProperty("query").GetString());
        Assert.AreEqual(3, body.GetProperty("count").GetInt32());
        Assert.AreEqual("DeepSeek 官方文档在哪里", body.GetProperty("ori_question").GetString());
        Assert.AreEqual("203.0.113.7", body.GetProperty("localization").GetString());
        Assert.IsFalse(body.GetProperty("need_content").GetBoolean(), "need_content=false must be explicit");
    }

    [TestMethod]
    public async Task SearchAsync_Omits_Absent_Optional_Fields()
    {
        var transport = new FakeTransport().Respond(200, EmptySearchJson);
        var service = CreateService(transport);

        var result = await service.SearchAsync(Connection(), new DeepSeekSearchRequest { Query = "q" });

        Assert.IsTrue(result.IsSuccess, FailureMessage(result));
        var body = ParseJson(transport.Requests[0].Body);
        Assert.AreEqual("q", body.GetProperty("query").GetString());
        Assert.IsFalse(body.GetProperty("need_content").GetBoolean());
        Assert.IsFalse(body.TryGetProperty("count", out _), "count must be omitted when null");
        Assert.IsFalse(body.TryGetProperty("ori_question", out _), "ori_question must be omitted when absent");
        Assert.IsFalse(body.TryGetProperty("localization", out _), "localization must be omitted when absent");
        Assert.AreEqual(2, body.EnumerateObject().Count(), "wire body must carry only query + need_content");
    }

    [TestMethod]
    public async Task SearchAsync_Whitespace_Optional_Fields_Are_Omitted()
    {
        var transport = new FakeTransport().Respond(200, EmptySearchJson);
        var service = CreateService(transport);
        var request = new DeepSeekSearchRequest
        {
            Query = "q",
            OriQuestion = "   ",
            Localization = "\t ",
            Count = null,
            NeedContent = true,
        };

        var result = await service.SearchAsync(Connection(), request);

        Assert.IsTrue(result.IsSuccess, FailureMessage(result));
        var body = ParseJson(transport.Requests[0].Body);
        Assert.IsFalse(body.TryGetProperty("ori_question", out _));
        Assert.IsFalse(body.TryGetProperty("localization", out _));
        Assert.IsTrue(body.GetProperty("need_content").GetBoolean());
    }

    [TestMethod]
    public async Task SearchAsync_Trims_Query_And_Optional_Fields()
    {
        var transport = new FakeTransport().Respond(200, EmptySearchJson);
        var service = CreateService(transport);
        var request = new DeepSeekSearchRequest
        {
            Query = "  hello  ",
            OriQuestion = "  original question  ",
            Localization = " 127.0.0.1 ",
        };

        var result = await service.SearchAsync(Connection(), request);

        Assert.IsTrue(result.IsSuccess, FailureMessage(result));
        var body = ParseJson(transport.Requests[0].Body);
        Assert.AreEqual("hello", body.GetProperty("query").GetString());
        Assert.AreEqual("original question", body.GetProperty("ori_question").GetString());
        Assert.AreEqual("127.0.0.1", body.GetProperty("localization").GetString());
    }

    [TestMethod]
    public async Task SearchAsync_Custom_Base_Url_With_Trailing_Slash_Is_Normalized()
    {
        var transport = new FakeTransport()
            .Respond(200, EmptySearchJson)
            .Respond(200, EmptySearchJson);
        var service = CreateService(transport);

        var official = await service.SearchAsync(
            Connection(baseUrl: "https://api.deepseek.com/"),
            new DeepSeekSearchRequest { Query = "q" });
        var custom = await service.SearchAsync(
            Connection(baseUrl: "https://gateway.example.test/base/"),
            new DeepSeekSearchRequest { Query = "q" });

        Assert.IsTrue(official.IsSuccess, FailureMessage(official));
        Assert.IsTrue(custom.IsSuccess, FailureMessage(custom));
        Assert.AreEqual(SearchUrl, transport.Requests[0].Url);
        Assert.AreEqual("https://gateway.example.test/base/v1/tools/search", transport.Requests[1].Url);
    }

    // =====================================================================
    // Fetch 请求形状
    // =====================================================================

    [TestMethod]
    public async Task FetchAsync_Builds_Official_Request_Shape()
    {
        const string url = "https://api-docs.deepseek.com/zh-cn/guides/search";
        var transport = new FakeTransport().Respond(200, """{"title":"T","content":"# C"}""");
        var service = CreateService(transport);

        var result = await service.FetchAsync(Connection(), new DeepSeekFetchRequest { Url = url });

        Assert.IsTrue(result.IsSuccess, FailureMessage(result));
        Assert.AreEqual(1, transport.CallCount);

        var sent = transport.Requests[0];
        Assert.AreEqual(FetchUrl, sent.Url);
        Assert.AreEqual("POST", sent.Method);
        Assert.AreEqual("Bearer " + ApiKey, sent.Headers["Authorization"]);
        Assert.AreEqual("application/json", sent.ContentType);

        var body = ParseJson(sent.Body);
        Assert.AreEqual(url, body.GetProperty("url").GetString());
        Assert.AreEqual(1, body.EnumerateObject().Count(), "fetch wire body must carry only url");
    }

    // =====================================================================
    // 校验矩阵：失败必须是 InvalidRequest，且不得调用 transport
    // =====================================================================

    [TestMethod]
    public async Task SearchAsync_Invalid_Inputs_Are_Rejected_Without_Transport()
    {
        var emoji = "😀";
        var cases = new (string Name, DeepSeekWebConnection Connection, DeepSeekSearchRequest Request)[]
        {
            ("empty query", Connection(), SearchRequest(string.Empty)),
            ("whitespace query", Connection(), SearchRequest("   \t\n ")),
            ("129 CJK runes query", Connection(), SearchRequest(new string('深', 129))),
            ("129 emoji runes query", Connection(), SearchRequest(string.Concat(Enumerable.Repeat(emoji, 129)))),
            ("count 0", Connection(), SearchRequest() with { Count = 0 }),
            ("count 51", Connection(), SearchRequest() with { Count = 51 }),
            ("ori_question 129 runes", Connection(), SearchRequest() with { OriQuestion = new string('问', 129) }),
            ("invalid localization", Connection(), SearchRequest() with { Localization = "not-an-ip" }),
            ("out-of-range localization", Connection(), SearchRequest() with { Localization = "999.1.1.1" }),
            ("blank api key", Connection(apiKey: "   "), SearchRequest()),
            ("ftp base url", Connection(baseUrl: "ftp://x"), SearchRequest()),
            ("malformed base url", Connection(baseUrl: "not a url"), SearchRequest()),
            ("base url with userinfo", Connection(baseUrl: "https://user:pass@api.deepseek.com"), SearchRequest()),
        };

        foreach (var testCase in cases)
        {
            var transport = new FakeTransport();
            var service = CreateService(transport, maxRetryAttempts: 0);

            var result = await service.SearchAsync(testCase.Connection, testCase.Request);

            Assert.IsFalse(result.IsSuccess, testCase.Name + ": expected validation failure");
            Assert.IsNotNull(result.Error, testCase.Name);
            Assert.AreEqual(DeepSeekWebErrorKind.InvalidRequest, result.Error.Kind, testCase.Name);
            Assert.AreEqual(0, transport.CallCount, testCase.Name + ": validation must not reach transport");
            Assert.IsFalse(
                result.Error.Message.Contains(ApiKey, StringComparison.Ordinal),
                testCase.Name + ": error message must not contain the api key");
        }
    }

    [TestMethod]
    public async Task SearchAsync_Query_At_Rune_Limit_Is_Accepted()
    {
        var transport = new FakeTransport()
            .Respond(200, EmptySearchJson)
            .Respond(200, EmptySearchJson);
        var service = CreateService(transport);

        var cjk = await service.SearchAsync(Connection(), SearchRequest(new string('深', 128)));
        // 128 emoji = 256 UTF-16 chars but exactly 128 Unicode code points: rune counting must not reject it.
        var emoji = await service.SearchAsync(
            Connection(),
            SearchRequest(string.Concat(Enumerable.Repeat("😀", 128))));

        Assert.IsTrue(cjk.IsSuccess, FailureMessage(cjk));
        Assert.IsTrue(emoji.IsSuccess, FailureMessage(emoji));
        Assert.AreEqual(2, transport.CallCount);
    }

    [TestMethod]
    public async Task FetchAsync_Invalid_Urls_Are_Rejected_Without_Transport()
    {
        var oversizedUrl = "https://example.test/" + new string('a', 8193 - "https://example.test/".Length);
        var cases = new (string Name, DeepSeekFetchRequest Request)[]
        {
            ("empty url", new DeepSeekFetchRequest { Url = string.Empty }),
            ("whitespace url", new DeepSeekFetchRequest { Url = "   " }),
            ("relative url", new DeepSeekFetchRequest { Url = "/v1/tools/fetch" }),
            ("file scheme", new DeepSeekFetchRequest { Url = "file:///c:/x" }),
            ("ftp scheme", new DeepSeekFetchRequest { Url = "ftp://example.test/x" }),
            ("userinfo", new DeepSeekFetchRequest { Url = "https://user:pass@host/x" }),
            ("oversized url", new DeepSeekFetchRequest { Url = oversizedUrl }),
        };
        Assert.AreEqual(8193, oversizedUrl.Length, "control: the oversized url must actually exceed the limit");

        foreach (var testCase in cases)
        {
            var transport = new FakeTransport();
            var service = CreateService(transport, maxRetryAttempts: 0);

            var result = await service.FetchAsync(Connection(), testCase.Request);

            Assert.IsFalse(result.IsSuccess, testCase.Name + ": expected validation failure");
            Assert.IsNotNull(result.Error, testCase.Name);
            Assert.AreEqual(DeepSeekWebErrorKind.InvalidRequest, result.Error.Kind, testCase.Name);
            Assert.AreEqual(0, transport.CallCount, testCase.Name + ": validation must not reach transport");
        }
    }

    [TestMethod]
    public async Task FetchAsync_Max_Length_Url_Is_Accepted_By_Validation()
    {
        var url = "https://example.test/" + new string('a', 8192 - "https://example.test/".Length);
        Assert.AreEqual(8192, url.Length, "control: url must be exactly at the limit");
        var transport = new FakeTransport().Respond(200, """{"title":"T","content":"C"}""");
        var service = CreateService(transport);

        var result = await service.FetchAsync(Connection(), new DeepSeekFetchRequest { Url = url });

        Assert.IsTrue(result.IsSuccess, FailureMessage(result));
        Assert.AreEqual(1, transport.CallCount);
    }

    // =====================================================================
    // 成功解析
    // =====================================================================

    [TestMethod]
    public async Task SearchAsync_Parses_All_Result_Fields_And_Ignores_Unknown_Fields()
    {
        const string json = """
            {
              "results": [
                {
                  "rerank_score": 2.4,
                  "title": "标题一",
                  "url": "https://example.test/a",
                  "snippet": "摘要一",
                  "summary": "长摘要一",
                  "content": "# 正文一",
                  "site_name": "Example",
                  "site_icon": "https://example.test/icon.png",
                  "date": "2026-10-01T00:00:00Z",
                  "authority": 4,
                  "unknown_field": { "nested": [1, 2, 3] },
                  "another_unknown": null
                },
                {
                  "rerank_score": 0.5,
                  "title": "标题二",
                  "url": "https://example.test/b",
                  "snippet": "摘要二",
                  "summary": "长摘要二",
                  "content": "",
                  "site_name": null,
                  "site_icon": null,
                  "date": null,
                  "authority": -1
                }
              ],
              "unknown_top_level": "ignored"
            }
            """;
        var transport = new FakeTransport().Respond(200, json);
        var service = CreateService(transport);

        var result = await service.SearchAsync(Connection(), SearchRequest());

        Assert.IsTrue(result.IsSuccess, FailureMessage(result));
        var value = result.Value!;
        Assert.HasCount(2, value.Results);

        var first = value.Results[0];
        Assert.AreEqual(2.4, first.RerankScore, 1e-9);
        Assert.AreEqual("标题一", first.Title);
        Assert.AreEqual("https://example.test/a", first.Url);
        Assert.AreEqual("摘要一", first.Snippet);
        Assert.AreEqual("长摘要一", first.Summary);
        Assert.AreEqual("# 正文一", first.Content);
        Assert.AreEqual("Example", first.SiteName);
        Assert.AreEqual("https://example.test/icon.png", first.SiteIcon);
        Assert.AreEqual("2026-10-01T00:00:00Z", first.Date);
        Assert.AreEqual(4, first.Authority);

        var second = value.Results[1];
        Assert.IsNull(second.SiteName);
        Assert.IsNull(second.SiteIcon);
        Assert.IsNull(second.Date);
        Assert.AreEqual(-1, second.Authority);
        Assert.AreEqual(string.Empty, second.Content);
    }

    [TestMethod]
    public async Task SearchAsync_Missing_Authority_Defaults_To_Unknown()
    {
        var transport = new FakeTransport().Respond(200, """{"results":[{"title":"t","url":"u"}]}""");
        var service = CreateService(transport);

        var result = await service.SearchAsync(Connection(), SearchRequest());

        Assert.IsTrue(result.IsSuccess, FailureMessage(result));
        Assert.AreEqual(-1, result.Value!.Results[0].Authority, "-1 means unknown authority");
    }

    [TestMethod]
    public async Task SearchAsync_Empty_Results_Is_A_Success()
    {
        var transport = new FakeTransport().Respond(200, EmptySearchJson);
        var service = CreateService(transport);

        var result = await service.SearchAsync(Connection(), SearchRequest());

        Assert.IsTrue(result.IsSuccess, FailureMessage(result));
        Assert.IsNotNull(result.Value);
        Assert.IsEmpty(result.Value.Results);
    }

    [TestMethod]
    public async Task SearchAsync_Unusable_2xx_Bodies_Are_InvalidResponse()
    {
        var bodies = new[]
        {
            "{}", // results missing
            """{"results":null}""", // results null
            """[]""", // root is not an object
            "<html>oops</html>", // not JSON
            "not json at all",
            string.Empty,
        };

        foreach (var body in bodies)
        {
            var transport = new FakeTransport().Respond(200, body);
            var service = CreateService(transport, maxRetryAttempts: 0);

            var result = await service.SearchAsync(Connection(), SearchRequest());

            Assert.IsFalse(result.IsSuccess, "body should be rejected: " + body);
            Assert.IsNotNull(result.Error);
            Assert.AreEqual(DeepSeekWebErrorKind.InvalidResponse, result.Error.Kind, "body: " + body);
            Assert.AreEqual(1, transport.CallCount, "a bad 2xx body must not be retried");
        }
    }

    [TestMethod]
    public async Task FetchAsync_Parses_Title_And_Content()
    {
        var transport = new FakeTransport().Respond(
            200,
            """{"title":"页面标题","content":"# Markdown\n\n正文","unknown":1}""");
        var service = CreateService(transport);

        var result = await service.FetchAsync(
            Connection(),
            new DeepSeekFetchRequest { Url = "https://example.test/page" });

        Assert.IsTrue(result.IsSuccess, FailureMessage(result));
        Assert.AreEqual("页面标题", result.Value!.Title);
        Assert.AreEqual("# Markdown\n\n正文", result.Value.Content);
    }

    [TestMethod]
    public async Task FetchAsync_Missing_Or_Null_Fields_Become_Empty_Strings()
    {
        var bodies = new[] { "{}", """{"title":null,"content":null}""" };

        foreach (var body in bodies)
        {
            var transport = new FakeTransport().Respond(200, body);
            var service = CreateService(transport);

            var result = await service.FetchAsync(
                Connection(),
                new DeepSeekFetchRequest { Url = "https://example.test/page" });

            Assert.IsTrue(result.IsSuccess, "body: " + body + " / " + FailureMessage(result));
            Assert.AreEqual(string.Empty, result.Value!.Title, "body: " + body);
            Assert.AreEqual(string.Empty, result.Value.Content, "body: " + body);
        }
    }

    [TestMethod]
    public async Task FetchAsync_Unusable_2xx_Bodies_Are_InvalidResponse()
    {
        foreach (var body in new[] { "<html>oops</html>", "not json", string.Empty })
        {
            var transport = new FakeTransport().Respond(200, body);
            var service = CreateService(transport, maxRetryAttempts: 0);

            var result = await service.FetchAsync(
                Connection(),
                new DeepSeekFetchRequest { Url = "https://example.test/page" });

            Assert.IsFalse(result.IsSuccess, "body: " + body);
            Assert.IsNotNull(result.Error);
            Assert.AreEqual(DeepSeekWebErrorKind.InvalidResponse, result.Error.Kind, "body: " + body);
        }
    }

    // =====================================================================
    // HTTP 错误映射
    // =====================================================================

    [TestMethod]
    public async Task Http_Status_Codes_Map_To_Expected_Error_Kinds()
    {
        var cases = new (int Status, DeepSeekWebErrorKind Kind)[]
        {
            (400, DeepSeekWebErrorKind.InvalidRequest),
            (401, DeepSeekWebErrorKind.Unauthorized),
            (402, DeepSeekWebErrorKind.InsufficientBalance),
            (403, DeepSeekWebErrorKind.Forbidden),
            (404, DeepSeekWebErrorKind.NotFound),
            (408, DeepSeekWebErrorKind.RequestTimeout),
            (413, DeepSeekWebErrorKind.PayloadTooLarge),
            (429, DeepSeekWebErrorKind.RateLimited),
            (500, DeepSeekWebErrorKind.ServerError),
            (502, DeepSeekWebErrorKind.ServerError),
            (504, DeepSeekWebErrorKind.ServerError),
            (418, DeepSeekWebErrorKind.InvalidRequest),
        };

        foreach (var (status, expectedKind) in cases)
        {
            var transport = new FakeTransport().Respond(status, ErrorJson(status), "Reason");
            var service = CreateService(transport, maxRetryAttempts: 0);

            var result = await service.SearchAsync(Connection(), SearchRequest());

            Assert.IsFalse(result.IsSuccess, "HTTP " + status);
            Assert.IsNotNull(result.Error, "HTTP " + status);
            Assert.AreEqual(expectedKind, result.Error.Kind, "HTTP " + status);
            Assert.AreEqual(status, result.Error.HttpStatusCode, "HTTP " + status);
            Assert.AreEqual("type-" + status, result.Error.ErrorType, "HTTP " + status);
            Assert.AreEqual("code-" + status, result.Error.ErrorCode, "HTTP " + status);
            Assert.IsTrue(
                result.Error.Message.Contains("boom-" + status, StringComparison.Ordinal),
                "HTTP " + status + ": upstream error.message must be surfaced, got: " + result.Error.Message);
            Assert.IsTrue(
                result.Error.Message.Contains("HTTP " + status, StringComparison.Ordinal),
                "HTTP " + status + ": status code must be readable in the message");
        }
    }

    [TestMethod]
    public async Task FetchAsync_403_Maps_To_Forbidden()
    {
        var transport = new FakeTransport().Respond(
            403,
            """{"error":{"message":"URL blocked by security policy","type":"forbidden","code":"403"}}""");
        var service = CreateService(transport, maxRetryAttempts: 0);

        var result = await service.FetchAsync(
            Connection(),
            new DeepSeekFetchRequest { Url = "https://example.test/page" });

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNotNull(result.Error);
        Assert.AreEqual(DeepSeekWebErrorKind.Forbidden, result.Error.Kind);
        Assert.AreEqual(403, result.Error.HttpStatusCode);
        Assert.IsTrue(result.Error.Message.Contains("security policy", StringComparison.Ordinal));
        Assert.AreEqual(1, transport.CallCount, "403 must not be retried");
    }

    [TestMethod]
    public async Task Numeric_Error_Code_Is_Preserved_As_String()
    {
        var transport = new FakeTransport().Respond(
            400,
            """{"error":{"message":"bad request","type":"invalid_request_error","code":1001}}""");
        var service = CreateService(transport, maxRetryAttempts: 0);

        var result = await service.SearchAsync(Connection(), SearchRequest());

        Assert.IsNotNull(result.Error);
        Assert.AreEqual("1001", result.Error.ErrorCode);
    }

    [TestMethod]
    public async Task Error_Without_Error_Block_Uses_Status_And_Truncated_Body()
    {
        var oversized = new string('A', 700) + "TAIL-SECRET";
        var transport = new FakeTransport().Respond(500, oversized, "Internal Server Error");
        var service = CreateService(transport, maxRetryAttempts: 0);

        var result = await service.SearchAsync(Connection(), SearchRequest());

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNotNull(result.Error);
        Assert.AreEqual(DeepSeekWebErrorKind.ServerError, result.Error.Kind);
        Assert.IsTrue(result.Error.Message.Contains("HTTP 500", StringComparison.Ordinal));
        Assert.IsTrue(result.Error.Message.Contains("Internal Server Error", StringComparison.Ordinal));
        Assert.IsTrue(
            result.Error.Message.Contains(new string('A', DeepSeekWebService.MaxErrorBodyLength), StringComparison.Ordinal),
            "the first 512 body chars must be kept");
        Assert.IsFalse(
            result.Error.Message.Contains("TAIL-SECRET", StringComparison.Ordinal),
            "the error body must be truncated");
        Assert.IsLessThanOrEqualTo(
            DeepSeekWebService.MaxErrorBodyLength + 120,
            result.Error.Message.Length,
            "the truncated message must stay bounded");
        Assert.IsNull(result.Error.ErrorType);
        Assert.IsNull(result.Error.ErrorCode);
    }

    [TestMethod]
    public async Task Error_Messages_Never_Leak_Api_Key_Or_Authorization_Header()
    {
        var transport = new FakeTransport().Respond(
            401,
            """{"error":{"message":"Authentication Fails (no such user)","type":"authentication_error","code":"invalid_request_error"}}""");
        var service = CreateService(transport, maxRetryAttempts: 0);

        var result = await service.SearchAsync(Connection(), SearchRequest());

        Assert.IsNotNull(result.Error);
        Assert.IsFalse(result.Error.Message.Contains(ApiKey, StringComparison.Ordinal), "api key leaked");
        Assert.IsFalse(result.Error.Message.Contains("Bearer", StringComparison.OrdinalIgnoreCase), "auth header leaked");
        Assert.IsFalse(result.Error.Message.Contains("Authorization", StringComparison.OrdinalIgnoreCase), "auth header leaked");
    }

    // =====================================================================
    // 有界重试
    // =====================================================================

    [TestMethod]
    public async Task Retry_429_Then_Success()
    {
        var transport = new FakeTransport()
            .Respond(429, ErrorJson(429))
            .Respond(200, EmptySearchJson);
        var service = CreateService(transport);

        var result = await service.SearchAsync(Connection(), SearchRequest());

        Assert.IsTrue(result.IsSuccess, FailureMessage(result));
        Assert.AreEqual(2, transport.CallCount, "expected one retry");
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1) }, transport.Delays.ToArray());
    }

    [TestMethod]
    public async Task Retry_500_500_Then_Success_Uses_Exponential_Delays()
    {
        var transport = new FakeTransport()
            .Respond(500, ErrorJson(500))
            .Respond(500, ErrorJson(500))
            .Respond(200, EmptySearchJson);
        var service = CreateService(transport, maxRetryAttempts: 2);

        var result = await service.SearchAsync(Connection(), SearchRequest());

        Assert.IsTrue(result.IsSuccess, FailureMessage(result));
        Assert.AreEqual(3, transport.CallCount, "two retries expected");
        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) },
            transport.Delays.ToArray());
    }

    [TestMethod]
    public async Task Persistent_Server_Error_Stops_After_Bounded_Retries()
    {
        var transport = new FakeTransport()
            .Respond(500, ErrorJson(500))
            .Respond(500, ErrorJson(500))
            .Respond(500, ErrorJson(500))
            .Respond(200, EmptySearchJson); // must never be consumed
        var service = CreateService(transport, maxRetryAttempts: 2);

        var result = await service.SearchAsync(Connection(), SearchRequest());

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNotNull(result.Error);
        Assert.AreEqual(DeepSeekWebErrorKind.ServerError, result.Error.Kind);
        Assert.AreEqual(500, result.Error.HttpStatusCode);
        Assert.AreEqual(3, transport.CallCount, "first attempt + 2 retries, then give up");
        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) },
            transport.Delays.ToArray());
    }

    [TestMethod]
    public async Task Non_Retryable_400_Is_Not_Retried()
    {
        var transport = new FakeTransport().Respond(400, ErrorJson(400));
        var service = CreateService(transport);

        var result = await service.SearchAsync(Connection(), SearchRequest());

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(1, transport.CallCount, "400 must not be retried");
        Assert.IsEmpty(transport.Delays, "no delay for a non-retryable error");
    }

    [TestMethod]
    public async Task Retry_After_Header_Overrides_Exponential_Delay()
    {
        var transport = new FakeTransport()
            .Respond(429, ErrorJson(429), headers: [("Retry-After", "3")])
            .Respond(200, EmptySearchJson);
        var service = CreateService(transport);

        var result = await service.SearchAsync(Connection(), SearchRequest());

        Assert.IsTrue(result.IsSuccess, FailureMessage(result));
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(3) }, transport.Delays.ToArray());
    }

    [TestMethod]
    public async Task Out_Of_Range_Retry_After_Is_Ignored()
    {
        var transport = new FakeTransport()
            .Respond(429, ErrorJson(429), headers: [("Retry-After", "99")])
            .Respond(200, EmptySearchJson);
        var service = CreateService(transport);

        var result = await service.SearchAsync(Connection(), SearchRequest());

        Assert.IsTrue(result.IsSuccess, FailureMessage(result));
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1) }, transport.Delays.ToArray());
    }

    [TestMethod]
    public async Task FetchAsync_Retries_Server_Error_Then_Succeeds()
    {
        var transport = new FakeTransport()
            .Respond(500, ErrorJson(500))
            .Respond(200, """{"title":"T","content":"C"}""");
        var service = CreateService(transport);

        var result = await service.FetchAsync(
            Connection(),
            new DeepSeekFetchRequest { Url = "https://example.test/page" });

        Assert.IsTrue(result.IsSuccess, FailureMessage(result));
        Assert.AreEqual(2, transport.CallCount);
        Assert.AreEqual("T", result.Value!.Title);
    }

    // =====================================================================
    // 传输异常与取消
    // =====================================================================

    [TestMethod]
    public async Task Transport_Exception_Becomes_Retryable_Transport_Error()
    {
        var transport = new FakeTransport().Throw(new IOException("connection refused"));
        var service = CreateService(transport, maxRetryAttempts: 0);

        var result = await service.SearchAsync(Connection(), SearchRequest());

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNotNull(result.Error);
        Assert.AreEqual(DeepSeekWebErrorKind.Transport, result.Error.Kind);
        Assert.IsNull(result.Error.HttpStatusCode);
        Assert.IsTrue(result.Error.Message.Contains("connection refused", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Transport_Exception_Then_Success_Is_Retried()
    {
        var transport = new FakeTransport()
            .Throw(new IOException("connection reset"))
            .Respond(200, EmptySearchJson);
        var service = CreateService(transport);

        var result = await service.SearchAsync(Connection(), SearchRequest());

        Assert.IsTrue(result.IsSuccess, FailureMessage(result));
        Assert.AreEqual(2, transport.CallCount);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1) }, transport.Delays.ToArray());
    }

    [TestMethod]
    public async Task Transport_Error_Message_Does_Not_Leak_Api_Key()
    {
        var transport = new FakeTransport().Throw(
            new IOException("dial failed for key " + ApiKey));
        var service = CreateService(transport, maxRetryAttempts: 0);

        var result = await service.SearchAsync(Connection(), SearchRequest());

        Assert.IsNotNull(result.Error);
        Assert.IsFalse(result.Error.Message.Contains(ApiKey, StringComparison.Ordinal), "api key leaked");
        Assert.IsTrue(result.Error.Message.Contains("dial failed", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Pre_Cancelled_Token_Throws_OperationCanceledException()
    {
        var transport = new FakeTransport();
        var service = CreateService(transport);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.SearchAsync(Connection(), SearchRequest(), cts.Token));

        Assert.AreEqual(0, transport.CallCount, "a cancelled call must not reach transport");
    }

    [TestMethod]
    public async Task Cancellation_Inside_Transport_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var transport = new CancelInsideTransport(cts);
        var service = new DeepSeekWebService(transport, maxRetryAttempts: 0);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.SearchAsync(Connection(), SearchRequest(), cts.Token));

        Assert.AreEqual(1, transport.CallCount);
    }

    [TestMethod]
    public async Task Cancellation_During_Retry_Delay_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var transport = new FakeTransport().Respond(500, ErrorJson(500));
        var service = new DeepSeekWebService(
            transport,
            maxRetryAttempts: 2,
            (_, token) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(token);
            });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.SearchAsync(Connection(), SearchRequest(), cts.Token));

        Assert.AreEqual(1, transport.CallCount);
    }

    // =====================================================================
    // 构造函数
    // =====================================================================

    [TestMethod]
    public void Constructor_Rejects_Negative_Retry_Attempts()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new DeepSeekWebService(new FakeTransport(), maxRetryAttempts: -1));
    }

    [TestMethod]
    public void Constructor_Rejects_Null_Transport()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new DeepSeekWebService(null!, maxRetryAttempts: 0));
    }

    // =====================================================================
    // Base URL 规范化与端点拼装（DeepSeekWebEndpoints 单元测试）
    // =====================================================================

    [TestMethod]
    public void NormalizeBaseUrl_Trims_And_Validates()
    {
        var valid = new (string? Input, string Expected)[]
        {
            (null, "https://api.deepseek.com"),
            ("", "https://api.deepseek.com"),
            ("   ", "https://api.deepseek.com"),
            ("https://api.deepseek.com", "https://api.deepseek.com"),
            ("https://api.deepseek.com/", "https://api.deepseek.com"),
            ("https://api.deepseek.com///", "https://api.deepseek.com"),
            ("  https://gateway.example.test/base/  ", "https://gateway.example.test/base"),
            ("http://127.0.0.1:1234/v1", "http://127.0.0.1:1234/v1"),
        };
        foreach (var (input, expected) in valid)
            Assert.AreEqual(expected, DeepSeekWebEndpoints.NormalizeBaseUrl(input), "input: " + (input ?? "<null>"));

        var invalid = new string?[]
        {
            "ftp://x",
            "not a url",
            "https://",
            "mailto:a@b.test",
            "https://user:pass@api.deepseek.com",
            "https://user@api.deepseek.com",
        };
        foreach (var input in invalid)
            Assert.IsNull(DeepSeekWebEndpoints.NormalizeBaseUrl(input), "input: " + input);
    }

    [TestMethod]
    public void BuildUrl_Joins_Normalized_Base_And_Path()
    {
        Assert.AreEqual(
            "https://api.deepseek.com/v1/tools/search",
            DeepSeekWebEndpoints.BuildUrl(null, DeepSeekWebEndpoints.SearchPath));
        Assert.AreEqual(
            "https://api.deepseek.com/v1/tools/search",
            DeepSeekWebEndpoints.BuildUrl("https://api.deepseek.com/", DeepSeekWebEndpoints.SearchPath));
        Assert.AreEqual(
            "https://api.deepseek.com/v1/tools/fetch",
            DeepSeekWebEndpoints.BuildUrl("https://api.deepseek.com", DeepSeekWebEndpoints.FetchPath));
        Assert.AreEqual(
            "https://gateway.example.test/base/v1/tools/search",
            DeepSeekWebEndpoints.BuildUrl("https://gateway.example.test/base/", DeepSeekWebEndpoints.SearchPath));
        Assert.IsNull(DeepSeekWebEndpoints.BuildUrl("ftp://x", DeepSeekWebEndpoints.SearchPath));
        Assert.IsNull(DeepSeekWebEndpoints.BuildUrl("not a url", DeepSeekWebEndpoints.SearchPath));
    }

    // =====================================================================
    // 辅助
    // =====================================================================

    private static DeepSeekWebConnection Connection(
        string? baseUrl = null,
        string? apiKey = null,
        int timeoutSeconds = 30) => new()
        {
            ApiKey = apiKey ?? ApiKey,
            BaseUrl = baseUrl ?? BaseUrl,
            TimeoutSeconds = timeoutSeconds,
        };

    private static DeepSeekSearchRequest SearchRequest(string query = "query") => new() { Query = query };

    private static DeepSeekWebService CreateService(FakeTransport transport, int maxRetryAttempts = 2) =>
        new(transport, maxRetryAttempts, transport.RecordDelay);

    private static string ErrorJson(int status) =>
        "{\"error\":{\"message\":\"boom-" + status
        + "\",\"type\":\"type-" + status
        + "\",\"param\":null,\"code\":\"code-" + status
        + "\"}}";

    private static string FailureMessage<T>(DeepSeekWebResult<T> result)
        where T : class =>
        result.Error is null
            ? "unexpected failure without an error object"
            : $"{result.Error.Kind}: {result.Error.Message}";

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// 假传输：记录每次出站请求，按队列返回响应或抛异常，并记录服务注入的退避延迟
    /// （测试永不真正等待）。
    /// </summary>
    private sealed class FakeTransport : IDeepSeekWebTransport
    {
        private readonly Queue<Func<DeepSeekWebTransportRequest, DeepSeekWebTransportResponse>> _steps = new();

        public List<DeepSeekWebTransportRequest> Requests { get; } = [];

        public List<TimeSpan> Delays { get; } = [];

        public int CallCount => Requests.Count;

        public Func<TimeSpan, CancellationToken, Task> RecordDelay => (delay, _) =>
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        };

        public FakeTransport Respond(DeepSeekWebTransportResponse response)
        {
            _steps.Enqueue(_ => response);
            return this;
        }

        public FakeTransport Respond(
            int statusCode,
            string body,
            string reasonPhrase = "",
            (string Name, string Value)[]? headers = null)
        {
            var headerDictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (headers is not null)
            {
                foreach (var (name, value) in headers)
                    headerDictionary[name] = value;
            }

            return Respond(new DeepSeekWebTransportResponse
            {
                StatusCode = statusCode,
                ReasonPhrase = reasonPhrase,
                Body = body,
                Headers = headerDictionary,
            });
        }

        public FakeTransport Throw(Exception exception)
        {
            _steps.Enqueue(_ => throw exception);
            return this;
        }

        public Task<DeepSeekWebTransportResponse> SendAsync(
            DeepSeekWebTransportRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);

            if (_steps.Count == 0)
            {
                throw new InvalidOperationException(
                    "FakeTransport has no queued response for call #" + Requests.Count);
            }

            return Task.FromResult(_steps.Dequeue()(request));
        }
    }

    /// <summary>模拟「调用方在传输层内部取消 token」的传输实现。</summary>
    private sealed class CancelInsideTransport : IDeepSeekWebTransport
    {
        private readonly CancellationTokenSource _cts;

        public CancelInsideTransport(CancellationTokenSource cts) => _cts = cts;

        public int CallCount { get; private set; }

        public Task<DeepSeekWebTransportResponse> SendAsync(
            DeepSeekWebTransportRequest request,
            CancellationToken ct = default)
        {
            CallCount++;
            _cts.Cancel();
            throw new OperationCanceledException(_cts.Token);
        }
    }
}
