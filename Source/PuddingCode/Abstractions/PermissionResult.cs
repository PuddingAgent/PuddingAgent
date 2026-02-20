namespace PuddingCode.Abstractions;

/// <summary>Agent 操作的安全级别。</summary>
public enum SecurityLevel
{
    /// <summary>L0: 只读，自动放行。</summary>
    ReadOnly = 0,

    /// <summary>L1: 项目内写入。</summary>
    ProjectWrite = 1,

    /// <summary>L2: 系统执行，必须人工确认。</summary>
    SystemExecution = 2
}

/// <summary>权限校验结果。</summary>
public sealed record PermissionResult(
    bool IsAllowed,
    SecurityLevel Level,
    string? DenialReason = null)
{
    public static PermissionResult Allowed(SecurityLevel level)
        => new(true, level);

    public static PermissionResult RequiresApproval(SecurityLevel level, string reason)
        => new(false, level, reason);

    public static PermissionResult Denied(string reason)
        => new(false, SecurityLevel.SystemExecution, reason);
}
