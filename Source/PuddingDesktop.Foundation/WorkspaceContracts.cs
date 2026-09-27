using System.Text.Json;

namespace PuddingDesktop.Foundation;

public sealed record WorkspaceSummary(
    string WorkspaceId, string Slug, string TeamId, string TeamName, string Name,
    string Description, string UserProfile, string TeamAccessPolicy, string CompanyAccessPolicy,
    bool IsEnabled, bool IsFrozen, int MemberCount, DateTimeOffset CreatedAt)
{
    /// <summary>Freeze and disable are independent states; neither is folded into the other.</summary>
    public string StateText => !IsEnabled ? "已停用" : IsFrozen ? "已冻结" : "启用中";
    public bool IsBuiltInDefault => string.Equals(WorkspaceId, "default", StringComparison.Ordinal);
}

public sealed record WorkspaceMember(int Id, string UserId, string Username, string DisplayName, string AccessLevel)
{
    public string AccessText => WorkspaceText.DescribeAccessLevel(AccessLevel);
}

public sealed record WorkspaceTeamOption(string TeamId, string Name);
public sealed record WorkspaceUserOption(string UserId, string Username, string DisplayName);

public sealed record WorkspaceCreateRequest(
    string WorkspaceId, string TeamId, string Name, string Description, string UserProfile,
    string TeamAccessPolicy, string CompanyAccessPolicy);

public sealed record WorkspaceEdit(
    string WorkspaceId, string Name, string Description, string UserProfile,
    string TeamAccessPolicy, string CompanyAccessPolicy, bool IsEnabled);

/// <summary>
/// Task-shaped operations for the workspace tab, implemented in Composition against the shared
/// WorkspaceService. No HTTP, JWT or DTO relay.
/// </summary>
public interface IWorkspaceSettings
{
    Task<IReadOnlyList<WorkspaceSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkspaceTeamOption>> ListTeamsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkspaceUserOption>> ListUsersAsync(CancellationToken cancellationToken = default);
    Task CreateAsync(WorkspaceCreateRequest create, CancellationToken cancellationToken = default);
    Task SaveAsync(WorkspaceEdit edit, CancellationToken cancellationToken = default);
    Task DeleteAsync(string workspaceId, CancellationToken cancellationToken = default);
    Task SetFrozenAsync(string workspaceId, bool frozen, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkspaceMember>> ListMembersAsync(string workspaceId, CancellationToken cancellationToken = default);
    Task AddMemberAsync(string workspaceId, string userId, string accessLevel, CancellationToken cancellationToken = default);
    Task RemoveMemberAsync(string workspaceId, int memberId, CancellationToken cancellationToken = default);
}

public static class WorkspaceText
{
    /// <summary>Mirrors WorkspaceService.AccessLevels (the WorkspaceAccessPolicy enum).</summary>
    public static IReadOnlyList<string> AccessLevels { get; } = ["None", "ReadOnly", "Write", "Manage"];

    public const string DefaultWorkspaceNotice =
        "内置默认工作空间不可删除：Core 会拒绝该请求，桌面端也不提供入口。";

    public const string FrozenNotice =
        "冻结与停用是两个独立状态：冻结阻止执行，停用表示工作区未启用。";

    public static string DescribeAccessLevel(string? level) => level switch
    {
        null or "" => "未设置",
        var value when string.Equals(value, "None", StringComparison.OrdinalIgnoreCase) => "None（无权限）",
        var value when string.Equals(value, "ReadOnly", StringComparison.OrdinalIgnoreCase) => "ReadOnly（只读）",
        var value when string.Equals(value, "Write", StringComparison.OrdinalIgnoreCase) => "Write（可读写）",
        var value when string.Equals(value, "Manage", StringComparison.OrdinalIgnoreCase) => "Manage（可管理）",
        var value => value
    };

    public static bool IsAccessLevel(string? level) =>
        level is not null && AccessLevels.Contains(level, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Validate(WorkspaceCreateRequest create)
    {
        var errors = new List<string>(Validate(create.WorkspaceId, create.Name, create.UserProfile,
            create.TeamAccessPolicy, create.CompanyAccessPolicy));
        if (string.IsNullOrWhiteSpace(create.TeamId)) errors.Add("必须选择所属团队。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(WorkspaceEdit edit)
    {
        var errors = new List<string>(Validate(edit.WorkspaceId, edit.Name, edit.UserProfile,
            edit.TeamAccessPolicy, edit.CompanyAccessPolicy));
        if (string.Equals(edit.WorkspaceId, "default", StringComparison.Ordinal) && !edit.IsEnabled)
            errors.Add("内置默认工作空间不能被停用。");
        return errors;
    }

    /// <summary>Workspace ids become the Slug and a directory name, so they stay conservative.</summary>
    public static IReadOnlyList<string> ValidateWorkspaceId(string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return ["工作区 ID 不能为空。"];
        var value = workspaceId.Trim();
        if (value.Length > 64 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            return ["工作区 ID 只能包含字母、数字、'-' 与 '_'。"];
        return [];
    }

    private static List<string> Validate(string? workspaceId, string name, string userProfile,
        string teamAccessPolicy, string companyAccessPolicy)
    {
        var errors = new List<string>(ValidateWorkspaceId(workspaceId));
        if (string.IsNullOrWhiteSpace(name)) errors.Add("工作区名称不能为空。");
        if (!IsAccessLevel(teamAccessPolicy)) errors.Add($"团队访问策略必须是 {string.Join(" / ", AccessLevels)} 之一。");
        if (!IsAccessLevel(companyAccessPolicy)) errors.Add($"公司访问策略必须是 {string.Join(" / ", AccessLevels)} 之一。");
        // UserProfile is stored verbatim as JSON; malformed JSON would be persisted unreadable.
        if (!string.IsNullOrWhiteSpace(userProfile) && !IsJson(userProfile))
            errors.Add("用户档案必须是合法 JSON（留空表示不设置）。");
        return errors;
    }

    public static bool IsJson(string value)
    {
        try { using var _ = JsonDocument.Parse(value); return true; }
        catch (JsonException) { return false; }
    }


}
