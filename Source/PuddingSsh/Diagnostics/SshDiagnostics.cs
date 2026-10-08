namespace PuddingSsh.Diagnostics;

/// <summary>
/// 组件错误码唯一真源（设计 §10）。公开错误**不透传第三方异常文本**：分类器（A2）只吃异常类别与执行事实，
/// 产出下表中的稳定码。
/// </summary>
public static class SshErrorCodes
{
    public const string ConfigInvalid = "ssh.config_invalid";

    /// <summary>
    /// 请求形状非法（缺 command、command 超 16 KiB 或含 NUL、cwd 非 POSIX 绝对路径等）。
    /// 设计 §10 表未列该码：请求参数拒绝属于工具层职责，但组件在 A2 引入 POSIX 封装与请求校验后
    /// 必须能**自己**fail-closed，而不是抛 BCL 异常让上层猜。语义与 <c>config_invalid</c>（主机配置非法）
    /// 明确分离：一个来自调用参数，一个来自冻结配置。
    /// </summary>
    public const string RequestInvalid = "ssh.request_invalid";

    public const string HostUnavailable = "ssh.host_unavailable";
    public const string TargetChanged = "ssh.target_changed";
    public const string CredentialUnavailable = "ssh.credential_unavailable";
    public const string DirectoryUnavailable = "ssh.directory_unavailable";
    public const string PassphraseRequired = "ssh.passphrase_required";
    public const string KnownHostsUnsupported = "ssh.known_hosts_unsupported";
    public const string HostKeyUntrusted = "ssh.host_key_untrusted";
    public const string HostKeyMismatch = "ssh.host_key_mismatch";
    public const string HostKeyRevoked = "ssh.host_key_revoked";
    public const string DnsFailed = "ssh.dns_failed";
    public const string ConnectFailed = "ssh.connect_failed";
    public const string AuthenticationFailed = "ssh.authentication_failed";
    public const string CommandDenied = "ssh.command_denied";
    public const string Busy = "ssh.busy";
    public const string Timeout = "ssh.timeout";
    public const string Cancelled = "ssh.cancelled";
    public const string ConnectionLost = "ssh.connection_lost";
    public const string ExitStatusMissing = "ssh.exit_status_missing";
    public const string RemoteExitNonzero = "ssh.remote_exit_nonzero";
    public const string TransportFault = "ssh.transport_fault";
}

/// <summary>
/// 执行阶段（设计 §10）。失败结果必须包含**最后可靠**的 phase；枚举真源在此，wire 名称由
/// <see cref="SshWireNames.ToWire(SshPhase)"/> 给出。
/// </summary>
public enum SshPhase
{
    Validation,
    Queued,
    Credentials,
    Resolving,
    Connecting,
    HostKey,
    Authenticating,
    Submitting,
    Executing,
    Collecting,
    Cleanup,
    Completed,
}

/// <summary>枚举 → 线上字符串（lower_snake）。序列化形状是合同的一部分，不许在各处手写。</summary>
public static class SshWireNames
{
    public static string ToWire(this SshPhase phase) => phase switch
    {
        SshPhase.Validation => "validation",
        SshPhase.Queued => "queued",
        SshPhase.Credentials => "credentials",
        SshPhase.Resolving => "resolving",
        SshPhase.Connecting => "connecting",
        SshPhase.HostKey => "host_key",
        SshPhase.Authenticating => "authenticating",
        SshPhase.Submitting => "submitting",
        SshPhase.Executing => "executing",
        SshPhase.Collecting => "collecting",
        SshPhase.Cleanup => "cleanup",
        SshPhase.Completed => "completed",
        _ => "unknown",
    };

    public static string ToWire(this Contracts.SshExecutionStatus status) => status switch
    {
        Contracts.SshExecutionStatus.Completed => "completed",
        Contracts.SshExecutionStatus.Failed => "failed",
        Contracts.SshExecutionStatus.TimedOut => "timed_out",
        Contracts.SshExecutionStatus.Cancelled => "cancelled",
        _ => "unknown",
    };

    public static string ToWire(this Contracts.SshExecutionState state) => state switch
    {
        Contracts.SshExecutionState.NotSubmitted => "not_submitted",
        Contracts.SshExecutionState.Submitted => "submitted",
        Contracts.SshExecutionState.Exited => "exited",
        _ => "unknown",
    };
}
