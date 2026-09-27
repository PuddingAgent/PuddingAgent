namespace PuddingDesktop.Foundation;

public sealed record SkillActionCount(string Action, int Count);

public sealed record SkillTopEntry(
    string SkillId, string Name, string LatestVersion, string Status, int InstallCount, int VersionCount);

public sealed record SkillHubOverview(
    int TotalSkills,
    int ActiveSkills,
    int RetiredSkills,
    int TotalVersions,
    int TotalInstalls,
    int DistinctAgents,
    int EvolvedSkills,
    IReadOnlyList<SkillActionCount> EvolutionActionCounts,
    IReadOnlyList<SkillTopEntry> TopInstalled,
    DateTimeOffset GeneratedAt)
{
    public static SkillHubOverview Empty { get; } =
        new(0, 0, 0, 0, 0, 0, 0, [], [], DateTimeOffset.MinValue);
}

public sealed record SkillHubSkillSummary(
    string SkillId, string Name, string Summary, string Description,
    IReadOnlyList<string> Tags, IReadOnlyList<string> Keywords,
    string LatestVersion, string Status, string Visibility,
    int VersionCount, int InstallCount, int PublishCount,
    string ContentHash, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record SkillHubEvent(
    long Id, string SkillId, string Version, string EventType,
    string ActorKind, string ActorId, string WorkspaceId, string PayloadJson, DateTimeOffset CreatedAt);

public sealed record SkillHubVersion(
    string SkillId, string Version, string ContentHash, string EvolutionAction, string ParentVersion,
    IReadOnlyList<string> RelatedSkillIds, string PublishedByAgentId, string PublishedByWorkspaceId,
    string PublishNote, int ContentBytes, DateTimeOffset CreatedAt);

public sealed record SkillHubVersionContent(
    string SkillId, string Version, string ContentHash, string EvolutionAction, string ParentVersion,
    IReadOnlyList<string> RelatedSkillIds, string PublishedByAgentId, string PublishedByWorkspaceId,
    string PublishNote, int ContentBytes, DateTimeOffset CreatedAt, string SkillMarkdown);

public sealed record SkillHubInstall(
    string SkillId, string AgentInstanceId, string WorkspaceId, string InstalledVersion,
    string ContentHash, string InstalledBy, DateTimeOffset InstalledAt, DateTimeOffset UpdatedAt);

public sealed record SkillHubDetail(
    SkillHubSkillSummary Skill,
    IReadOnlyList<SkillHubVersion> Versions,
    IReadOnlyList<SkillHubInstall> RecentInstalls);

public sealed record SkillHubMetaEdit(
    string Name, string Summary, string Description,
    IReadOnlyList<string> Tags, IReadOnlyList<string> Keywords, string Status, string Visibility);

public sealed record SkillHubVersionPublish(
    string SkillId, string Name, string Version, string SkillMarkdown,
    string EvolutionAction, string ParentVersion, string PublishNote,
    IReadOnlyList<string> Tags, string Visibility);

/// <summary>
/// An installation ledger entry. Registering one records that an agent reports a version; it is not
/// proof that a package was installed or is running, and the UI must not present it as such.
/// </summary>
public sealed record SkillHubInstallRegistration(
    string SkillId, string AgentInstanceId, string WorkspaceId,
    string InstalledVersion, string ContentHash, string InstalledBy);

public sealed record EvoMapNode(
    string NodeId, string SkillId, string Version, string EvolutionAction, string ParentNodeId,
    string Name, string Status, string PublishedByAgentId, DateTimeOffset CreatedAt,
    int ContentBytes, int InstallCount);

public sealed record EvoMapEdge(string FromNodeId, string ToNodeId, string Action);

public sealed record SkillHubEvoMap(
    IReadOnlyList<EvoMapNode> Nodes, IReadOnlyList<EvoMapEdge> Edges, DateTimeOffset GeneratedAt);

public sealed record SkillHubUpdate(
    string SkillId, string Name, string InstalledVersion, string LatestVersion,
    string LatestEvolutionAction, DateTimeOffset LatestPublishedAt, string PublishNote)
{
    /// <summary>Reported as behind only when the strings differ; the page never guesses a semantic order.</summary>
    public bool IsBehind => !string.Equals(InstalledVersion, LatestVersion, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A ledger row is what an Agent reported; it is not proof of installation or execution.</summary>
public sealed record SkillHubInstallQuery(string AgentInstanceId, string SkillId, int Page, int PageSize);

/// <summary>
/// Task-shaped operations for the Skill Hub pages, implemented in Composition against ISkillHubService.
/// </summary>
public interface ISkillHubSettings
{
    Task<SkillHubOverview> ReadOverviewAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SkillHubSkillSummary>> ListSkillsAsync(
        string? query, string? tag, string? status, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SkillHubEvent>> ListEventsAsync(string? skillId, int limit, CancellationToken cancellationToken = default);

    Task<SkillHubDetail?> ReadSkillAsync(string skillId, CancellationToken cancellationToken = default);
    Task<SkillHubVersionContent?> ReadVersionAsync(string skillId, string version, CancellationToken cancellationToken = default);
    Task SaveSkillMetaAsync(string skillId, SkillHubMetaEdit edit, CancellationToken cancellationToken = default);
    /// <summary>Soft retirement: the skill is marked retired and an audit event is written. Not a delete.</summary>
    Task RetireSkillAsync(string skillId, CancellationToken cancellationToken = default);
    Task PublishVersionAsync(SkillHubVersionPublish publish, CancellationToken cancellationToken = default);
    Task RegisterInstallAsync(SkillHubInstallRegistration registration, CancellationToken cancellationToken = default);

    Task<SkillHubEvoMap?> ReadLineageAsync(string skillId, CancellationToken cancellationToken = default);
    Task<SkillHubEvoMap> ReadGlobalLineageAsync(IReadOnlyList<string>? skillIds, int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SkillHubInstall>> ListInstallsAsync(
        string? agentInstanceId, string? skillId, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SkillHubUpdate>> ListUpdatesAsync(string agentInstanceId, CancellationToken cancellationToken = default);
}

public static class SkillHubText
{
    public static IReadOnlyList<int> EventPageSizes { get; } = [20, 50, 100, 200];

    /// <summary>Core's own whitelist (SkillHubService.AllowedEvolutionActions); the form offers exactly these.</summary>
    public static IReadOnlyList<string> EvolutionActions { get; } =
        ["create", "patch", "split", "compress", "retire", "merge", "fork"];

    /// <summary>Core's own status vocabulary (SkillHubService.AllowedStatuses).</summary>
    public static IReadOnlyList<string> Statuses { get; } = ["active", "deprecated", "retired"];

    public static IReadOnlyList<string> Visibilities { get; } = ["global", "workspace"];

    /// <summary>Mirrors Core's SkillIdPattern; a dotted id is rejected by Core, so the form rejects it first.</summary>
    public static IReadOnlyList<string> ValidateSkillId(string? skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId)) return ["技能 ID 不能为空。"];
        var value = skillId.Trim();
        if (value.Length > 128 || !System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z0-9][a-z0-9\\-]{1,127}$"))
            return ["技能 ID 只能包含小写字母、数字和 '-'，且必须以字母或数字开头（例如 pudding-code-search）。"];
        return [];
    }

    public static IReadOnlyList<string> Validate(SkillHubMetaEdit edit)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.Name)) errors.Add("技能名称不能为空。");
        if (!Statuses.Contains(edit.Status, StringComparer.Ordinal)) errors.Add($"状态必须是 {string.Join(" / ", Statuses)} 之一。");
        if (!Visibilities.Contains(edit.Visibility, StringComparer.Ordinal)) errors.Add($"可见性必须是 {string.Join(" / ", Visibilities)} 之一。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(SkillHubVersionPublish publish)
    {
        var errors = new List<string>(ValidateSkillId(publish.SkillId));
        if (string.IsNullOrWhiteSpace(publish.Name)) errors.Add("技能名称不能为空。");
        if (string.IsNullOrWhiteSpace(publish.Version)) errors.Add("版本号不能为空。");
        if (string.IsNullOrWhiteSpace(publish.SkillMarkdown)) errors.Add("Skill Markdown 不能为空。");
        if (!EvolutionActions.Contains(publish.EvolutionAction, StringComparer.Ordinal))
            errors.Add($"进化动作必须是 {string.Join(" / ", EvolutionActions)} 之一。");
        if (!string.IsNullOrWhiteSpace(publish.EvolutionAction)
            && !string.Equals(publish.EvolutionAction, "create", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(publish.ParentVersion))
            errors.Add("非 create 的进化动作必须指定父版本。");
        if (!Visibilities.Contains(publish.Visibility, StringComparer.Ordinal)) errors.Add($"可见性必须是 {string.Join(" / ", Visibilities)} 之一。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(SkillHubInstallRegistration registration)
    {
        var errors = new List<string>(ValidateSkillId(registration.SkillId));
        if (string.IsNullOrWhiteSpace(registration.AgentInstanceId)) errors.Add("缺少 Agent 实例 ID。");
        if (string.IsNullOrWhiteSpace(registration.InstalledVersion)) errors.Add("缺少已安装版本。");
        return errors;
    }

    /// <summary>An install ledger row is a report, never a claim that the skill is running.</summary>
    public const string InstallLedgerNotice =
        "安装台账只记录 Agent 上报的版本，不代表技能已安装、已加载或正在运行。";

    public static IReadOnlyList<int> InstallPageSizes { get; } = [20, 50, 100, 200];

    /// <summary>
    /// Renders the lineage as an indented version tree. Cycles stop expanding instead of looping, and a
    /// node whose parent is missing from the result set is shown as a root with a note.
    /// </summary>
    public static IReadOnlyList<string> RenderLineage(SkillHubEvoMap map)
    {
        var byId = new Dictionary<string, EvoMapNode>(StringComparer.Ordinal);
        foreach (var node in map.Nodes) byId[node.NodeId] = node;
        var children = map.Nodes
            .Where(node => node.ParentNodeId.Length > 0 && byId.ContainsKey(node.ParentNodeId))
            .GroupBy(node => node.ParentNodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(node => node.Version, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var roots = map.Nodes
            .Where(node => node.ParentNodeId.Length == 0 || !byId.ContainsKey(node.ParentNodeId))
            .OrderBy(node => node.Version, StringComparer.Ordinal)
            .ToArray();

        var lines = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Walk(EvoMapNode node, int depth, bool fromFallback)
        {
            var indent = new string(' ', depth * 2);
            if (!visited.Add(node.NodeId))
            {
                lines.Add($"{indent}↺ {node.Version} · {node.EvolutionAction}（谱系存在环，已停止展开）");
                return;
            }
            var notes = new List<string>();
            if (depth == 0 && node.ParentNodeId.Length > 0 && !byId.ContainsKey(node.ParentNodeId))
                notes.Add("父节点不在结果集中");
            if (fromFallback) notes.Add("无根组件：该节点没有任何已发布的根版本");
            var note = notes.Count == 0 ? "" : "（" + string.Join("；", notes) + "）";
            lines.Add($"{indent}{node.Version} · {node.EvolutionAction} · {DescribeStatus(node.Status)}{note}" +
                      $" · {node.ContentBytes} 字节 · 安装 {node.InstallCount}");
            if (children.TryGetValue(node.NodeId, out var kids))
                foreach (var kid in kids) Walk(kid, depth + 1, fromFallback: false);
        }
        foreach (var root in roots) Walk(root, 0, fromFallback: false);
        // A component with no reachable root (a pure cycle) must still be shown rather than silently dropped.
        foreach (var node in map.Nodes.OrderBy(node => node.Version, StringComparer.Ordinal))
            if (!visited.Contains(node.NodeId)) Walk(node, 0, fromFallback: true);
        return lines;
    }

    /// <summary>Counts what the page must not silently hide: dangling edges and nodes outside the result set.</summary>
    public static string DescribeLineage(SkillHubEvoMap map)
    {
        var ids = new HashSet<string>(map.Nodes.Select(node => node.NodeId), StringComparer.Ordinal);
        var dangling = map.Edges.Count(edge => !ids.Contains(edge.FromNodeId) || !ids.Contains(edge.ToNodeId));
        var orphans = map.Nodes.Count(node => node.ParentNodeId.Length > 0 && !ids.Contains(node.ParentNodeId));
        return $"节点 {map.Nodes.Count} · 边 {map.Edges.Count} · 根 {map.Nodes.Count(node => node.ParentNodeId.Length == 0)}" +
               (orphans == 0 ? "" : $" · 父节点缺失 {orphans}") +
               (dangling == 0 ? "" : $" · 悬空边 {dangling}");
    }

    public static string DescribeUpdate(SkillHubUpdate update) =>
        $"{update.Name}（{update.SkillId}）· 已登记 {update.InstalledVersion} → 最新 {update.LatestVersion}" +
        $" · 动作 {update.LatestEvolutionAction}" +
        (string.IsNullOrWhiteSpace(update.PublishNote) ? "" : $" · {update.PublishNote}");

    public static string DescribeStatus(string? status) => status switch
    {
        null or "" => "状态未知",
        var s when string.Equals(s, "active", StringComparison.OrdinalIgnoreCase) => "启用中",
        var s when string.Equals(s, "retired", StringComparison.OrdinalIgnoreCase) => "已退役",
        var s when string.Equals(s, "draft", StringComparison.OrdinalIgnoreCase) => "草稿",
        var s => s
    };

    /// <summary>Event types come from Core's audit vocabulary; an unknown type is shown verbatim, not guessed at.</summary>
    public static string DescribeEventType(string? eventType) => eventType switch
    {
        null or "" => "未知事件",
        var s when string.Equals(s, "skill.published", StringComparison.OrdinalIgnoreCase) => "技能发布",
        var s when string.Equals(s, "skill.version_published", StringComparison.OrdinalIgnoreCase) => "版本发布",
        var s when string.Equals(s, "skill.meta_updated", StringComparison.OrdinalIgnoreCase) => "元数据更新",
        var s when string.Equals(s, "skill.retired", StringComparison.OrdinalIgnoreCase) => "技能退役",
        var s when string.Equals(s, "skill.installed", StringComparison.OrdinalIgnoreCase) => "安装登记",
        var s => s
    };

    public static string DescribeActor(string? actorKind, string? actorId)
    {
        var kind = actorKind switch
        {
            null or "" => "未知来源",
            var s when string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase) => "Agent",
            var s when string.Equals(s, "user", StringComparison.OrdinalIgnoreCase) => "用户",
            var s when string.Equals(s, "system", StringComparison.OrdinalIgnoreCase) => "系统",
            var s => s
        };
        return string.IsNullOrWhiteSpace(actorId) ? kind : $"{kind} · {actorId}";
    }

    public static string DescribeSkillReference(string? skillId, string? version) =>
        string.IsNullOrWhiteSpace(skillId)
            ? "（无技能）"
            : string.IsNullOrWhiteSpace(version) ? skillId : $"{skillId}@{version}";

    /// <summary>A retired skill is never presented as usable, whatever else the row says.</summary>
    public static bool IsUsable(string? status) => !string.Equals(status, "retired", StringComparison.OrdinalIgnoreCase);

    public static bool MatchesEvent(SkillHubEvent entry, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var term = query.Trim();
        return Contains(entry.SkillId, term) || Contains(entry.Version, term) || Contains(entry.EventType, term)
            || Contains(entry.ActorId, term) || Contains(entry.WorkspaceId, term) || Contains(entry.PayloadJson, term);
    }

    private static bool Contains(string? value, string term) =>
        value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase);
}
