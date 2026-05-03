using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Platform;
using StackExchange.Redis;

namespace PuddingController.Services;

/// <summary>
/// Runtime 节点注册表——Controller 端维护的已注册 Runtime 节点列表。
/// 数据存储于 Redis Hash ctrl:runtimenodes（nodeId → JSON），支持多实例共享。
/// Runtime 启动后会调用 /api/runtime-registry/register 注册自身，Controller 据此调度。
/// 支持嵌入式宿主节点：携带 EmbeddedMode=true 的节点在此处单独管理并支持冻结控制。
/// </summary>
public sealed class RuntimeRegistryService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly TimeSpan NodeTimeout = TimeSpan.FromSeconds(90);
    private const string NodesKey = "ctrl:runtimenodes";

    private readonly IDatabase? _redis;
    private readonly ILogger<RuntimeRegistryService> _logger;
    private static readonly ConcurrentDictionary<string, RuntimeNodeInfo> Nodes = new();

    public RuntimeRegistryService(ILogger<RuntimeRegistryService> logger, IConnectionMultiplexer? redis = null)
    {
        _redis = redis?.GetDatabase();
        _logger = logger;
    }

    /// <summary>接收 Runtime 注册/心跳请求。</summary>
    public RuntimeRegisterResponse Register(RuntimeRegisterRequest request)
    {
        // 保留现有冻结状态（不被心跳覆盖）
        var existing = GetNode(request.NodeId);
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

        if (_redis is not null)
            _redis.HashSet(NodesKey, request.NodeId, JsonSerializer.Serialize(node, JsonOpts));
        else
            Nodes[request.NodeId] = node;

        _logger.LogInformation(
            "[RuntimeRegistry] Registered node={NodeId} endpoint={Endpoint} activeSessions={Count} embedded={Em} hostType={Ht}",
            request.NodeId, request.Endpoint, request.ActiveSessionCount, request.EmbeddedMode, request.HostType);

        return new RuntimeRegisterResponse { Accepted = true, Message = "OK", IsFrozen = isFrozen };
    }

    /// <summary>挑选一个可用的 Runtime 节点（最少活跃 Session 优先）。</summary>
    public RuntimeNodeInfo? PickNode()
    {
        MarkStaledOffline();
        return LoadAll()
            .Where(n => n.Status == RuntimeNodeStatus.Online)
            .OrderBy(n => n.ActiveSessionCount)
            .FirstOrDefault();
    }

    /// <summary>获取所有节点快照（包含离线节点）。</summary>
    public IReadOnlyList<RuntimeNodeInfo> GetAll()
    {
        MarkStaledOffline();
        return LoadAll().ToArray();
    }

    /// <summary>获取所有嵌入式宿主节点。</summary>
    public IReadOnlyList<RuntimeNodeInfo> GetEmbeddedNodes()
    {
        MarkStaledOffline();
        return LoadAll().Where(n => n.EmbeddedMode).ToArray();
    }

    /// <summary>获取指定节点的原生能力列表（节点不存在返回 null）。</summary>
    public IReadOnlyList<NativeCapabilityDescriptor>? GetNodeCapabilities(string nodeId)
        => GetNode(nodeId)?.NativeCapabilities;

    /// <summary>冻结嵌入式节点——冻结后 NativeCapabilityExecutor 将拒绝所有原生能力调用。</summary>
    public bool FreezeNode(string nodeId)
    {
        var node = GetNode(nodeId);
        if (node is null) return false;

        if (_redis is not null)
            _redis.HashSet(NodesKey, nodeId, JsonSerializer.Serialize(node with { IsFrozen = true }, JsonOpts));
        else
            Nodes[nodeId] = node with { IsFrozen = true };

        _logger.LogWarning("[RuntimeRegistry] Node={NodeId} FROZEN", nodeId);
        return true;
    }

    /// <summary>解除嵌入式节点冻结。</summary>
    public bool UnfreezeNode(string nodeId)
    {
        var node = GetNode(nodeId);
        if (node is null) return false;

        if (_redis is not null)
            _redis.HashSet(NodesKey, nodeId, JsonSerializer.Serialize(node with { IsFrozen = false }, JsonOpts));
        else
            Nodes[nodeId] = node with { IsFrozen = false };

        _logger.LogInformation("[RuntimeRegistry] Node={NodeId} UNFROZEN", nodeId);
        return true;
    }

    /// <summary>将长时间未心跳的节点标记为 Offline。</summary>
    private void MarkStaledOffline()
    {
        var threshold = DateTimeOffset.UtcNow - NodeTimeout;

        if (_redis is not null)
        {
            var entries = _redis.HashGetAll(NodesKey);
            foreach (var entry in entries)
            {
                var node = Deserialize(entry.Value);
                if (node is null) continue;
                if (node.Status == RuntimeNodeStatus.Online && node.LastHeartbeat < threshold)
                {
                    var offline = node with { Status = RuntimeNodeStatus.Offline };
                    _redis.HashSet(NodesKey, entry.Name, JsonSerializer.Serialize(offline, JsonOpts));
                    _logger.LogWarning("[RuntimeRegistry] Node={NodeId} marked Offline (last heartbeat={Hb})",
                        node.NodeId, node.LastHeartbeat);
                }
            }
            return;
        }

        foreach (var kv in Nodes)
        {
            var node = kv.Value;
            if (node.Status == RuntimeNodeStatus.Online && node.LastHeartbeat < threshold)
            {
                Nodes[kv.Key] = node with { Status = RuntimeNodeStatus.Offline };
                _logger.LogWarning("[RuntimeRegistry] Node={NodeId} marked Offline (last heartbeat={Hb})",
                    node.NodeId, node.LastHeartbeat);
            }
        }
    }

    private RuntimeNodeInfo? GetNode(string nodeId)
    {
        if (_redis is not null)
        {
            var json = _redis.HashGet(NodesKey, nodeId);
            return Deserialize(json);
        }

        return Nodes.TryGetValue(nodeId, out var node) ? node : null;
    }

    private IEnumerable<RuntimeNodeInfo> LoadAll()
    {
        if (_redis is not null)
        {
            var entries = _redis.HashGetAll(NodesKey);
            foreach (var e in entries)
            {
                var n = Deserialize(e.Value);
                if (n is not null) yield return n;
            }
            yield break;
        }

        foreach (var n in Nodes.Values)
            yield return n;
    }

    private RuntimeNodeInfo? Deserialize(RedisValue v)
        => v.IsNullOrEmpty ? null : JsonSerializer.Deserialize<RuntimeNodeInfo>((string)v!, JsonOpts);
}
