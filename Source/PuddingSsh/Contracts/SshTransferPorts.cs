namespace PuddingSsh.Contracts;

/// <summary>
/// 服务端 SFTP 能力**实测**结果（切片 B）：设计明确要求「原子/不覆盖 rename 能力必须实测，
/// 服务器不支持时返回不支持」——因此这些值只能来自真实探针，不能假定。
/// </summary>
public sealed record SshTransferCapabilities(
    bool AtomicReplace,
    bool RenameNoOverwrite,
    bool ExclusiveCreate)
{
    /// <summary>尚未探测。</summary>
    public static SshTransferCapabilities Unknown { get; } = new(false, false, false);
}

/// <summary>
/// 传输结果（切片 B）：状态、实际字节、稳定错误码与诊断。
/// <see cref="TemporaryLeftBehind"/> 表示**本次**临时文件残留（不得误报原目标已回滚）。
/// </summary>
public sealed record SshTransferOutcome(
    SshExecutionStatus Status,
    long BytesTransferred,
    string? ErrorCode,
    string? Diagnostic,
    bool TemporaryLeftBehind = false,
    string? TemporaryPath = null)
{
    public static SshTransferOutcome Completed(long bytes) =>
        new(SshExecutionStatus.Completed, bytes, null, null);

    public static SshTransferOutcome Failed(string errorCode, long bytes, string? diagnostic, bool tempLeftBehind = false, string? tempPath = null) =>
        new(SshExecutionStatus.Failed, bytes, errorCode, diagnostic, tempLeftBehind, tempPath);

    public static SshTransferOutcome Cancelled(long bytes, string? diagnostic, bool tempLeftBehind = false, string? tempPath = null) =>
        new(SshExecutionStatus.Cancelled, bytes, Diagnostics.SshErrorCodes.Cancelled, diagnostic, tempLeftBehind, tempPath);
}

/// <summary>
/// 一次独占的 SFTP 传输会话（切片 B）：单文件上传/下载 + 能力实测 + 有界释放。
/// 与 <see cref="ISshTransportSession"/> 一样，一个会话代表一条独占连接，不做并发保护。
/// </summary>
public interface ISshTransferSession : IAsyncDisposable
{
    /// <summary>本次握手实际验证通过的指纹。</summary>
    string VerifiedHostKeySha256 { get; }

    /// <summary>
    /// 实测的服务端传输能力**快照**；结果阶段只读它，不得再做远端探测。
    /// 空值表示调用方尚未要求探测（上传会在提交前把它作为有界阶段执行）。
    /// </summary>
    SshTransferCapabilities? Capabilities { get; }

    /// <summary>执行一次能力探测（由调用方在有界阶段/预算内调用；探测成功后写入快照）。</summary>
    Task<SshTransferCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken);

    /// <summary>关闭证据：通道已释放。</summary>
    bool Closed { get; }

    /// <summary>非阻塞中止请求（本端取消/截止时由协调器调用）。</summary>
    void RequestAbort();

    /// <summary>带预算释放（与 <see cref="ISshTransportSession"/> 同一预算合同）。</summary>
    ValueTask DisposeAsync(TimeSpan budget);

    /// <summary>上传单个普通文件：同目录唯一临时文件 + 发布。</summary>
    Task<SshTransferOutcome> UploadAsync(
        SshUploadPlan plan,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>下载单个普通文件：本地同目录唯一临时文件 + 发布。</summary>
    Task<SshTransferOutcome> DownloadAsync(
        SshDownloadPlan plan,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// 传输会话工厂（切片 B）：与执行路径共用身份/信任/超时合同，只是通道换成 SFTP。
/// </summary>
public interface ISshTransferFactory
{
    Task<ISshTransferSession> ConnectTransferAsync(
        SshTransportConnectRequest request,
        string scratchDirectory,
        CancellationToken cancellationToken);
}

/// <summary>
/// 排他创建被服务端拒绝（目标已存在）：创建层**没有**创建文件，因此调用方不得回滚删除。
/// </summary>
public sealed class SshSftpTargetExistsException(string remotePath)
    : Exception($"exclusive create rejected: {remotePath}")
{
    public string RemotePath { get; } = remotePath;
}

/// <summary>
/// SFTP 通道（切片 B）：把用到的 SFTP 原语收成一个小接口，
/// 使「发布语义/能力判定/临时文件清理」这些逻辑可以**确定性单测**；
/// 真实实现在 <c>Transport/</c>，由探针在真实 OpenSSH 上验证。
/// </summary>
public interface ISshSftpChannel : IDisposable
{
    bool IsConnected { get; }

    bool Exists(string remotePath);

    /// <summary>是否为普通文件（false = 目录/不存在/未知）。</summary>
    bool IsRegularFile(string remotePath);

    long GetLength(string remotePath);

    /// <summary>用 SFTP realpath 语义解析目录（实现可用 ChangeDirectory + WorkingDirectory）。</summary>
    string GetRealDirectory(string remotePath);

    Stream OpenRead(string remotePath);

    /// <summary>
    /// 对目标做**排他创建**并返回可写流；目标已存在时抛 <see cref="SshSftpTargetExistsException"/>
    /// （此时**没有**创建任何东西 ⇒ 调用方不得回滚删除）。创建成功即表示本次持有该文件。
    /// </summary>
    Stream OpenWriteNew(string remotePath);

    /// <summary>标准 SFTP RENAME（不覆盖语义需实测；OpenSSH 实测为覆盖）。</summary>
    void Rename(string oldPath, string newPath);

    /// <summary>posix-rename@openssh.com（原子替换）。</summary>
    void RenamePosix(string oldPath, string newPath);

    void Delete(string remotePath);
}
