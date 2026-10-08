namespace PuddingSsh.Contracts;

/// <summary>
/// <c>ISshClient</c>：Runtime 工具层唯一消费的端口（设计 §4）。Test/Execute 的**实现**属于协调器（A3）：
/// 排队、deadline、并发准入、清理宽限都在实现侧，端口本身不泄露第三方类型。
/// </summary>
public interface ISshClient
{
    Task<SshTestResult> TestAsync(SshTestRequest request, CancellationToken cancellationToken);

    Task<SshExecuteResult> ExecuteAsync(SshExecuteRequest request, CancellationToken cancellationToken);
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

/// <summary>一次独占连接的会话：Exec + 双流有界采集 + 终止 + 资源释放。</summary>
public interface ISshTransportSession : IAsyncDisposable
{
    /// <summary>本次握手实际验证通过的指纹（<c>SHA256:...</c>）。</summary>
    string VerifiedHostKeySha256 { get; }

    bool IsConnected { get; }

    /// <summary>
    /// 执行一条 POSIX 命令。取消只是**发送信号**并可能结束客户端等待，因此调用方不得据此宣称远端进程已消失。
    /// </summary>
    Task<SshExecuteResult> ExecuteAsync(
        string command,
        TimeSpan timeout,
        int maxOutputBytes,
        CancellationToken cancellationToken);
}
