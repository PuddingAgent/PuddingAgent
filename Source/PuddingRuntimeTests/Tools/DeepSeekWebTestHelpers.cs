using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingDeepSeekWeb;
using PuddingRuntime.Services.DeepSeekWeb;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>DeepSeek Web 工具/设置测试共用的临时 data 目录与 double（不读真实 D:\data）。</summary>
internal sealed class DeepSeekTempDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(
        Path.GetTempPath(),
        "pudding-deepseek-web-tests",
        Guid.NewGuid().ToString("N"));

    public PuddingDataPaths Paths { get; }

    public DeepSeekTempDirectory()
    {
        Directory.CreateDirectory(Root);
        Paths = PuddingDataPaths.FromRoot(Root);
    }

    public string SearchProvidersPath => Paths.SystemConfigFile("search.providers.json");

    public string LlmProvidersPath => Paths.SystemConfigFile("llm.providers.json");

    public DeepSeekWebSettingsProvider CreateSettingsProvider() =>
        new(Paths, NullLogger<DeepSeekWebSettingsProvider>.Instance);

    public void WriteSearchProviders(string json) => WriteFile(SearchProvidersPath, json);

    public void WriteLlmProviders(string json) => WriteFile(LlmProvidersPath, json);

    public void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Paths.ConfigRoot);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}

/// <summary>记录最后一次请求的假 <see cref="IWebClient"/>（可按顺序返回响应/抛异常）。</summary>
internal sealed class RecordingWebClient : IWebClient
{
    private readonly Queue<WebClientResponse> _responses = new();

    public RecordingWebClient(params WebClientResponse[] responses)
    {
        foreach (var response in responses)
            _responses.Enqueue(response);
    }

    public List<WebClientRequest> Requests { get; } = [];

    public WebClientRequest? LastRequest => Requests.Count == 0 ? null : Requests[^1];

    public Exception? ExceptionToThrow { get; set; }

    public Task<WebClientResponse> SendAsync(WebClientRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (ExceptionToThrow is not null)
            throw ExceptionToThrow;
        if (_responses.Count == 0)
            throw new InvalidOperationException("RecordingWebClient has no stubbed response left.");

        return Task.FromResult(_responses.Dequeue());
    }
}

/// <summary>可编程返回值的假 <see cref="IDeepSeekWebService"/>（记录调用参数，不触网）。</summary>
internal sealed class StubDeepSeekWebService : IDeepSeekWebService
{
    public List<(DeepSeekWebConnection Connection, DeepSeekSearchRequest Request)> SearchCalls { get; } = [];

    public List<(DeepSeekWebConnection Connection, DeepSeekFetchRequest Request)> FetchCalls { get; } = [];

    public Func<DeepSeekSearchRequest, DeepSeekWebResult<DeepSeekSearchResponse>> SearchHandler { get; set; } =
        _ => DeepSeekWebResult<DeepSeekSearchResponse>.Ok(new DeepSeekSearchResponse());

    public Func<DeepSeekFetchRequest, DeepSeekWebResult<DeepSeekFetchResponse>> FetchHandler { get; set; } =
        _ => DeepSeekWebResult<DeepSeekFetchResponse>.Ok(new DeepSeekFetchResponse());

    public DeepSeekSearchRequest? LastSearchRequest =>
        SearchCalls.Count == 0 ? null : SearchCalls[^1].Request;

    public DeepSeekFetchRequest? LastFetchRequest =>
        FetchCalls.Count == 0 ? null : FetchCalls[^1].Request;

    public Task<DeepSeekWebResult<DeepSeekSearchResponse>> SearchAsync(
        DeepSeekWebConnection connection,
        DeepSeekSearchRequest request,
        CancellationToken ct = default)
    {
        SearchCalls.Add((connection, request));
        return Task.FromResult(SearchHandler(request));
    }

    public Task<DeepSeekWebResult<DeepSeekFetchResponse>> FetchAsync(
        DeepSeekWebConnection connection,
        DeepSeekFetchRequest request,
        CancellationToken ct = default)
    {
        FetchCalls.Add((connection, request));
        return Task.FromResult(FetchHandler(request));
    }
}

/// <summary>工具构造 / 执行 / 线上响应 fixture 的共用帮助器。</summary>
internal static class DeepSeekWebTestTools
{
    internal static DeepSeekSearchTool CreateSearchTool(
        DeepSeekTempDirectory temp,
        IDeepSeekWebService service) =>
        new(service, temp.CreateSettingsProvider(), NullLogger<DeepSeekSearchTool>.Instance);

    internal static DeepSeekFetchTool CreateFetchTool(
        DeepSeekTempDirectory temp,
        IDeepSeekWebService service) =>
        new(service, temp.CreateSettingsProvider(), NullLogger<DeepSeekFetchTool>.Instance);

    /// <summary>用真实组件实现 + 宿主传输适配器 + 记录型 IWebClient 组装端到端链路。</summary>
    internal static DeepSeekWebService CreateRealService(
        RecordingWebClient webClient,
        int maxRetryAttempts = 0) =>
        new(new WebClientDeepSeekTransport(webClient), maxRetryAttempts);

    internal static Task<ToolExecutionResult> ExecuteAsync(
        IPuddingTool tool,
        string argumentsJson) =>
        tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = argumentsJson,
            Context = new ToolExecutionContext
            {
                WorkspaceId = "workspace-1",
                SessionId = "session-1",
                AgentInstanceId = "agent-1",
            },
        });

    internal static WebClientResponse JsonResponse(
        int statusCode,
        string body,
        string reasonPhrase = "OK") => new()
    {
        StatusCode = statusCode,
        ReasonPhrase = reasonPhrase,
        ContentType = "application/json",
        Body = body,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        FinalUrl = "https://api.deepseek.com",
    };

    /// <summary>search.providers.json 的 deepseek_search 节（默认 test 值）。</summary>
    internal static string SearchConfig(
        string apiKey = "test-key",
        string baseUrl = "https://custom.deepseek.local",
        bool enabled = true,
        int? timeoutSeconds = 12) =>
        JsonSerializer.Serialize(new
        {
            deepseek_search = new
            {
                enabled,
                baseUrl,
                apiKey,
                timeoutSeconds,
            },
        });

    /// <summary>llm.providers.json（providers 数组 + providerId=deepseek）。</summary>
    internal static string LlmConfig(
        string apiKey,
        string baseUrl = "https://llm.deepseek.local",
        bool isEnabled = true,
        string? apiKeyRef = null) =>
        JsonSerializer.Serialize(new
        {
            providers = new[]
            {
                new
                {
                    providerId = "deepseek",
                    name = "DeepSeek",
                    baseUrl,
                    apiKey,
                    apiKeyRef,
                    isEnabled,
                    models = Array.Empty<object>(),
                },
            },
        });
}
