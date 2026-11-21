using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Observability;
using PuddingDeepSeekWeb;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <c>deepseek_search</c> / <c>deepseek_fetch</c> 的<b>计费计量</b>契约。
/// <para>背景：这两个工具背后是 DeepSeek 官方按次计费 API（每 1000 次成功请求 5 元），
/// 在加埋点之前调用次数在任何报表里都看不见。本文件锁定四件事：</para>
/// <list type="number">
/// <item>真的发出请求的调用（成功 / 上游失败 / 传输异常）各上报恰好一条计费尝试；</item>
/// <item>凭据缺失、参数非法这些<b>没有发请求</b>的失败一条都不报（否则用量报表虚高）；</item>
/// <item>维度不含敏感内容：fetch 只记 host + 长度，search 只记 query 长度；</item>
/// <item>telemetry 是 best-effort：sink 为 null 或 sink 抛异常都不能让工具调用失败。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class DeepSeekWebTelemetryTests
{
    private const string SearchMetricName = "deepseek_search_attempt";
    private const string FetchMetricName = "deepseek_fetch_attempt";

    // ── deepseek_search ────────────────────────────────────────────────────

    [TestMethod]
    public async Task Search_Success_Reports_One_Billed_Attempt()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService
        {
            SearchHandler = _ => DeepSeekWebResult<DeepSeekSearchResponse>.Ok(new DeepSeekSearchResponse
            {
                Results = [new DeepSeekSearchResult { Title = "Example", Url = "https://example.com/" }],
            }),
        };
        var sink = new RecordingTelemetryMetricSink();
        var tool = new DeepSeekSearchTool(
            service, temp.CreateSettingsProvider(), NullLogger<DeepSeekSearchTool>.Instance, sink);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, """
            {"query":"secret-topic","count":5,"need_content":true}
            """);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(1, sink.Metrics);
        var metric = sink.Metrics[0];
        Assert.AreEqual(SearchMetricName, metric.Name);
        Assert.AreEqual("deepseek_search", metric.Source);
        Assert.AreEqual(TelemetryMetricCategories.Tool, metric.Category);
        Assert.AreEqual(TelemetryMetricStatuses.Succeeded, metric.Status);
        Assert.AreEqual("info", metric.Severity);
        Assert.IsNull(metric.ErrorCode);

        var dimensions = metric.Dimensions;
        Assert.IsNotNull(dimensions);
        Assert.AreEqual("12", dimensions["query_length"], "'secret-topic' 是 12 个字符");
        Assert.AreEqual("5", dimensions["count"]);
        Assert.AreEqual("true", dimensions["need_content"]);
        Assert.AreEqual("search.providers.json", dimensions["source"]);
        // query 正文（可能含隐私）绝不进维度，只留长度。
        Assert.IsFalse(
            dimensions.Values.Any(v => v.Contains("secret-topic", StringComparison.Ordinal)),
            "telemetry 维度里出现了 query 正文");
    }

    [TestMethod]
    public async Task Search_Upstream_Failure_Reports_One_Failed_Attempt()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService
        {
            SearchHandler = _ => DeepSeekWebResult<DeepSeekSearchResponse>.Fail(
                DeepSeekWebErrorKind.RateLimited, "search qps exceeded", 429),
        };
        var sink = new RecordingTelemetryMetricSink();
        var tool = new DeepSeekSearchTool(
            service, temp.CreateSettingsProvider(), NullLogger<DeepSeekSearchTool>.Instance, sink);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"query":"deepseek"}""");

        Assert.IsFalse(result.Success, "上游返回 429 时工具必须失败");
        Assert.HasCount(1, sink.Metrics);
        var metric = sink.Metrics[0];
        Assert.AreEqual(SearchMetricName, metric.Name);
        Assert.AreEqual(TelemetryMetricStatuses.Failed, metric.Status);
        Assert.AreEqual("error", metric.Severity);
        Assert.AreEqual(nameof(DeepSeekWebErrorKind.RateLimited), metric.ErrorCode);
    }

    [TestMethod]
    public async Task Search_Transport_Failure_Reports_One_Failed_Attempt()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService
        {
            SearchHandler = _ => throw new HttpRequestException("connection reset"),
        };
        var sink = new RecordingTelemetryMetricSink();
        var tool = new DeepSeekSearchTool(
            service, temp.CreateSettingsProvider(), NullLogger<DeepSeekSearchTool>.Instance, sink);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"query":"deepseek"}""");

        Assert.IsFalse(result.Success, "传输异常时工具必须失败");
        Assert.HasCount(1, sink.Metrics);
        var metric = sink.Metrics[0];
        Assert.AreEqual(SearchMetricName, metric.Name);
        Assert.AreEqual(TelemetryMetricStatuses.Failed, metric.Status);
        Assert.AreEqual("transport", metric.ErrorCode);
    }

    [TestMethod]
    public async Task Search_Missing_Credentials_Reports_No_Billed_Attempt()
    {
        // 刻意不写任何 provider 配置：工具在发请求之前就该失败，因此这不是一次计费尝试。
        using var temp = new DeepSeekTempDirectory();
        var service = new StubDeepSeekWebService();
        var sink = new RecordingTelemetryMetricSink();
        var tool = new DeepSeekSearchTool(
            service, temp.CreateSettingsProvider(), NullLogger<DeepSeekSearchTool>.Instance, sink);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"query":"deepseek"}""");

        Assert.IsFalse(result.Success, "凭据缺失时工具必须失败");
        Assert.IsEmpty(service.SearchCalls, "凭据缺失时绝不能发出网络请求");
        Assert.IsFalse(
            sink.Metrics.Any(m => m.Name == SearchMetricName),
            "没有发出请求的失败不得计为一次计费尝试（否则用量报表虚高）");
    }

    [TestMethod]
    public async Task Search_Invalid_Parameters_Reports_No_Billed_Attempt()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService();
        var sink = new RecordingTelemetryMetricSink();
        var tool = new DeepSeekSearchTool(
            service, temp.CreateSettingsProvider(), NullLogger<DeepSeekSearchTool>.Instance, sink);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"query":"   "}""");

        Assert.IsFalse(result.Success, "空 query 必须在本地被拦下");
        Assert.IsEmpty(service.SearchCalls, "参数非法时绝不能发出网络请求");
        Assert.IsFalse(
            sink.Metrics.Any(m => m.Name == SearchMetricName),
            "参数非法不是一次计费尝试");
    }

    [TestMethod]
    public async Task Search_With_Null_Telemetry_Still_Succeeds()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService();
        // 不传 sink：未接入计量的宿主机必须保持原有行为。
        var tool = new DeepSeekSearchTool(
            service, temp.CreateSettingsProvider(), NullLogger<DeepSeekSearchTool>.Instance);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"query":"deepseek"}""");

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(1, service.SearchCalls);
    }

    [TestMethod]
    public async Task Search_Throwing_Telemetry_Sink_Does_Not_Break_Tool()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService();
        var sink = new ThrowingTelemetryMetricSink();
        var tool = new DeepSeekSearchTool(
            service, temp.CreateSettingsProvider(), NullLogger<DeepSeekSearchTool>.Instance, sink);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"query":"deepseek"}""");

        Assert.AreEqual(1, sink.Calls, "成功路径必须先尝试上报");
        Assert.IsTrue(result.Success, "telemetry 抛异常不得让工具调用失败: " + result.Error);
    }

    // ── deepseek_fetch ────────────────────────────────────────────────────

    [TestMethod]
    public async Task Fetch_Success_Reports_Host_And_Length_But_Never_The_Url()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        const string url = "https://example.com/private/path?token=abc123";
        var service = new StubDeepSeekWebService
        {
            FetchHandler = _ => DeepSeekWebResult<DeepSeekFetchResponse>.Ok(new DeepSeekFetchResponse
            {
                Title = "Example",
                Content = "body",
            }),
        };
        var sink = new RecordingTelemetryMetricSink();
        var tool = new DeepSeekFetchTool(
            service, temp.CreateSettingsProvider(), NullLogger<DeepSeekFetchTool>.Instance, sink);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, $$"""{"url":"{{url}}"}""");

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(1, sink.Metrics);
        var metric = sink.Metrics[0];
        Assert.AreEqual(FetchMetricName, metric.Name);
        Assert.AreEqual("deepseek_fetch", metric.Source);
        Assert.AreEqual(TelemetryMetricCategories.Tool, metric.Category);
        Assert.AreEqual(TelemetryMetricStatuses.Succeeded, metric.Status);

        var dimensions = metric.Dimensions;
        Assert.IsNotNull(dimensions);
        Assert.AreEqual("example.com", dimensions["host"]);
        Assert.AreEqual(url.Length.ToString(), dimensions["url_length"]);
        // 完整 URL（含路径与查询串）绝不进维度。
        Assert.IsFalse(
            dimensions.Values.Any(v => v.Contains("token=abc123", StringComparison.Ordinal)
                                       || v.Contains("/private/path", StringComparison.Ordinal)),
            "telemetry 维度里出现了完整 URL");
    }

    [TestMethod]
    public async Task Fetch_Invalid_Url_Reports_No_Billed_Attempt()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService();
        var sink = new RecordingTelemetryMetricSink();
        var tool = new DeepSeekFetchTool(
            service, temp.CreateSettingsProvider(), NullLogger<DeepSeekFetchTool>.Instance, sink);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"url":"ftp://example.com/x"}""");

        Assert.IsFalse(result.Success, "非 http(s) 的 URL 必须在本地被拦下");
        Assert.IsEmpty(service.FetchCalls, "URL 非法时绝不能发出网络请求");
        Assert.IsFalse(
            sink.Metrics.Any(m => m.Name == FetchMetricName),
            "URL 非法不是一次计费尝试");
    }

    [TestMethod]
    public async Task Fetch_Transport_Failure_Reports_One_Failed_Attempt()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService
        {
            FetchHandler = _ => throw new HttpRequestException("connection reset"),
        };
        var sink = new RecordingTelemetryMetricSink();
        var tool = new DeepSeekFetchTool(
            service, temp.CreateSettingsProvider(), NullLogger<DeepSeekFetchTool>.Instance, sink);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"url":"https://example.com/a"}""");

        Assert.IsFalse(result.Success, "传输异常时工具必须失败");
        Assert.HasCount(1, sink.Metrics);
        var metric = sink.Metrics[0];
        Assert.AreEqual(FetchMetricName, metric.Name);
        Assert.AreEqual(TelemetryMetricStatuses.Failed, metric.Status);
        Assert.AreEqual("transport", metric.ErrorCode);
        Assert.AreEqual("example.com", metric.Dimensions!["host"]);
    }

    [TestMethod]
    public async Task Fetch_Throwing_Telemetry_Sink_Does_Not_Break_Tool()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService();
        var sink = new ThrowingTelemetryMetricSink();
        var tool = new DeepSeekFetchTool(
            service, temp.CreateSettingsProvider(), NullLogger<DeepSeekFetchTool>.Instance, sink);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"url":"https://example.com/a"}""");

        Assert.AreEqual(1, sink.Calls, "成功路径必须先尝试上报");
        Assert.IsTrue(result.Success, "telemetry 抛异常不得让工具调用失败: " + result.Error);
    }

    // ── doubles ───────────────────────────────────────────────────────────

    /// <summary>按序记录收到的每一条 <see cref="TelemetryMetric"/>（同步记录，断言可直接读）。</summary>
    private sealed class RecordingTelemetryMetricSink : ITelemetryMetricSink
    {
        public List<TelemetryMetric> Metrics { get; } = [];

        public Task RecordAsync(TelemetryMetric metric, CancellationToken ct = default)
        {
            Metrics.Add(metric);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 模拟「遥测后端坏了」的 sink。<b>同步</b>抛出（而不是返回 faulted task）：只有同步抛才能证明
    /// 工具里那层 try/catch 真的在保护调用结果，否则异常会变成无人观察的任务异常、测不出问题。
    /// </summary>
    private sealed class ThrowingTelemetryMetricSink : ITelemetryMetricSink
    {
        public int Calls { get; private set; }

        public Task RecordAsync(TelemetryMetric metric, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("telemetry backend is down");
        }
    }
}
