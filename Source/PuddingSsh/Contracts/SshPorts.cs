namespace PuddingSsh.Contracts;

/// <summary>
/// <c>ISshClient</c>：Runtime 工具层唯一消费的端口（设计 §4）。实现是
/// <see cref="Execution.SshOperationCoordinator"/>：准入排队、deadline 合成、凭据、建连、执行与释放都在实现侧，
/// 端口本身不泄露第三方类型。
/// <para>
/// 第一个参数是**冻结的主机上下文**（限额/凭据来源/信任集合/默认 cwd/宿主 deadline）。
/// 请求 DTO 只承载调用方（最终是模型）能提供的东西：目标、命令、cwd、限额请求与理由。
/// </para>
/// </summary>
public interface ISshClient
{
    Task<SshTestResult> TestAsync(
        SshHostContext host,
        SshTestRequest request,
        CancellationToken cancellationToken);

    Task<SshExecuteResult> ExecuteAsync(
        SshHostContext host,
        SshExecuteRequest request,
        CancellationToken cancellationToken);
}

/// <summary>宿主冻结的 identity 请求：**绝对** <c>.ssh</c> 目录 + 文件名校验后的结果。</summary>
public sealed record SshIdentityRequest(string AbsoluteSshDirectory, string IdentityFileName);

/// <summary>认证材料。目录解析（current_user / agent_private）由引用方完成，组件不搜寻其他身份、不跨作用域回退。</summary>
public interface ISshIdentityProvider
{
    ValueTask<SshIdentityMaterial> GetIdentityAsync(SshIdentityRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 认证材料的内存副本：**只在内存中交给第三方库**，连接释放时销毁（设计 §6.2）。
/// 不含口令字段 —— 首期加密私钥一律 <c>ssh.passphrase_required</c>。
/// </summary>
public sealed class SshIdentityMaterial : IDisposable
{
    private readonly byte[] _privateKey;
    private bool _disposed;

    public SshIdentityMaterial(string absoluteSshDirectory, string identityFileName, byte[] privateKey)
    {
        DirectoryPath = absoluteSshDirectory;
        IdentityFileName = identityFileName;
        _privateKey = privateKey;
    }

    /// <summary>冻结的绝对目录；只用于诊断，不用于再次读取文件。</summary>
    public string DirectoryPath { get; }

    public string IdentityFileName { get; }

    /// <summary>释放次数（内部可观测）：门禁断言「清零效果恰好发生一次」。</summary>
    internal int DisposeCount { get; private set; }

    /// <summary>私钥字节的内存流；调用方用后立即释放（<see cref="IDisposable.Dispose"/> 会清零）。</summary>
    internal MemoryStream OpenKeyStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new MemoryStream(_privateKey, writable: false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeCount++;
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_privateKey);
    }
}

/// <summary>握手前的主机密钥证据（只带 BCL 类型，不外泄第三方事件类型）。</summary>
public sealed record SshHostKeyEvidence(
    string HostKeyAlgorithm,
    string Sha256Fingerprint,
    ReadOnlyMemory<byte> HostKeyBlob);

/// <summary>验证裁定。拒绝必须携带设计 §10 的稳定错误码。</summary>
public sealed record SshHostKeyDecision(bool Trusted, string? ErrorCode, string? Diagnostic)
{
    public static SshHostKeyDecision Trust() => new(true, null, null);

    public static SshHostKeyDecision Reject(string errorCode, string? diagnostic = null) =>
        new(false, errorCode, diagnostic);
}

/// <summary>
/// 主机密钥验证端口：在**认证之前**安装，默认拒绝（设计 §6.3）。首期实现 = known_hosts 明文/哈希记录
/// 与显式 pin（A2）；组件不提供「首次信任」或自动写 known_hosts。
/// </summary>
public interface ISshHostKeyVerifier
{
    SshHostKeyDecision Verify(SshHostKeyEvidence evidence);
}

/// <summary>
/// 一次操作独占连接的建连请求（设计 §1.2 第 2 条：首期不做连接池）。
/// <see cref="Identity"/> 的所有权**转移**给组件：建连失败由组件销毁，建连成功由会话在释放时销毁并清零。
/// </summary>
public sealed record SshTransportConnectRequest(
    SshTargetRef Target,
    SshIdentityMaterial Identity,
    ISshHostKeyVerifier HostKeyVerifier,
    TimeSpan ConnectTimeout);

/// <summary>
/// 第三方客户端适配端口（实现见 <c>Transport/</c>）。可注入假连接，因此上层协调器（A3）可在无服务端时测试。
/// </summary>
public interface ISshTransportFactory
{
    Task<ISshTransportSession> ConnectAsync(SshTransportConnectRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 提交事实（ADR-096 D4）：进入**可能发送 exec** 的调用之前必须推进到
/// <see cref="SubmissionAttempted"/>；只有底层能明确证明「什么都没写出去」时才允许
/// <see cref="NotSent"/>，此时才可能保持 <c>NotSubmitted</c> 与 <c>retry_safe</c>。
/// </summary>
public enum SshSubmissionEvidence
{
    /// <summary>明确未发送（例如尚未调用 exec，或底层给出未写出证据）。</summary>
    NotSent,

    /// <summary>可能已经发送：此后任何失败都必须按「远端状态未知、不可重试」处理。</summary>
    SubmissionAttempted,
}

/// <summary>一次独占连接的会话：Exec + 双流有界采集 + 终止 + 资源释放。</summary>
public interface ISshTransportSession : IAsyncDisposable
{
    /// <summary>本次握手实际验证通过的指纹（<c>SHA256:...</c>）。</summary>
    string VerifiedHostKeySha256 { get; }

    /// <summary>本次握手实际使用的主机密钥算法（如 <c>ssh-ed25519</c>）；诊断与 <c>ssh_test</c> 结果用。</summary>
    string HostKeyAlgorithm { get; }

    bool IsConnected { get; }

    /// <summary>提交事实；调用 exec 之前必须已推进（见 <see cref="SshSubmissionEvidence"/>）。</summary>
    SshSubmissionEvidence SubmissionEvidence { get; }

    /// <summary>
    /// **非阻塞**中止请求：只登记请求，实际的 TERM/关闭连接由会话自己的有界清理状态机执行
    /// （ADR-096 D3）；调用线程不得因此被卡住。
    /// </summary>
    void RequestAbort();

    /// <summary>
    /// 执行一条 POSIX 命令。取消只是**发送信号**并可能结束客户端等待，因此调用方不得据此宣称远端进程已消失。
    /// </summary>
    Task<SshExecuteResult> ExecuteAsync(
        string command,
        TimeSpan timeout,
        int maxOutputBytes,
        CancellationToken cancellationToken);
}

/// <summary>
/// 会话的**资源可见性**（ADR-096 D3）：让上层能断言「回收真的发生了」，而不是只看
/// 「业务调用返回了」。实现可选；不实现时上层按未知处理，不据此宣称已释放。
/// </summary>
public interface ISshTransportSessionDiagnostics
{
    /// <summary>仍然存活的流读取任务数（stdout/stderr 排空）。</summary>
    int PendingReaders { get; }

    /// <summary>是否已收到中止请求。</summary>
    bool AbortRequested { get; }

    /// <summary>连接与命令是否已经关闭（关闭状态机完成）。</summary>
    bool Closed { get; }
}
