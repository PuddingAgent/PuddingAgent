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

/// <summary>
/// Task-shaped read model for the Skill Hub pages, implemented in Composition against ISkillHubService.
/// Writes (publish, evolve, retire, install) arrive in later slices and are deliberately absent here.
/// </summary>
public interface ISkillHubSettings
{
    Task<SkillHubOverview> ReadOverviewAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SkillHubSkillSummary>> ListSkillsAsync(
        string? query, string? tag, string? status, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SkillHubEvent>> ListEventsAsync(string? skillId, int limit, CancellationToken cancellationToken = default);
}

public static class SkillHubText
{
    public static IReadOnlyList<int> EventPageSizes { get; } = [20, 50, 100, 200];

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
