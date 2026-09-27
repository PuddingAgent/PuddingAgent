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
