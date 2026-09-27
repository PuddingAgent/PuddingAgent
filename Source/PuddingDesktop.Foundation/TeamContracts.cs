namespace PuddingDesktop.Foundation;

public sealed record TeamSummary(
    int Id, string TeamId, string Name, string Description, bool IsEnabled,
    int MemberCount, int WorkspaceCount, DateTimeOffset CreatedAt)
{
    public string StateText => IsEnabled ? "已启用" : "已停用";
    public string CountsText => $"成员 {MemberCount} · 工作区 {WorkspaceCount}";
    public bool CanDelete => WorkspaceCount == 0;
}

public sealed record TeamMember(string UserId, string Username, string DisplayName, string Role)
{
    public string RoleText => TeamContractsText.DescribeMemberRole(Role);
}

public sealed record TeamWorkspace(
    int Id, string WorkspaceId, string Slug, string TeamId, string TeamName, string Name, string Description,
    string UserProfile, string TeamAccessPolicy, string CompanyAccessPolicy,
    bool IsEnabled, bool IsFrozen, int MemberCount, DateTimeOffset CreatedAt)
{
    public string StateText => !IsEnabled ? "已停用" : IsFrozen ? "已冻结" : "启用中";
    public string PolicyText =>
        $"团队 {TeamContractsText.DescribeAccessLevel(TeamAccessPolicy)} · 公司 {TeamContractsText.DescribeAccessLevel(CompanyAccessPolicy)}";
    public bool IsBuiltInDefault => string.Equals(WorkspaceId, "default", StringComparison.Ordinal);
}

public sealed record TeamWhitelistEntry(int Id, string UserId, string Username, string DisplayName, string AccessLevel)
{
    public string AccessText => TeamContractsText.DescribeAccessLevel(AccessLevel);
}

public sealed record TeamEdit(string TeamId, string Name, string Description, bool IsEnabled);
public sealed record TeamMemberAdd(string TeamId, string UserId, string Role);
public sealed record TeamWorkspaceEdit(
    string TeamId, string WorkspaceId, string Name, string Description, string UserProfile,
    string TeamAccessPolicy, string CompanyAccessPolicy, bool IsEnabled);
public sealed record TeamWorkspaceMemberAdd(string WorkspaceId, string UserId, string AccessLevel);

public interface ITeamSettings
{
    Task<IReadOnlyList<TeamSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task CreateAsync(TeamEdit create, CancellationToken cancellationToken = default);
    Task UpdateAsync(TeamEdit edit, CancellationToken cancellationToken = default);
    /// <summary>Core refuses while the team still owns workspaces.</summary>
    Task DeleteAsync(string teamId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TeamMember>> ListMembersAsync(string teamId, CancellationToken cancellationToken = default);
    Task AddMemberAsync(TeamMemberAdd add, CancellationToken cancellationToken = default);
    Task RemoveMemberAsync(string teamId, string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TeamWorkspace>> ListWorkspacesAsync(string teamId, CancellationToken cancellationToken = default);
    Task CreateWorkspaceAsync(TeamWorkspaceEdit create, CancellationToken cancellationToken = default);
    Task UpdateWorkspaceAsync(TeamWorkspaceEdit edit, CancellationToken cancellationToken = default);
    Task DeleteWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TeamWhitelistEntry>> ListWorkspaceMembersAsync(string workspaceId, CancellationToken cancellationToken = default);
    Task AddWorkspaceMemberAsync(TeamWorkspaceMemberAdd add, CancellationToken cancellationToken = default);
    Task RemoveWorkspaceMemberAsync(string workspaceId, int memberId, CancellationToken cancellationToken = default);
}

public static class TeamContractsText
{
    /// <summary>Mirrors TeamService: TeamMemberRole and WorkspaceAccessPolicy.</summary>
    public static IReadOnlyList<string> MemberRoles { get; } = ["Member", "Admin"];
    public static IReadOnlyList<string> AccessLevels { get; } = ["None", "ReadOnly", "Write", "Manage"];
    /// <summary>None is not a usable whitelist level; Core rejects it for whitelist entries.</summary>
    public static IReadOnlyList<string> WhitelistLevels { get; } = ["ReadOnly", "Write", "Manage"];

    public const string TeamDeleteNotice =
        "团队下还有工作区时不能删除团队：Core 会拒绝，界面在删除前也会拦一次并让你先清理工作区。";

    public const string WhitelistNotice =
        "工作区白名单的访问级别不能用 None（Core 直接拒绝）；白名单成员按行 ID 删除，跨工作区删除会被拒绝。";

    public const string DefaultWorkspaceNotice =
        "内置默认工作空间受保护：团队页的删除也走同一条保护，不会把默认工作区删掉。";

    public static string DescribeMemberRole(string? role) => role switch
    {
        null or "" => "角色未知",
        var value when string.Equals(value, "Member", StringComparison.OrdinalIgnoreCase) => "Member（成员）",
        var value when string.Equals(value, "Admin", StringComparison.OrdinalIgnoreCase) => "Admin（团队管理员）",
        var value => value
    };

    public static string DescribeAccessLevel(string? level) => level switch
    {
        null or "" => "未设置",
        var value when string.Equals(value, "None", StringComparison.OrdinalIgnoreCase) => "None（无权限）",
        var value when string.Equals(value, "ReadOnly", StringComparison.OrdinalIgnoreCase) => "ReadOnly（只读）",
        var value when string.Equals(value, "Write", StringComparison.OrdinalIgnoreCase) => "Write（可读写）",
        var value when string.Equals(value, "Manage", StringComparison.OrdinalIgnoreCase) => "Manage（可管理）",
        var value => value
    };

    public static bool IsKnownMemberRole(string? role) =>
        role is not null && MemberRoles.Contains(role, StringComparer.OrdinalIgnoreCase);
    public static bool IsKnownAccessLevel(string? level) =>
        level is not null && AccessLevels.Contains(level, StringComparer.OrdinalIgnoreCase);
    public static bool IsUsableWhitelistLevel(string? level) =>
        level is not null && WhitelistLevels.Contains(level, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> ValidateTeamId(string? teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return ["TeamId 不能为空。"];
        var value = teamId.Trim();
        return value.Length > 64 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            ? ["TeamId 只能包含字母、数字、'-'、'_' 与 '.'。"]
            : [];
    }

    public static IReadOnlyList<string> Validate(TeamEdit edit, bool isCreate)
    {
        var errors = new List<string>();
        if (isCreate) errors.AddRange(ValidateTeamId(edit.TeamId));
        if (string.IsNullOrWhiteSpace(edit.Name)) errors.Add("团队名称不能为空。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(TeamMemberAdd add)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(add.TeamId)) errors.Add("缺少团队。");
        if (string.IsNullOrWhiteSpace(add.UserId)) errors.Add("必须选择用户。");
        if (!IsKnownMemberRole(add.Role)) errors.Add($"团队角色必须是 {string.Join(" / ", MemberRoles)} 之一。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(TeamWorkspaceEdit edit, bool isCreate)
    {
        var errors = new List<string>();
        if (isCreate) errors.AddRange(ValidateTeamId(edit.WorkspaceId));
        if (string.IsNullOrWhiteSpace(edit.Name)) errors.Add("工作区名称不能为空。");
        if (!IsKnownAccessLevel(edit.TeamAccessPolicy)) errors.Add($"团队访问策略必须是 {string.Join(" / ", AccessLevels)} 之一。");
        if (!IsKnownAccessLevel(edit.CompanyAccessPolicy)) errors.Add($"公司访问策略必须是 {string.Join(" / ", AccessLevels)} 之一。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(TeamWorkspaceMemberAdd add)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(add.WorkspaceId)) errors.Add("缺少工作区。");
        if (string.IsNullOrWhiteSpace(add.UserId)) errors.Add("必须选择用户。");
        // None 是合法的枚举值但不是可用的白名单级别，界面按白名单词表校验。
        if (!IsUsableWhitelistLevel(add.AccessLevel))
            errors.Add($"白名单访问级别必须是 {string.Join(" / ", WhitelistLevels)} 之一（不能用 None）。");
        return errors;
    }
}
