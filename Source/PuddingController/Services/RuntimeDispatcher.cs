using System.Net.Http.Json;
using PuddingCode.Platform;

namespace PuddingController.Services;

/// <summary>
/// Runtime 分发器——将执行请求发送到指定 Runtime 节点。
/// V1 仅支持单节点 Embedded Runtime（同进程或本地 HTTP）。
/// </summary>
public sealed class RuntimeDispatcher
{
    private readonly ILogger<RuntimeDispatcher> _logger;
    private string _runtimeEndpoint = "http://localhost:5100"; // 默认本地 Runtime

    public RuntimeDispatcher(ILogger<RuntimeDispatcher> logger)
    {
        _logger = logger;
    }

    /// <summary>配置 Runtime 端点。</summary>
    public void SetRuntimeEndpoint(string endpoint) => _runtimeEndpoint = endpoint;

    /// <summary>将消息分发到 Runtime 执行。</summary>
    public async Task<RuntimeDispatchResult> DispatchAsync(RuntimeDispatchRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation("[RuntimeDispatch] Dispatching session={SessionId} to {Endpoint}",
            request.SessionId, _runtimeEndpoint);

        try
        {
            using var httpClient = new HttpClient { BaseAddress = new Uri(_runtimeEndpoint) };
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
            _logger.LogError(ex, "[RuntimeDispatch] Failed to dispatch to Runtime");
            return new RuntimeDispatchResult
            {
                SessionId = request.SessionId,
                AgentInstanceId = "",
                IsSuccess = false,
                ErrorMessage = $"Runtime dispatch failed: {ex.Message}"
            };
        }
    }
}
