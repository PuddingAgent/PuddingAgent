using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// <see cref="JevDecisionService"/> 契约测试：请求体形状、结构化答案解析、非 2xx 错误映射。
/// 全部经 stub <see cref="HttpMessageHandler"/> 离线执行，不访问网络。
/// </summary>
[TestClass]
public sealed class JevDecisionServiceTests
{
    private const string BaseUrl = "https://jev.example.test";
    private const string DecideUrl = BaseUrl + "/api/v1/decide";

    private const string SuccessResponse = """
        {
          "model": "jev-1.13.0",
          "answers": {
            "mood": {"type":"choice","choice":"risk_on","confidence":0.72,"probabilities":{"risk_on":0.72,"neutral":0.28}},
            "size": {"type":"score","score":7.5,"confidence":0.55,"probabilities":[0.02,0.05,0.13,0.25,0.55],"legend":["空仓","低","中","高","满仓"]},
            "bull": {"type":"noul","noul":0.31}
          },
          "usage": {"input_tokens":62,"output_tokens":0,"cost_usd":0.000026,"credits_remaining_usd":4.999974}
        }
        """;

    private static JevDecisionOptions CreateOptions(int maxRetries = 0) => new()
    {
        BaseUrl = BaseUrl,
        ApiKey = "test-key",
        ModelId = "jev-latest",
        MaxRetries = maxRetries,
        RetryDelayMilliseconds = 1,
    };

    private static JevDecisionRequest CreateRequest() => new()
    {
        State = JsonNode.Parse("""{"portfolio":"60/40","drawdown":-0.12}"""),
        Questions =
        [
            new JevQuestion
            {
                Name = "mood",
                Type = JevQuestionType.Choice,
                Instructions = "选择当前市场情绪",
                ChoiceCriteria = new Dictionary<string, string?>
                {
                    ["risk_on"] = "风险偏好上升",
                    ["neutral"] = null,
                },
            },
            new JevQuestion
            {
                Name = "size",
                Type = JevQuestionType.Score,
                Instructions = "给出建议仓位",
                ScoreCriteria = ["空仓", "低", "中", "高", "满仓"],
            },
            new JevQuestion
            {
                Name = "bull",
                Type = JevQuestionType.Noul,
                Instructions = "未来一个季度是否为牛市",
                NoulCriteria = new JevBoolCriteria { True = "趋势向上", False = "趋势向下" },
            },
        ],
    };

    private static JevDecisionService CreateService(
        RecordingHandler handler,
        JevDecisionOptions? options = null)
        => new(
            new StaticHttpClientFactory(new HttpClient(handler)),
            new StubOptionsProvider(options ?? CreateOptions()),
            NullLogger<JevDecisionService>.Instance);

    // ── 1. 请求体形状 + choice/score/noul 结构化解析 ──────────────────────

    [TestMethod]
    public async Task DecideAsync_ChoiceScoreNoul_PostsContractAndParsesStructuredAnswers()
    {
        var handler = new RecordingHandler(
            (_, _) => Json(HttpStatusCode.OK, SuccessResponse));
        var service = CreateService(handler);

        var result = await service.DecideAsync(CreateRequest());

        // —— 请求契约 ——
        Assert.HasCount(1, handler.Requests);
        var sent = handler.Requests[0];
        Assert.AreEqual(DecideUrl, sent.Uri);
        Assert.AreEqual("POST", sent.Method);
        Assert.AreEqual("Bearer", sent.AuthorizationScheme);
        Assert.AreEqual("test-key", sent.AuthorizationParameter);
        Assert.AreEqual("application/json", sent.ContentType);

        var payload = JsonNode.Parse(sent.Body)!;
        Assert.AreEqual("jev-latest", payload["model"]!.GetValue<string>());
        Assert.IsTrue(JsonNode.DeepEquals(
            JsonNode.Parse("""{"portfolio":"60/40","drawdown":-0.12}"""),
            payload["state"]));
        var questions = payload["questions"]!.AsObject();
        Assert.HasCount(3, questions);
        Assert.AreEqual("choice", questions["mood"]!["type"]!.GetValue<string>());
        Assert.AreEqual("选择当前市场情绪", questions["mood"]!["instructions"]!.GetValue<string>());
        Assert.AreEqual("风险偏好上升", questions["mood"]!["criteria"]!["risk_on"]!.GetValue<string>());
        Assert.IsNull(questions["mood"]!["criteria"]!["neutral"]);
        Assert.AreEqual("score", questions["size"]!["type"]!.GetValue<string>());
        CollectionAssert.AreEqual(
            new[] { "空仓", "低", "中", "高", "满仓" },
            questions["size"]!["criteria"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        Assert.AreEqual("noul", questions["bull"]!["type"]!.GetValue<string>());
        Assert.AreEqual("趋势向上", questions["bull"]!["criteria"]!["true"]!.GetValue<string>());
        Assert.AreEqual("趋势向下", questions["bull"]!["criteria"]!["false"]!.GetValue<string>());

        // —— 结构化答案 ——
        Assert.AreEqual("jev-1.13.0", result.Model);
        Assert.HasCount(3, result.Answers);

        var mood = result.GetAnswer("mood")!;
        Assert.AreEqual("choice", mood.Type);
        Assert.AreEqual("risk_on", mood.Choice);
        Assert.AreEqual(0.72, mood.Confidence!.Value);
        Assert.AreEqual(0.72, mood.Probabilities!["risk_on"]);
        Assert.AreEqual(0.28, mood.Probabilities!["neutral"]);
        Assert.IsNull(mood.Score);
        Assert.IsNull(mood.Noul);

        var size = result.GetAnswer("size")!;
        Assert.AreEqual("score", size.Type);
        Assert.AreEqual(7.5, size.Score!.Value);
        Assert.HasCount(5, size.ScoreProbabilities!);
        Assert.AreEqual(0.55, size.ScoreProbabilities![4]);
        CollectionAssert.AreEqual(
            new[] { "空仓", "低", "中", "高", "满仓" },
            size.Legend!.ToArray());
        Assert.IsNull(size.Probabilities);

        var bull = result.GetAnswer("bull")!;
        Assert.AreEqual("noul", bull.Type);
        Assert.AreEqual(0.31, bull.Noul!.Value);
        Assert.IsNull(bull.Confidence);

        Assert.AreEqual(62, result.Usage!.InputTokens!.Value);
        Assert.AreEqual(0, result.Usage!.OutputTokens!.Value);
        Assert.AreEqual(0.000026m, result.Usage!.CostUsd!.Value);
        Assert.AreEqual(4.999974m, result.Usage!.CreditsRemainingUsd!.Value);

        Assert.IsNull(result.GetAnswer("missing"), "缺失提问不得被伪造出占位答案。");
    }

    [TestMethod]
    public async Task DecideAsync_RequestModelOverride_WinsOverOptions()
    {
        var handler = new RecordingHandler(
            (_, _) => Json(HttpStatusCode.OK, SuccessResponse));
        var service = CreateService(handler);
        var request = CreateRequest() with { ModelId = "jev-1.13.0" };

        await service.DecideAsync(request);

        var payload = JsonNode.Parse(handler.Requests[0].Body)!;
        Assert.AreEqual("jev-1.13.0", payload["model"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task DecideAsync_StateArraysAndStrings_AreSerializedVerbatim()
    {
        var handler = new RecordingHandler(
            (_, _) => Json(HttpStatusCode.OK, SuccessResponse));
        var service = CreateService(handler);

        var request = new JevDecisionRequest
        {
            State = JsonNode.Parse("""[1,2,3]"""),
            Questions =
            [
                new JevQuestion
                {
                    Name = "pick",
                    Type = JevQuestionType.Choice,
                    ChoiceCriteria = new Dictionary<string, string?> { ["a"] = "A" },
                },
            ],
        };

        await service.DecideAsync(request);

        var payload = JsonNode.Parse(handler.Requests[0].Body)!;
        Assert.IsTrue(JsonNode.DeepEquals(
            JsonNode.Parse("[1,2,3]"), payload["state"]));
    }

    // ── 2. 非 2xx / 上游错误映射 ─────────────────────────────────────────

    [TestMethod]
    [DataRow(400, "jev.invalid_request")]
    [DataRow(401, "jev.unauthorized")]
    [DataRow(402, "jev.insufficient_credits")]
    [DataRow(403, "jev.account_disabled")]
    [DataRow(500, "jev.http_error")]
    public async Task DecideAsync_NonSuccessStatus_MapsStableErrorCode(int status, string expectedCode)
    {
        var handler = new RecordingHandler(
            (_, _) => Json((HttpStatusCode)status, """{"error":"upstream says no"}"""));
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<JevDecisionException>(
            () => service.DecideAsync(CreateRequest()));

        Assert.AreEqual(expectedCode, exception.Code);
        Assert.AreEqual(status, exception.HttpStatusCode);
        Assert.IsTrue(
            exception.Message.Contains($"HTTP {status}", StringComparison.Ordinal),
            "异常消息必须含 HTTP 状态码。");
        Assert.AreEqual("""{"error":"upstream says no"}""", exception.ResponseBody);
    }

    [TestMethod]
    public async Task DecideAsync_UpstreamBadGateway_RetriesWithBackoffThenThrows()
    {
        var handler = new RecordingHandler(
            (_, _) => Json(HttpStatusCode.BadGateway, """{"error":"upstream"}"""));
        var service = CreateService(handler, CreateOptions(maxRetries: 2));

        var exception = await Assert.ThrowsAsync<JevDecisionException>(
            () => service.DecideAsync(CreateRequest()));

        Assert.AreEqual("jev.upstream_error", exception.Code);
        Assert.AreEqual(502, exception.HttpStatusCode);
        Assert.HasCount(3, handler.Requests, "502 应按 MaxRetries=2 重试到 3 次尝试。");
    }

    [TestMethod]
    public async Task DecideAsync_UpstreamRecoversWithinRetryBudget_ReturnsAnswer()
    {
        var handler = new RecordingHandler(
            (_, attempt) => attempt == 1
                ? Json(HttpStatusCode.BadGateway, """{"error":"transient"}""")
                : Json(HttpStatusCode.OK, SuccessResponse));
        var service = CreateService(handler, CreateOptions(maxRetries: 2));

        var result = await service.DecideAsync(CreateRequest());

        Assert.AreEqual("jev-1.13.0", result.Model);
        Assert.HasCount(2, handler.Requests);
    }

    [TestMethod]
    public async Task DecideAsync_ErrorBody_IsTruncatedTo4096Characters()
    {
        var body = new string('x', 10_000);
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.BadRequest, body));
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<JevDecisionException>(
            () => service.DecideAsync(CreateRequest()));

        Assert.AreEqual(4_096, exception.ResponseBody!.Length);
    }

    // ── 3. fail-closed 本地校验 ──────────────────────────────────────────

    [TestMethod]
    public async Task DecideAsync_ResponseWithoutAnswers_ThrowsInvalidResponse()
    {
        var handler = new RecordingHandler(
            (_, _) => Json(HttpStatusCode.OK, """{"model":"jev-1.13.0"}"""));
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<JevDecisionException>(
            () => service.DecideAsync(CreateRequest()));

        Assert.AreEqual("jev.invalid_response", exception.Code);
        Assert.AreEqual(200, exception.HttpStatusCode);
    }

    [TestMethod]
    public async Task DecideAsync_ScoreWithTooFewLevels_ThrowsBeforeSending()
    {
        var handler = new RecordingHandler(
            (_, _) => Json(HttpStatusCode.OK, SuccessResponse));
        var service = CreateService(handler);
        var request = new JevDecisionRequest
        {
            State = JsonNode.Parse("\"state\""),
            Questions =
            [
                new JevQuestion
                {
                    Name = "size",
                    Type = JevQuestionType.Score,
                    ScoreCriteria = ["只有一个层级"],
                },
            ],
        };

        var exception = await Assert.ThrowsAsync<JevDecisionException>(
            () => service.DecideAsync(request));

        Assert.AreEqual("jev.invalid_request", exception.Code);
        Assert.IsNull(exception.HttpStatusCode);
        Assert.IsEmpty(handler.Requests, "本地校验失败不得发出 HTTP 请求。");
    }

    // ── stubs ────────────────────────────────────────────────────────────

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubOptionsProvider(JevDecisionOptions options) : IJevDecisionOptionsProvider
    {
        public Task<JevDecisionOptions> GetOptionsAsync(CancellationToken ct = default)
            => Task.FromResult(options);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            return responder(request, Requests.Count);
        }
    }

    private sealed record RecordedRequest(
        string Method,
        string Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string? ContentType,
        string Body);
}
