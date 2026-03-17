using System.Collections.Concurrent;
using PuddingCode.Platform;

namespace PuddingController.Services;

/// <summary>
/// Runtime 节点注册表——Controller 端维护的已注册 Runtime 节点列表。
/// Runtime 启动后会调用 /api/runtime-registry/register 注册自身，Controller 据此调度。
/// 支持嵌入式宿主节点：携带 EmbeddedMode=true 的节点在此处单独管理并支持冻结控制。
/// </summary>
public sealed class RuntimeRegistryService
{
    // 节点离线超时：超过该时长没有心跳则标记为 Offline
    private static readonly TimeSpan NodeTimeout = TimeSpan.FromSeconds(90);

    // key = nodeId
    private readonly ConcurrentDictionary<string, RuntimeNodeInfo> _nodes = new();
    private readonly ILogger<RuntimeRegistryService> _logger;

    public RuntimeRegistryService(ILogger<RuntimeRegistryService> logger)
    {
        _logger = logger;
    }

    /// <summary>接收 Runtime 注册/心跳请求。</summary>
    public RuntimeRegisterResponse Register(RuntimeRegisterRequest request)
    {
        // 若已有记录，保留冻结状态（冻结状态由管理员手动控制，不被心跳覆盖）
        _nodes.TryGetValue(request.NodeId, out var existing);
        var isFrozen = existing?.IsFrozen ?? false;

        var node = new RuntimeNodeInfo
        {
            NodeId = request.NodeId,
            Endpoint = request.Endpoint,
            Status = RuntimeNodeStatus.Online,
            LastHeartbeat = DateTimeOffset.UtcNow,
            ActiveSessionCount = request.ActiveSessionCount,
            EmbeddedMode = request.EmbeddedMode,
            HostType = request.HostType,
            NativeCapabilities = request.NativeCapabilities,
            IsFrozen = isFrozen,
        };

        _nodes[request.NodeId] = node;
        _logger.LogInformation(
            "[RuntimeRegistry] Registered node={NodeId} endpoint={Endpoint} activeSessions={Count} embedded={Em} hostType={Ht}",
            request.NodeId, request.Endpoint, request.ActiveSessionCount, request.EmbeddedMode, request.HostType);

        return new RuntimeRegisterResponse { Accepted = true, Message = "OK", IsFrozen = isFrozen };
    }

    /// <summary>
    /// 挑选一个可用的 Runtime 节点（最少活跃 Session 优先）。
    /// 若没有已注册的 Online 节点，返回 null（调用方应回退到默认端点或报错）。
    /// </summary>
    public RuntimeNodeInfo? PickNode()
    {
        MarkStaledOffline();
        return _nodes.Values
            .Where(n => n.Status == RuntimeNodeStatus.Online)
            .OrderBy(n => n.ActiveSessionCount)
            .FirstOrDefault();
    }

    /// <summary>获取所有节点快照（包含离线节点）。</summary>
    public IReadOnlyList<RuntimeNodeInfo> GetAll()
    {
        MarkStaledOffline();
        return _nodes.Values.ToArray();
    }

    /// <summary>获取所有嵌入式宿主节点。</summary>
    public IReadOnlyList<RuntimeNodeInfo> GetEmbeddedNodes()
    {
        MarkStaledOffline();
        return _nodes.Values.Where(n => n.EmbeddedMode).ToArray();
    }

    /// <summary>获取指定节点的原生能力列表（节点不存在返回 null）。</summary>
    public IReadOnlyList<NativeCapabilityDescriptor>? GetNodeCapabilities(string nodeId)
    {
        _nodes.TryGetValue(nodeId, out var node);
        return node?.NativeCapabilities;
    }

    /// <summary>
    /// 冻结嵌入式节点——冻结后 NativeCapabilityExecutor 将拒绝所有原生能力调用。
    /// </summary>
    public bool FreezeNode(string nodeId)
    {
        if (!_nodes.TryGetValue(nodeId, out var node)) return false;
        _nodes[nodeId] = node with { IsFrozen = true };
        _logger.LogWarning("[RuntimeRegistry] Node={NodeId} FROZEN", nodeId);
        return true;
    }

    /// <summary>解除嵌入式节点冻结。</summary>
    public bool UnfreezeNode(string nodeId)
    {
        if (!_nodes.TryGetValue(nodeId, out var node)) return false;
        _nodes[nodeId] = node with { IsFrozen = false };
        _logger.LogInformation("[RuntimeRegistry] Node={NodeId} UNFROZEN", nodeId);
        return true;
    }

    /// <summary>将长时间未心跳的节点标记为 Offline。</summary>
    private void MarkStaledOffline()
    {
        var threshold = DateTimeOffset.UtcNow - NodeTimeout;
        foreach (var kv in _nodes)
        {
            if (kv.Value.Status == RuntimeNodeStatus.Online
                && kv.Value.LastHeartbeat < threshold)
            {
                _nodes[kv.Key] = kv.Value with { Status = RuntimeNodeStatus.Offline };
                _logger.LogWarning("[RuntimeRegistry] Node={NodeId} marked Offline (last heartbeat={Hb})",
                    kv.Key, kv.Value.LastHeartbeat);
            }
        }
    }
}
