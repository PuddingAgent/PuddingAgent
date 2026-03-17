using System.Net.Http.Json;
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
    private readonly ILogger<RuntimeDispatcher> _logger;
    private string _fallbackEndpoint = "http://localhost:5100";

    public RuntimeDispatcher(
        RuntimeRegistryService registry,
        ILogger<RuntimeDispatcher> logger)
    {
        _registry = registry;
        _logger = logger;
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
            using var httpClient = new HttpClient { BaseAddress = new Uri(endpoint) };
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
}

