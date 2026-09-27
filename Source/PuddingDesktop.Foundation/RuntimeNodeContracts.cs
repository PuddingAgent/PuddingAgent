namespace PuddingDesktop.Foundation;

/// <summary>One native capability a runtime node exposes.</summary>
public sealed record RuntimeNodeCapability(string CapabilityId, string Name, string Description, string Category, bool RequiresApproval)
{
    public string Display => $"{Name}（{CapabilityId}）· {Category}" + (RequiresApproval ? " · 需审批" : "");
    public string CategoryText => RuntimeNodeText.DescribeCapabilityCategory(Category);
}

/// <summary>
/// A runtime node. Core's model has no host/IP or freeze-reason field: the host is derived from the endpoint
/// and the freeze reason lives only in the audit trail, so the page shows exactly that.
/// </summary>
public sealed record RuntimeNode(
    string NodeId, string Endpoint, string Status, DateTimeOffset LastHeartbeat, int ActiveSessionCount,
    bool EmbeddedMode, string HostType, bool IsFrozen, IReadOnlyList<RuntimeNodeCapability> Capabilities)
{
    public string StatusText => RuntimeNodeText.DescribeStatus(Status);
    public string ModeText => EmbeddedMode ? "嵌入（桌面宿主）" : "独立 Runtime";
    public string HostText => RuntimeNodeText.HostOf(Endpoint);
    public string HeartbeatText => RuntimeNodeText.DescribeHeartbeat(LastHeartbeat, DateTimeOffset.UtcNow);
    public bool IsOnline => string.Equals(Status, "Online", StringComparison.OrdinalIgnoreCase);
}

public sealed record RuntimeNodeSummary(
    int Total, int Online, int Degraded, int Offline, int ActiveSessions, int Frozen, int Embedded)
{
    public static RuntimeNodeSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
    public string HeadlineText =>
        $"节点 {Total} · 在线 {Online} · 降级 {Degraded} · 离线 {Offline} · 活跃会话 {ActiveSessions}";
    public string ExtraText => $"嵌入节点 {Embedded} · 已冻结 {Frozen}";

    public static RuntimeNodeSummary Of(IReadOnlyList<RuntimeNode> nodes) => new(
        nodes.Count,
        nodes.Count(node => string.Equals(node.Status, "Online", StringComparison.OrdinalIgnoreCase)),
        nodes.Count(node => string.Equals(node.Status, "Degraded", StringComparison.OrdinalIgnoreCase)),
        nodes.Count(node => string.Equals(node.Status, "Offline", StringComparison.OrdinalIgnoreCase)),
        nodes.Sum(node => node.ActiveSessionCount),
        nodes.Count(node => node.IsFrozen),
        nodes.Count(node => node.EmbeddedMode));
}

public interface IRuntimeNodeSettings
{
    Task<IReadOnlyList<RuntimeNode>> ListNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>The reason is recorded in the audit trail only; Core's node model has no reason field.</summary>
    Task FreezeAsync(string nodeId, string reason, CancellationToken cancellationToken = default);
    Task UnfreezeAsync(string nodeId, string reason, CancellationToken cancellationToken = default);
}

public static class RuntimeNodeText
{
    public static IReadOnlyList<string> Statuses { get; } = ["Online", "Degraded", "Offline"];

    public const string ReasonNotice =
        "冻结原因只写进审计记录：Core 的节点模型没有原因字段，所以列表里只显示「已冻结」，原因要去审批/审计里查。";

    public const string HostNotice =
        "Core 的节点模型只有 Endpoint，没有单独的 host/IP 字段：主机名是从 Endpoint 推导出来的，界面不另外猜地址。";

    public const string FreezeEffectNotice =
        "冻结后该节点拒绝所有原生能力调用（不是「暂停调度」）；解冻即刻恢复，两步都会写审计。";

    public static string DescribeStatus(string? status) => status switch
    {
        null or "" => "状态未知",
        var value when string.Equals(value, "Online", StringComparison.OrdinalIgnoreCase) => "在线",
        var value when string.Equals(value, "Degraded", StringComparison.OrdinalIgnoreCase) => "降级",
        var value when string.Equals(value, "Offline", StringComparison.OrdinalIgnoreCase) => "离线",
        var value => value
    };

    public static string DescribeCapabilityCategory(string? category) => category switch
    {
        null or "" => "未分类",
        var value when string.Equals(value, "QueryState", StringComparison.OrdinalIgnoreCase) => "QueryState（只读查询）",
        var value when string.Equals(value, "RunTest", StringComparison.OrdinalIgnoreCase) => "RunTest（驱动测试）",
        var value when string.Equals(value, "Custom", StringComparison.OrdinalIgnoreCase) => "Custom（自定义）",
        var value => value
    };

    /// <summary>Derives "host:port" from the endpoint; an unparseable endpoint is shown verbatim.</summary>
    public static string HostOf(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return "未上报端点";
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Authority : endpoint;
    }

    /// <summary>Heartbeat age is reported as elapsed time; a future timestamp is called out rather than hidden.</summary>
    public static string DescribeHeartbeat(DateTimeOffset lastHeartbeat, DateTimeOffset now)
    {
        var elapsed = now - lastHeartbeat;
        if (elapsed < TimeSpan.Zero) return "心跳时间在未来（时钟可能不同步）";
        if (elapsed.TotalSeconds < 60) return $"{elapsed.TotalSeconds:0} 秒前心跳";
        if (elapsed.TotalMinutes < 60) return $"{elapsed.TotalMinutes:0} 分钟前心跳";
        if (elapsed.TotalHours < 24) return $"{elapsed.TotalHours:0.#} 小时前心跳";
        return $"{elapsed.TotalDays:0.#} 天前心跳";
    }

    /// <summary>A node that never reported a heartbeat is not presented as fresh.</summary>
    public static bool IsStale(DateTimeOffset lastHeartbeat, DateTimeOffset now, TimeSpan threshold) =>
        now - lastHeartbeat > threshold;

    public static IReadOnlyList<string> Validate(string nodeId, string reason)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(nodeId)) errors.Add("请先选择节点。");
        // Core 的审计记录会把原因写进 Detail；空原因会让这条轨迹失去意义。
        if (string.IsNullOrWhiteSpace(reason)) errors.Add("必须填写原因（会写入审计记录）。");
        return errors;
    }
}
