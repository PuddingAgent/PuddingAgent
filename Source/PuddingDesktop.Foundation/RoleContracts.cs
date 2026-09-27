namespace PuddingDesktop.Foundation;

/// <summary>
/// A permission role. Core stores the permission list as JSON strings and does not validate them, so the
/// page pre-checks against the vocabulary Core's own roles use.
/// </summary>
public sealed record PermissionRole(
    int Id, string RoleId, string Name, string Description, IReadOnlyList<string> Permissions,
    bool IsSystemRole, DateTimeOffset CreatedAt)
{
    public string PermissionsText => Permissions.Count == 0 ? "无权限" : string.Join(" · ", Permissions);
    public string KindText => IsSystemRole ? "系统内置" : "自定义";
    public bool IsEditable => !IsSystemRole;
    public bool HasUnknownPermission => Permissions.Any(permission => !RoleText.IsKnownPermission(permission));
}

/// <summary>
/// A role create or update. The RoleId is supplied on both paths (Core's create checks it for duplicates),
/// so create and update are separate operations rather than being inferred from a missing id.
/// </summary>
public sealed record RoleEdit(string RoleId, string Name, string Description, IReadOnlyList<string> Permissions);

public interface IRoleSettings
{
    Task<IReadOnlyList<PermissionRole>> ListAsync(CancellationToken cancellationToken = default);
    Task CreateAsync(RoleEdit create, CancellationToken cancellationToken = default);
    Task UpdateAsync(RoleEdit edit, CancellationToken cancellationToken = default);
    /// <summary>Core refuses to delete a built-in role; the page does not offer it for those.</summary>
    Task DeleteAsync(string roleId, CancellationToken cancellationToken = default);
}

public static class RoleText
{
    /// <summary>
    /// The permission strings Core actually defines: they are exactly the ones its four seeded roles use.
    /// The Web page also lists team:*/user:* permissions, but no Core authorization check references them,
    /// so they are not offered here.
    /// </summary>
    public static IReadOnlyList<string> KnownPermissions { get; } =
    [
        "workspace:read", "workspace:write", "workspace:manage",
        "agent:run", "agent:manage",
        "template:read", "template:manage",
        "llm:read", "llm:manage",
    ];

    public const string SystemRoleNotice =
        "系统内置角色不可修改也不可删除：Core 会直接拒绝（400），界面不提供保存与删除入口。";

    public const string PermissionNotice =
        "Core 只把权限列表按字符串存成 JSON，不做白名单校验；界面按 Core 自己四个内置角色用到的 9 项校验，" +
        "未知权限会被拦下——这是界面规则，不是 Core 强制。";

    public const string TeamUserPermissionGap =
        "Web 页面还列出 team:* 与 user:* 权限，但 Core 的授权检查里没有任何一处引用它们；界面不发明这些取值。";

    public static string DescribePermission(string permission) => permission switch
    {
        "workspace:read" => "workspace:read（读工作区）",
        "workspace:write" => "workspace:write（写工作区）",
        "workspace:manage" => "workspace:manage（管理工作区）",
        "agent:run" => "agent:run（运行 Agent）",
        "agent:manage" => "agent:manage（管理 Agent）",
        "template:read" => "template:read（读模板）",
        "template:manage" => "template:manage（管理模板）",
        "llm:read" => "llm:read（读 LLM 资源）",
        "llm:manage" => "llm:manage（管理 LLM 资源）",
        var value => value
    };

    public static bool IsKnownPermission(string? permission) =>
        permission is not null && KnownPermissions.Contains(permission, StringComparer.Ordinal);

    /// <summary>Role ids become a stored key, so they stay conservative.</summary>
    public static IReadOnlyList<string> ValidateRoleId(string? roleId)
    {
        if (string.IsNullOrWhiteSpace(roleId)) return ["角色 ID 不能为空。"];
        var value = roleId.Trim();
        return value.Length > 64 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            ? ["角色 ID 只能包含字母、数字、'-'、'_' 与 '.'。"]
            : [];
    }

    public static IReadOnlyList<string> Validate(RoleEdit edit, bool isCreate)
    {
        var errors = new List<string>();
        if (isCreate) errors.AddRange(ValidateRoleId(edit.RoleId));
        if (string.IsNullOrWhiteSpace(edit.Name)) errors.Add("角色名称不能为空。");
        foreach (var permission in edit.Permissions.Where(permission => !IsKnownPermission(permission)))
            errors.Add($"未知权限：{permission}（Core 没有定义该取值）。");
        return errors;
    }

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? permissions) =>
        permissions is null
            ? []
            : permissions.Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
}
