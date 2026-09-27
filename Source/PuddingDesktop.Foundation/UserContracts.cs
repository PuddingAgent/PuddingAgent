namespace PuddingDesktop.Foundation;

/// <summary>
/// An account. No password or hash is part of this model: the password is write-only from the settings page.
/// </summary>
public sealed record AppUserAccount(
    int Id, string UserId, string Username, string Email, string DisplayName, string UserType,
    bool IsEnabled, IReadOnlyList<string> RoleIds, DateTimeOffset CreatedAt, string Avatar)
{
    public string UserTypeText => UserContractsText.DescribeUserType(UserType);
    public string StateText => IsEnabled ? "已启用" : "已停用";
    public bool IsAdmin => string.Equals(UserType, "Admin", StringComparison.OrdinalIgnoreCase);
    public string RolesText => RoleIds.Count == 0 ? "未分配角色" : string.Join("、", RoleIds);
}

public sealed record UserCreate(
    string UserId, string Username, string Email, string DisplayName, string UserType,
    string Password, string ConfirmPassword);

public sealed record UserMetaEdit(
    string UserId, string Username, string Email, string DisplayName, string UserType, bool IsEnabled);

public sealed record UserPasswordChange(string UserId, string NewPassword, string ConfirmPassword);

public interface IUserSettings
{
    Task<IReadOnlyList<AppUserAccount>> ListAsync(CancellationToken cancellationToken = default);
    Task CreateAsync(UserCreate create, CancellationToken cancellationToken = default);
    Task UpdateAsync(UserMetaEdit edit, CancellationToken cancellationToken = default);
    Task ChangePasswordAsync(UserPasswordChange change, CancellationToken cancellationToken = default);
    /// <summary>Full replacement, matching Core: roles not listed are removed from the user.</summary>
    Task AssignRolesAsync(string userId, IReadOnlyList<string> roleIds, CancellationToken cancellationToken = default);
    /// <summary>Core refuses to remove the last Admin; the page shows that refusal.</summary>
    Task DeleteAsync(string userId, CancellationToken cancellationToken = default);
}

public static class UserContractsText
{
    public static IReadOnlyList<string> UserTypes { get; } = ["Admin", "SimpleUser"];

    /// <summary>Mirrors UserService.MinimumPasswordLength (Core's own floor).</summary>
    public const int MinimumPasswordLength = 6;

    public const string PasswordNotice =
        "密码只写不读：界面不回显密码，也不提供查看；新建必须填写并二次确认，改密同样要确认。";

    public const string LastAdminNotice =
        "至少保留一个 Admin 账号：删除最后一个 Admin 会被 Core 拒绝，界面也不提供绕过。";

    public const string RoleReplacementNotice =
        "角色分配是全量替换：界面上未勾选的角色会被移除；Core 对未知角色 ID 只是匹配不到，界面只提供已存在的角色。";

    public const string EmailConflictNotice =
        "邮箱必须唯一：Core 在新建与改名时都会校验，重复直接冲突（原实现改邮箱不校验，会撞唯一索引变成 500）。";

    public static string DescribeUserType(string? userType) => userType switch
    {
        null or "" => "类型未知",
        var value when string.Equals(value, "Admin", StringComparison.OrdinalIgnoreCase) => "Admin（管理员）",
        var value when string.Equals(value, "SimpleUser", StringComparison.OrdinalIgnoreCase) => "SimpleUser（普通用户）",
        var value => value
    };

    public static bool IsKnownUserType(string? userType) =>
        userType is not null && UserTypes.Contains(userType, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Validate(UserCreate create)
    {
        var errors = new List<string>();
        errors.AddRange(ValidateAccountIds(create.UserId, create.Username, create.Email, create.UserType));
        if (string.IsNullOrWhiteSpace(create.Password)) errors.Add("密码不能为空。");
        else if (create.Password.Length < MinimumPasswordLength)
            errors.Add($"密码不得少于 {MinimumPasswordLength} 位。");
        // 二次确认是界面要求（Core 只收一个密码字段），不填或不一致都要拦下。
        if (!string.Equals(create.Password, create.ConfirmPassword, StringComparison.Ordinal))
            errors.Add("两次输入的密码不一致。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(UserMetaEdit edit) =>
        ValidateAccountIds(edit.UserId, edit.Username, edit.Email, edit.UserType);

    public static IReadOnlyList<string> Validate(UserPasswordChange change)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(change.UserId)) errors.Add("缺少用户。");
        if (string.IsNullOrWhiteSpace(change.NewPassword)) errors.Add("新密码不能为空。");
        else if (change.NewPassword.Length < MinimumPasswordLength)
            errors.Add($"密码不得少于 {MinimumPasswordLength} 位。");
        if (!string.Equals(change.NewPassword, change.ConfirmPassword, StringComparison.Ordinal))
            errors.Add("两次输入的密码不一致。");
        return errors;
    }

    /// <summary>User ids become a login name and a JWT subject, so they stay conservative.</summary>
    public static IReadOnlyList<string> ValidateUserId(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return ["UserId 不能为空。"];
        var value = userId.Trim();
        return value.Length > 64 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            ? ["UserId 只能包含字母、数字、'-'、'_' 与 '.'。"]
            : [];
    }

    public static bool IsPlausibleEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email)
        && email.Contains('@', StringComparison.Ordinal)
        && email.IndexOf('@') > 0
        && email.LastIndexOf('@') < email.Length - 1;

    private static List<string> ValidateAccountIds(string userId, string username, string email, string userType)
    {
        var errors = new List<string>();
        errors.AddRange(ValidateUserId(userId));
        if (string.IsNullOrWhiteSpace(username)) errors.Add("用户名不能为空。");
        if (string.IsNullOrWhiteSpace(email)) errors.Add("邮箱不能为空。");
        else if (!IsPlausibleEmail(email)) errors.Add("邮箱格式看起来不合法。");
        if (!IsKnownUserType(userType)) errors.Add($"UserType 必须是 {string.Join(" / ", UserTypes)} 之一。");
        return errors;
    }
}
