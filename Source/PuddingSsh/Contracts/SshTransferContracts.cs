namespace PuddingSsh.Contracts;

/// <summary>覆盖策略（切片 B）：默认**禁止覆盖**，只有显式请求才允许替换目标。</summary>
public enum SshOverwritePolicy
{
    /// <summary>目标已存在即失败（默认）。</summary>
    Fail,

    /// <summary>显式允许替换目标。</summary>
    Overwrite,
}

/// <summary>
/// 单文件上传（切片 B）：本地源文件 → 远端目标路径。
/// 远端路径按 POSIX 处理，必须落在主机配置的**允许上传根**之内。
/// </summary>
public sealed record SshUploadRequest(
    SshTargetRef Target,
    string LocalPath,
    string RemotePath,
    SshOverwritePolicy Overwrite = SshOverwritePolicy.Fail,
    long? MaxBytes = null);

/// <summary>
/// 单文件下载（切片 B）：远端源路径 → 本地目标路径。
/// 本地路径以执行快照工作区为根（<see cref="SshTransferLimits.LocalRoot"/>）。
/// </summary>
public sealed record SshDownloadRequest(
    SshTargetRef Target,
    string RemotePath,
    string LocalPath,
    SshOverwritePolicy Overwrite = SshOverwritePolicy.Fail,
    long? MaxBytes = null);

/// <summary>
/// 传输限额（切片 B）：默认单文件 64 MiB，硬上限 256 MiB；流式复制，超限中止，禁止整文件装入内存。
/// <see cref="UploadRoot"/> 与 <see cref="DownloadRoot"/> 是**分别建模**的远端允许根（POSIX 绝对路径）。
/// </summary>
public sealed record SshTransferLimits
{
    /// <summary>默认单文件上限 64 MiB。</summary>
    public const long DefaultMaxBytes = 64L * 1024 * 1024;

    /// <summary>硬上限 256 MiB（配置只能在其内收窄）。</summary>
    public const long HardMaxBytes = 256L * 1024 * 1024;

    /// <summary>默认上限（可被请求进一步收窄，但不得超过硬上限）。</summary>
    public long MaxBytes { get; init; } = DefaultMaxBytes;

    /// <summary>远端允许上传根（POSIX 绝对路径，已规范化）。</summary>
    public string UploadRoot { get; init; } = string.Empty;

    /// <summary>远端允许下载根（POSIX 绝对路径，已规范化）。</summary>
    public string DownloadRoot { get; init; } = string.Empty;

    /// <summary>本地根（执行快照工作区绝对路径）。</summary>
    public string LocalRoot { get; init; } = string.Empty;

    /// <summary>
    /// 机器可校验：非法配置**拒绝**（<c>ssh.config_invalid</c>），不做静默夹取。
    /// </summary>
    public void Validate()
    {
        if (MaxBytes <= 0 || MaxBytes > HardMaxBytes)
        {
            throw new Diagnostics.SshComponentException(
                Diagnostics.SshErrorCodes.ConfigInvalid,
                $"传输限额非法：max_bytes={MaxBytes}（取值 1..{HardMaxBytes}）。");
        }

        RequireRoot(UploadRoot, "transfer.upload_root");
        RequireRoot(DownloadRoot, "transfer.download_root");

        // 注意：Windows 上 "/workspace" 是 rooted 但**不是** fully-qualified（无盘符），
        // 因此这里用 IsPathRooted 判定「绝对」，与 POSIX 语义一致。
        if (string.IsNullOrWhiteSpace(LocalRoot) || !System.IO.Path.IsPathRooted(LocalRoot))
        {
            throw new Diagnostics.SshComponentException(
                Diagnostics.SshErrorCodes.ConfigInvalid,
                "传输限额非法：transfer.local_root 必须是绝对路径。");
        }
    }

    /// <summary>有效上限 = min(请求值 ?? 默认值, 配置上限, 硬上限)。0/负数请求由 validator 拒绝。</summary>
    public long ResolveMaxBytes(long? requestedBytes)
    {
        var requested = requestedBytes ?? MaxBytes;
        return Math.Min(Math.Min(requested, MaxBytes), HardMaxBytes);
    }

    private static void RequireRoot(string root, string field)
    {
        if (string.IsNullOrWhiteSpace(root) || !root.StartsWith('/'))
        {
            throw new Diagnostics.SshComponentException(
                Diagnostics.SshErrorCodes.ConfigInvalid,
                $"传输限额非法：{field} 必须是 POSIX 绝对路径（当前为 '{root}'）。");
        }
    }
}

/// <summary>上传计划：本地源 + 规范化远端目标 + 同目录唯一临时文件名。</summary>
public sealed record SshUploadPlan(
    string LocalPath,
    string RemotePath,
    string RemoteTempPath,
    string RemoteRoot,
    SshOverwritePolicy Overwrite,
    long MaxBytes);

/// <summary>下载计划：规范化远端源 + 本地目标 + 同目录唯一临时文件名。</summary>
public sealed record SshDownloadPlan(
    string RemotePath,
    string LocalPath,
    string LocalTempPath,
    string RemoteRoot,
    SshOverwritePolicy Overwrite,
    long MaxBytes);
/// <summary>
/// 传输结果（协调器输出）：与执行结果一样表达「本端终止事实、远端未知、清理是否收敛、临时文件是否残留」。
/// </summary>
public sealed record SshTransferResult
{
    public SshExecutionStatus Status { get; init; } = SshExecutionStatus.Failed;

    public Diagnostics.SshPhase Phase { get; init; } = Diagnostics.SshPhase.Validation;

    public string HostId { get; init; } = string.Empty;

    public string HostRevision { get; init; } = string.Empty;

    /// <summary>实际传输的字节数（中断时是已确认写入的字节）。</summary>
    public long BytesTransferred { get; init; }

    public string? RemotePath { get; init; }

    public string? LocalPath { get; init; }

    /// <summary>不自动重试：传输一律 false，避免调用方误判为可安全重放。</summary>
    public bool RetrySafe { get; init; }

    public string? ErrorCode { get; init; }

    public string? Diagnostic { get; init; }

    /// <summary>清理状态（关闭证据）：Pending 表示资源仍在隔离区被追踪。</summary>
    public SshCleanupState CleanupState { get; init; } = SshCleanupState.NotStarted;

    /// <summary>本次临时文件是否残留（true 时 <see cref="TemporaryPath"/> 给出路径）。</summary>
    public bool TemporaryLeftBehind { get; init; }

    public string? TemporaryPath { get; init; }

    /// <summary>实测的服务端传输能力（诊断用；未探测为 null）。</summary>
    public SshTransferCapabilities? Capabilities { get; init; }

    public int DurationMs { get; init; }
}