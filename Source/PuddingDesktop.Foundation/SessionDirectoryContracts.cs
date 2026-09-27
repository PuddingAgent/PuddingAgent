namespace PuddingDesktop.Foundation;

/// <summary>
/// One session from Core's session repository. Every field comes from SessionRecord; nothing is derived
/// beyond presentation text.
/// </summary>
public sealed record SessionDirectoryEntry(
    string SessionId, string WorkspaceId, string AgentTemplateId, string ChannelId, string OwnerUserId,
    string SessionType, string SessionRole, string Status, string Title,
    string RuntimeNodeId, string AgentInstanceId, string ParentSessionId, string RootSessionId,
    string PrincipalKind, string PrincipalId, DateTimeOffset CreatedAt, DateTimeOffset LastActiveAt)
{
    public string DisplayTitle => Title.Length > 0 ? Title : "（未命名会话）";
    public string StatusText => SessionDirectoryText.DescribeStatus(Status);
    public string RoleText => SessionDirectoryText.DescribeRole(SessionRole);
    public string TypeText => SessionDirectoryText.DescribeType(SessionType);
    public string PrincipalText => PrincipalKind.Length == 0
        ? $"Owner {OwnerUserId}"
        : $"{PrincipalKind}:{PrincipalId} · Owner {OwnerUserId}";
    public string LineageText
    {
        get
        {
            var parts = new List<string>();
            if (ParentSessionId.Length > 0) parts.Add($"父 {ParentSessionId}");
            if (RootSessionId.Length > 0 && RootSessionId != SessionId) parts.Add($"根 {RootSessionId}");
            if (AgentInstanceId.Length > 0) parts.Add($"Agent 实例 {AgentInstanceId}");
            if (RuntimeNodeId.Length > 0) parts.Add($"节点 {RuntimeNodeId}");
            return parts.Count == 0 ? "没有父/根/实例关联" : string.Join(" · ", parts);
        }
    }
    public string ActiveText => RuntimeNodeText.DescribeHeartbeat(LastActiveAt, DateTimeOffset.UtcNow);
}

public sealed record SessionFilter(
    string WorkspaceId, string ChannelId, string UserId, string AgentTemplateId, string Status, string Role,
    string Text, int Page, int PageSize)
{
    public static SessionFilter Default { get; } = new("", "", "", "", "", "", "", 1, 50);
    public string DescribeText
    {
        get
        {
            var parts = new List<string>();
            if (WorkspaceId.Length > 0) parts.Add($"工作区={WorkspaceId}");
            if (ChannelId.Length > 0) parts.Add($"渠道={ChannelId}");
            if (UserId.Length > 0) parts.Add($"用户={UserId}");
            if (AgentTemplateId.Length > 0) parts.Add($"模板={AgentTemplateId}");
            if (Status.Length > 0) parts.Add($"状态={Status}");
            if (Role.Length > 0) parts.Add($"角色={Role}");
            if (Text.Length > 0) parts.Add($"关键字={Text}");
            return parts.Count == 0 ? "未设置筛选（Core 返回的全部会话）" : string.Join(" · ", parts);
        }
    }
}

public sealed record SessionDirectoryPage(
    IReadOnlyList<SessionDirectoryEntry> Items, int Page, int PageSize, int Total, int FrozenExcluded)
{
    public static SessionDirectoryPage Empty { get; } = new([], 1, 50, 0, 0);
    public int PageCount => PageSize <= 0 ? 0 : (Total + PageSize - 1) / PageSize;
    public bool CanGoBack => Page > 1;
    public bool CanGoForward => Page < PageCount;
    public string PageText => Total == 0
        ? "没有匹配的会话"
        : $"第 {Page}/{PageCount} 页 · 共 {Total} 个会话 · 每页 {PageSize}" +
          (FrozenExcluded > 0 ? $" · 已排除 {FrozenExcluded} 个 Frozen 会话" : "");
}

public interface ISessionDirectorySettings
{
    Task<SessionDirectoryPage> ListAsync(SessionFilter filter, CancellationToken cancellationToken = default);
}

public static class SessionDirectoryText
{
    public static IReadOnlyList<string> Statuses { get; } = ["Active", "Idle", "Completed", "Failed"];
    public static IReadOnlyList<string> Roles { get; } = ["Main", "Task", "Branch", "Audit"];
    public static IReadOnlyList<string> Types { get; } = ["ServiceSession", "TaskSession", "AuditSession"];

    /// <summary>Frozen is excluded, matching the HTTP list endpoint.</summary>
    public const string FrozenNotice =
        "Frozen 会话不在这里显示：Core 的 /api/sessions 列表同样把它过滤掉，界面沿用同一口径（Frozen 用于已被后继会话接管的记录）。";

    public const string ClientSideNotice =
        "Core 的会话仓库只支持按渠道/用户/工作区查询，没有分页、状态或模板筛选；本页的状态/角色/模板/关键字筛选与分页是**在已返回集合上做的**，不是 Core 侧的过滤条件。";

    public const string SourceNotice =
        "数据来自进程内 ISessionRepository（与 Core 自己的会话主线服务同一个单例），不是第二份会话存储。";

    public static string DescribeStatus(string? status) => status switch
    {
        null or "" => "状态未知",
        var value when string.Equals(value, "Active", StringComparison.OrdinalIgnoreCase) => "活跃",
        var value when string.Equals(value, "Idle", StringComparison.OrdinalIgnoreCase) => "空闲",
        var value when string.Equals(value, "Completed", StringComparison.OrdinalIgnoreCase) => "已完成",
        var value when string.Equals(value, "Failed", StringComparison.OrdinalIgnoreCase) => "失败",
        var value when string.Equals(value, "Frozen", StringComparison.OrdinalIgnoreCase) => "已冻结",
        var value => value
    };

    public static string DescribeRole(string? role) => role switch
    {
        null or "" => "角色未知",
        var value when string.Equals(value, "Main", StringComparison.OrdinalIgnoreCase) => "Main（主线）",
        var value when string.Equals(value, "Task", StringComparison.OrdinalIgnoreCase) => "Task（任务）",
        var value when string.Equals(value, "Branch", StringComparison.OrdinalIgnoreCase) => "Branch（分支）",
        var value when string.Equals(value, "Audit", StringComparison.OrdinalIgnoreCase) => "Audit（审计）",
        var value => value
    };

    public static string DescribeType(string? type) => type switch
    {
        null or "" => "类型未知",
        var value when string.Equals(value, "ServiceSession", StringComparison.OrdinalIgnoreCase) => "ServiceSession（服务会话）",
        var value when string.Equals(value, "TaskSession", StringComparison.OrdinalIgnoreCase) => "TaskSession（任务会话）",
        var value when string.Equals(value, "AuditSession", StringComparison.OrdinalIgnoreCase) => "AuditSession（审计会话）",
        var value => value
    };

    /// <summary>Core's own clamp for admin tables; the repository itself does not page.</summary>
    public static int ClampPageSize(int pageSize) => Math.Clamp(pageSize, 1, 500);

    public static SessionFilter Normalize(SessionFilter filter) => filter with
    {
        WorkspaceId = filter.WorkspaceId.Trim(),
        ChannelId = filter.ChannelId.Trim(),
        UserId = filter.UserId.Trim(),
        AgentTemplateId = filter.AgentTemplateId.Trim(),
        Status = filter.Status.Trim(),
        Role = filter.Role.Trim(),
        Text = filter.Text.Trim(),
        Page = Math.Max(1, filter.Page),
        PageSize = ClampPageSize(filter.PageSize),
    };

    public static IReadOnlyList<string> Validate(SessionFilter filter)
    {
        var errors = new List<string>();
        if (filter.Page < 1) errors.Add("页码至少为 1。");
        if (filter.PageSize is < 1 or > 500) errors.Add("每页条数必须在 1–500 之间。");
        // Frozen 不是这一页支持的筛选值（列表沿用 Core 的排除口径）。
        if (filter.Status.Length > 0 && !Statuses.Contains(filter.Status, StringComparer.OrdinalIgnoreCase))
            errors.Add($"状态取值不在本页支持的范围内：{filter.Status}（Frozen 会话一律不列出）。");
        if (filter.Role.Length > 0 && !Roles.Contains(filter.Role, StringComparer.OrdinalIgnoreCase))
            errors.Add($"会话角色不在 Core 的枚举里：{filter.Role}。");
        return errors;
    }

    /// <summary>
    /// Applies the page-level filters and paging over the set Core returned. Kept pure so the behaviour is
    /// testable without a host, and stated in the UI as page-side rather than Core-side.
    /// </summary>
    public static SessionDirectoryPage Apply(IReadOnlyList<SessionDirectoryEntry> all, SessionFilter filter)
    {
        var normalized = Normalize(filter);
        var frozen = all.Count(session => string.Equals(session.Status, "Frozen", StringComparison.OrdinalIgnoreCase));
        var visible = all.Where(session => !string.Equals(session.Status, "Frozen", StringComparison.OrdinalIgnoreCase));

        if (normalized.Status.Length > 0)
            visible = visible.Where(session => string.Equals(session.Status, normalized.Status, StringComparison.OrdinalIgnoreCase));
        if (normalized.Role.Length > 0)
            visible = visible.Where(session => string.Equals(session.SessionRole, normalized.Role, StringComparison.OrdinalIgnoreCase));
        if (normalized.AgentTemplateId.Length > 0)
            visible = visible.Where(session => string.Equals(session.AgentTemplateId, normalized.AgentTemplateId, StringComparison.OrdinalIgnoreCase));
        if (normalized.Text.Length > 0)
            visible = visible.Where(session =>
                session.DisplayTitle.Contains(normalized.Text, StringComparison.OrdinalIgnoreCase)
                || session.SessionId.Contains(normalized.Text, StringComparison.OrdinalIgnoreCase)
                || session.OwnerUserId.Contains(normalized.Text, StringComparison.OrdinalIgnoreCase));

        var ordered = visible.OrderByDescending(session => session.LastActiveAt).ThenBy(session => session.SessionId, StringComparer.Ordinal).ToList();
        var total = ordered.Count;
        var pageSize = normalized.PageSize;
        var pageCount = pageSize <= 0 ? 0 : Math.Max(1, (total + pageSize - 1) / pageSize);
        var page = Math.Clamp(normalized.Page, 1, pageCount == 0 ? 1 : pageCount);
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        return new SessionDirectoryPage(items, page, pageSize, total, frozen);
    }
}
