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

    /// <summary>远端以信号结束（只有 exit-signal、没有 exit-status）。</summary>
    public const string RemoteExitSignal = "ssh.remote_exit_signal";

    /// <summary>退出码为 0 但输出没有读完（读失败或排空被中止）：不得报成完整成功。</summary>
    public const string OutputIncomplete = "ssh.output_incomplete";
    public const string TransportFault = "ssh.transport_fault";

    // ---------- 切片 B：单文件 SFTP 传输 ----------

    /// <summary>主机未配置传输限额（未开放上传/下载根）：关闭式失败。</summary>
    public const string TransferUnavailable = "ssh.transfer_unavailable";

    /// <summary>服务端不支持原子/不覆盖发布（能力实测不成立）：**不得**用竞态实现冒充原子性。</summary>
    public const string TransferUnsupported = "ssh.transfer_unsupported";

    /// <summary>超过本次/配置的文件字节上限（流式中止）。</summary>
    public const string TransferTooLarge = "ssh.transfer_too_large";

    /// <summary>传输中断/未完成（不自动重试、不提供断点续传）。</summary>
    public const string TransferIncomplete = "ssh.transfer_incomplete";

    /// <summary>本地路径被拒绝（越出根、重解析点、目录、设备文件等）。</summary>
    public const string LocalPathInvalid = "ssh.local_path_invalid";

    /// <summary>远端路径被拒绝（非绝对、越出允许根、符号链接逃逸等）。</summary>
    public const string RemotePathInvalid = "ssh.remote_path_invalid";

    /// <summary>本地目标已存在且未允许覆盖。</summary>
    public const string LocalPathExists = "ssh.local_path_exists";

    /// <summary>远端目标已存在且未允许覆盖。</summary>
    public const string RemotePathExists = "ssh.remote_path_exists";

    /// <summary>传输本身的失败（读写/权限/服务端错误）。</summary>
    public const string TransferFailed = "ssh.transfer_failed";

    /// <summary>清理失败：临时文件残留（不得误报原目标已回滚）。</summary>
    public const string TransferCleanupFailed = "ssh.transfer_cleanup_failed";
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
