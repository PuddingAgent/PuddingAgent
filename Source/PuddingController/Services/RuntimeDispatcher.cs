using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using PuddingCode.Platform;

namespace PuddingController.Services;

/// <summary>
/// Runtime 分发器——将执行请求发送到选定的 Runtime 节点。
/// 1. 优先从 RuntimeRegistryService 挑选活跃节点（最少 Session 优先）。
/// 2. 若注册表为空，回退到配置文件中的静态端点（兼容 Dev 单机模式）。
/// </summary>
public sealed class RuntimeDispatcher
{
    private readonly RuntimeRegistryService _registry;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RuntimeDispatcher> _logger;
    private string _fallbackEndpoint;

    public RuntimeDispatcher(
        RuntimeRegistryService registry,
        IHttpClientFactory httpClientFactory,
        ILogger<RuntimeDispatcher> logger,
        IConfiguration configuration)
    {
        _registry = registry;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _fallbackEndpoint = configuration["Pudding:RuntimeFallbackEndpoint"] ?? "http://localhost:5100";
    }

    /// <summary>配置兜底静态端点（从 appsettings 读取，用于无注册节点时回退）。</summary>
    public void SetFallbackEndpoint(string endpoint) => _fallbackEndpoint = endpoint;

    /// <summary>将消息分发到选定的 Runtime 节点执行。</summary>
    public async Task<RuntimeDispatchResult> DispatchAsync(RuntimeDispatchRequest request, CancellationToken ct = default)
    {
        // 优先使用注册表中的节点，兜底使用静态配置
        var node = _registry.PickNode();
        var endpoint = node?.Endpoint ?? _fallbackEndpoint;

        _logger.LogInformation("[RuntimeDispatch] session={SessionId} → {Endpoint} (via {Source})",
            request.SessionId, endpoint, node is null ? "fallback" : "registry");

        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.BaseAddress = new Uri(endpoint);

            var response = await httpClient.PostAsJsonAsync("/api/runtime/execute", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<RuntimeDispatchResult>(ct);
            return result ?? new RuntimeDispatchResult
            {
                SessionId = request.SessionId,
                AgentInstanceId = "",
                IsSuccess = false,
                ErrorMessage = "Empty response from Runtime"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RuntimeDispatch] Failed to dispatch to {Endpoint}", endpoint);
            return new RuntimeDispatchResult
            {
                SessionId = request.SessionId,
                AgentInstanceId = "",
                IsSuccess = false,
                ErrorMessage = $"Runtime dispatch failed: {ex.Message}"
            };
        }
    }

    /// <summary>将消息分发到 Runtime 的 SSE 执行端点，并逐帧转发。</summary>
    public async IAsyncEnumerable<ServerSentEventFrame> DispatchStreamAsync(
        RuntimeDispatchRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var node = _registry.PickNode();
        var endpoint = node?.Endpoint ?? _fallbackEndpoint;

        _logger.LogInformation("[RuntimeDispatch] stream session={SessionId} → {Endpoint} (via {Source})",
            request.SessionId, endpoint, node is null ? "fallback" : "registry");

        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.BaseAddress = new Uri(endpoint);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/runtime/execute/stream")
        {
            Content = JsonContent.Create(request)
        };
        using var response = await httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "[RuntimeDispatch] stream failed status={Status} endpoint={Endpoint} body={Body}",
                (int)response.StatusCode, endpoint, errorBody);
            yield return ServerSentEventFrame.Json(SseEventTypes.Error, new { message = $"Runtime stream failed: {errorBody}" });
            yield break;
        }

        await foreach (var frame in ReadSseFramesAsync(response, ct))
            yield return frame;
    }

    private static async IAsyncEnumerable<ServerSentEventFrame> ReadSseFramesAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        var data = new StringBuilder();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            if (line.Length == 0)
            {
                if (eventName is not null && data.Length > 0)
                    yield return new ServerSentEventFrame(eventName, data.ToString());

                eventName = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
                eventName = line["event: ".Length..].Trim();
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
                data.Append(line["data: ".Length..]);
        }
    }
}

