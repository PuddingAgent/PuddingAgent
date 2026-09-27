namespace PuddingDesktop.Foundation;

/// <summary>
/// External Task API runtime policy. Core reads it from system.json with a short cache, so the page shows
/// what Core currently believes rather than a value it cached itself.
/// </summary>
public sealed record ExternalApiStatus(
    bool Enabled, string PublicBaseUrl, bool RequireHttps,
    int DefaultTokenLifetimeDays, int MaxTokenLifetimeDays, int MaxActiveTokensPerOwner)
{
    public string EnabledText => Enabled ? "已启用" : "未启用";
    public string LifetimeText => $"默认 {DefaultTokenLifetimeDays} 天 · 上限 {MaxTokenLifetimeDays} 天";
    public string SchemeText => RequireHttps ? "要求 HTTPS（Loopback 例外）" : "不强制 HTTPS";
}

/// <summary>
/// An access token's metadata. There is deliberately no secret and no hash here: Core returns the plaintext
/// exactly once at creation, and never exposes the stored hash to any admin surface.
/// </summary>
public sealed record AccessTokenSummary(
    string TokenId, string KeyId, string DisplayPrefix, string Name, string OwnerUserId, int Version,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? RevokedAtUtc,
    string RevokedByUserId, string RevocationReason, DateTimeOffset? LastUsedAtUtc,
    IReadOnlyList<string> Scopes, IReadOnlyList<string> Workspaces, string Status)
{
    public string StatusText => AccessTokenText.DescribeStatus(Status);
    public bool IsActive => string.Equals(Status, "Active", StringComparison.OrdinalIgnoreCase);
    public string ScopesText => Scopes.Count == 0 ? "无 scope" : string.Join(" · ", Scopes);
    public string WorkspacesText => Workspaces.Count == 0 ? "无工作区" : string.Join("、", Workspaces);
    public string LastUsedText => LastUsedAtUtc is null
        ? "从未使用"
        : $"最后使用 {LastUsedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}";
    public string RevocationText => RevokedAtUtc is null
        ? ""
        : $"撤销于 {RevokedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}" +
          (RevokedByUserId.Length == 0 ? "" : $" · 由 {RevokedByUserId}") +
          (RevocationReason.Length == 0 ? "" : $" · 原因：{RevocationReason}");
}

/// <summary>The creation result. The plaintext exists only here.</summary>
public sealed record AccessTokenCreated(AccessTokenSummary Token, string AccessToken)
{
    public string TokenText => AccessToken;
}

public sealed record AccessTokenFilter(string Status, string OwnerUserId, string WorkspaceId, string Scope, int Page, int PageSize);

public sealed record AccessTokenListPage(IReadOnlyList<AccessTokenSummary> Items, int Total, int Page, int PageSize)
{
    public int PageCount => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

public sealed record AccessTokenCreateRequest(
    string Name, IReadOnlyList<string> WorkspaceIds, IReadOnlyList<string> Scopes, int? LifetimeDays);

public interface IAccessTokenSettings
{
    Task<ExternalApiStatus> ReadStatusAsync(CancellationToken cancellationToken = default);
    Task<AccessTokenListPage> ListAsync(AccessTokenFilter filter, CancellationToken cancellationToken = default);
    /// <summary>Returns the plaintext once; the caller must show it immediately and never store it.</summary>
    Task<AccessTokenCreated> CreateAsync(AccessTokenCreateRequest request, CancellationToken cancellationToken = default);
    Task<AccessTokenSummary?> ReadAsync(string tokenId, CancellationToken cancellationToken = default);
    Task RenameAsync(string tokenId, int expectedVersion, string name, CancellationToken cancellationToken = default);
    /// <summary>Irreversible and immediate. There is no unrevoke operation in Core.</summary>
    Task RevokeAsync(string tokenId, int expectedVersion, string reason, CancellationToken cancellationToken = default);
}

public static class AccessTokenText
{
    /// <summary>Mirrors ExternalTaskApiScopes.All. There is no wildcard scope.</summary>
    public static IReadOnlyList<string> Scopes { get; } =
        ["tasks.read", "tasks.write", "tasks.comment", "tasks.evaluate", "tasks.command",
         "workspaces.read", "agents.read", "messages.send"];

    public static IReadOnlyList<string> Statuses { get; } = ["Active", "Expired", "Revoked", "OwnerDisabled"];
    public static IReadOnlyList<int> PageSizes { get; } = [20, 50, 100];

    public const string SecretOnceNotice =
        "明文访问令牌只在创建响应里出现一次，此后 Core 不再提供任何回显；界面不提供 reveal，也不保存它。";

    public const string ScopeNotice =
        "scope 白名单固定 8 项，没有通配符：tasks.write 不隐含 tasks.command，tasks.evaluate 不改变状态；" +
        "未知 scope 在创建时就被 Core 拒绝，运行时 fail closed。";

    public const string RevokeNotice =
        "撤销不可逆且立即生效：Core 没有「取消撤销」，也不提供硬删除或扩大 scope/工作区。";

    /// <summary>Core only rejects a reason longer than 500 characters; the page asks for one regardless.</summary>
    public const string RevokeReasonNotice =
        "Core 只拒绝超过 500 字符的撤销原因，空原因在 Core 侧是允许的；界面仍然要求填写，以便审计可追溯。";

    public const string VersionNotice =
        "重命名与撤销都带 expectedVersion（CAS）：版本不一致说明别人先改过，界面必须让你刷新后重试，而不是覆盖。";

    public static string DescribeStatus(string? status) => status switch
    {
        null or "" => "状态未知",
        var value when string.Equals(value, "Active", StringComparison.OrdinalIgnoreCase) => "有效",
        var value when string.Equals(value, "Expired", StringComparison.OrdinalIgnoreCase) => "已过期",
        var value when string.Equals(value, "Revoked", StringComparison.OrdinalIgnoreCase) => "已撤销",
        var value when string.Equals(value, "OwnerDisabled", StringComparison.OrdinalIgnoreCase) => "Owner 已停用",
        var value => value
    };

    public static string DescribeScope(string scope) => scope switch
    {
        "tasks.read" => "tasks.read（读任务）",
        "tasks.write" => "tasks.write（写任务；不含 command）",
        "tasks.comment" => "tasks.comment（评论）",
        "tasks.evaluate" => "tasks.evaluate（评估；不改状态）",
        "tasks.command" => "tasks.command（下发命令）",
        "workspaces.read" => "workspaces.read（读工作区）",
        "agents.read" => "agents.read（读 Agent）",
        "messages.send" => "messages.send（发消息）",
        var value => value
    };

    public static bool IsKnownScope(string? scope) => scope is not null && Scopes.Contains(scope, StringComparer.Ordinal);
    public static bool IsKnownStatus(string? status) =>
        status is not null && Statuses.Contains(status, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Validate(AccessTokenCreateRequest request, ExternalApiStatus status)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Name)) errors.Add("名称不能为空。");
        if (request.Scopes.Count == 0) errors.Add("至少要选择一个 scope。");
        foreach (var scope in request.Scopes.Where(scope => !IsKnownScope(scope)))
            errors.Add($"未知 scope：{scope}。");
        if (request.WorkspaceIds.Count == 0) errors.Add("至少要选择一个工作区。");
        // Core rejects an explicit out-of-range lifetime; an omitted value uses the configured default.
        if (request.LifetimeDays is { } days && (days < 1 || days > status.MaxTokenLifetimeDays))
            errors.Add($"有效期必须在 1 到 {status.MaxTokenLifetimeDays} 天之间（留空使用默认 {status.DefaultTokenLifetimeDays} 天）。");
        return errors;
    }

    /// <summary>Maps Core's creation errors onto the message the page shows.</summary>
    public static string DescribeCreateError(string? error) => error switch
    {
        null or "" => "创建失败。",
        var value when value.EndsWith("InvalidName", StringComparison.Ordinal) => "名称不合法。",
        var value when value.EndsWith("NoScopes", StringComparison.Ordinal) => "必须至少选择一个 scope。",
        var value when value.EndsWith("UnknownScope", StringComparison.Ordinal) => "包含未知 scope。",
        var value when value.EndsWith("NoWorkspaces", StringComparison.Ordinal) => "必须至少选择一个工作区。",
        var value when value.EndsWith("UnknownWorkspace", StringComparison.Ordinal) => "包含不存在的工作区。",
        var value when value.EndsWith("LifetimeOutOfRange", StringComparison.Ordinal) => "有效期超出允许范围。",
        var value when value.EndsWith("TooManyActiveTokens", StringComparison.Ordinal) =>
            "该 Owner 的有效令牌数已达上限（Core 返回 409）。",
        var value => value
    };
}
