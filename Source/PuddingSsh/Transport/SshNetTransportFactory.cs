using System.Text;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Validation;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace PuddingSsh.Transport;

/// <summary>
/// SSH.NET 适配的建连工厂（设计 §4.1 <c>Transport/</c>）：第三方类型只出现在 <c>Transport/</c> 内。
/// 执行会话与 SFTP 传输会话共用同一套「默认拒绝的主机密钥 + 冻结身份 + 固定版本」建连准备。
/// </summary>
public sealed class SshNetTransportFactory : ISshTransportFactory
{
    public async Task<ISshTransportSession> ConnectAsync(
        SshTransportConnectRequest request,
        CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(request, static connectionInfo => new SshClient(connectionInfo), cancellationToken)
            .ConfigureAwait(false);

        var client = (SshClient)prepared.Client;
        var identity = request.Identity;
        return new SshNetTransportSession(
            client,
            request.Target,
            identity,
            prepared.PrivateKey,
            prepared.KeyStream,
            prepared.Capture.VerifiedFingerprint,
            prepared.Capture.HostKeyAlgorithm);
    }

    /// <summary>
    /// 打开一次**独占的 SFTP 传输会话**（切片 B）：身份/信任/超时合同与执行路径完全一致，
    /// 只是通道换成 SFTP。文件系统资源（client/key/keyStream/identity）由传输会话在释放时一并回收。
    /// </summary>
    public async Task<ISshTransferSession> ConnectTransferAsync(
        SshTransportConnectRequest request,
        string scratchDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirectory);
        SshRemotePathPolicy.NormalizeRoot(scratchDirectory, "transfer.scratch_directory");

        var prepared = await PrepareAsync(request, static connectionInfo => new SftpClient(connectionInfo), cancellationToken)
            .ConfigureAwait(false);

        var identity = request.Identity;
        return new SshSftpTransferSession(
            new SshSftpChannel((SftpClient)prepared.Client),
            prepared.Capture.VerifiedFingerprint ?? string.Empty,
            scratchDirectory,
            SshLocalFileSystem.Instance,
            releaseOwnedResources: () =>
            {
                prepared.PrivateKey.Dispose();
                prepared.KeyStream.Dispose();
                identity.Dispose();
            });
    }

    /// <summary>建连准备：校验 + 冻结身份 + 主机密钥回调（默认拒绝）+ 有界 Connect。</summary>
    private static async Task<PreparedConnection> PrepareAsync(
        SshTransportConnectRequest request,
        Func<ConnectionInfo, BaseClient> createClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Identity);
        ArgumentNullException.ThrowIfNull(request.HostKeyVerifier);

        // 底层公开入口的**防御校验**（ADR-096 D5）：不能只等 Runtime 加检查。
        SshRequestValidator.ValidateTarget(request.Target);
        if (request.ConnectTimeout <= TimeSpan.Zero)
        {
            throw new SshComponentException(
                SshErrorCodes.ConfigInvalid,
                "connect_timeout 必须为正；过期/零额度不得传入库（ADR-096 D2）。");
        }

        var keyStream = request.Identity.OpenKeyStream();
        PrivateKeyFile? privateKey = null;
        BaseClient? client = null;
        var capture = new HostKeyCapture();

        try
        {
            try
            {
                privateKey = new PrivateKeyFile(keyStream);
            }
            catch (Exception ex)
            {
                keyStream.Dispose();
                throw WrapCredentialFailure(ex);
            }

            var authentication = new PrivateKeyAuthenticationMethod(request.Target.Username, privateKey);

            var connectionInfo = new ConnectionInfo(
                request.Target.Host,
                request.Target.Port,
                request.Target.Username,
                authentication)
            {
                Timeout = request.ConnectTimeout,
                RetryAttempts = 1,
                MaxSessions = 1,
                ChannelCloseTimeout = TimeSpan.FromSeconds(1),
                Encoding = Encoding.UTF8,
            };

            client = createClient(connectionInfo);

            // 默认拒绝：只有验证器明确 Trust 才置 CanTrust=true。回调在 ConnectAsync 之前安装，
            // 因此不存在「认证先发生、之后再验证」的窗口。
            client.HostKeyReceived += (_, e) =>
            {
                var evidence = new SshHostKeyEvidence(
                    e.HostKeyName ?? string.Empty,
                    "SHA256:" + e.FingerPrintSHA256,
                    e.HostKey);

                var decision = request.HostKeyVerifier.Verify(evidence);
                e.CanTrust = decision.Trusted;
                capture.Record(decision, evidence);
            };

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(request.ConnectTimeout);
            await client.ConnectAsync(connectCts.Token).ConfigureAwait(false);

            return new PreparedConnection(client, privateKey, keyStream, capture);
        }
        catch (Exception ex)
        {
            client?.Dispose();
            privateKey?.Dispose();
            keyStream.Dispose();
            request.Identity.Dispose();

            if (capture.Rejection is { Trusted: false } rejection)
            {
                throw new SshHostKeyRejectedException(
                    rejection.ErrorCode ?? SshErrorCodes.HostKeyUntrusted,
                    rejection.Diagnostic,
                    ex);
            }

            throw;
        }
    }

    /// <summary>
    /// 加密私钥首期必须**明确**返回 <c>ssh.passphrase_required</c>（设计 §6.2）：不把口令放进参数或日志、
    /// 不改写用户密钥；选定文件不可用则为 <c>ssh.credential_unavailable</c>。
    /// </summary>
    private static SshComponentException WrapCredentialFailure(Exception exception)
    {
        if (exception is SshPassPhraseNullOrEmptyException)
        {
            return new SshComponentException(
                SshErrorCodes.PassphraseRequired,
                "选定私钥已加密，首期不提供口令输入能力（系统 ssh-agent 另行交付）。",
                exception);
        }

        return new SshComponentException(
            SshErrorCodes.CredentialUnavailable,
            "选定私钥不可用或格式非法。",
            exception);
    }

    private sealed record PreparedConnection(
        BaseClient Client,
        PrivateKeyFile PrivateKey,
        Stream KeyStream,
        HostKeyCapture Capture);

    /// <summary>握手回调的线程安全落点：回调可能在工作线程执行，不能用普通闭包变量跨线程读。</summary>
    private sealed class HostKeyCapture
    {
        private readonly object _gate = new();
        private SshHostKeyDecision? _rejection;
        private string? _verifiedFingerprint;
        private string? _hostKeyAlgorithm;

        public SshHostKeyDecision? Rejection
        {
            get
            {
                lock (_gate)
                {
                    return _rejection;
                }
            }
        }

        public string? VerifiedFingerprint
        {
            get
            {
                lock (_gate)
                {
                    return _verifiedFingerprint;
                }
            }
        }

        public string? HostKeyAlgorithm
        {
            get
            {
                lock (_gate)
                {
                    return _hostKeyAlgorithm;
                }
            }
        }

        public void Record(SshHostKeyDecision decision, SshHostKeyEvidence evidence)
        {
            lock (_gate)
            {
                if (decision.Trusted)
                {
                    _verifiedFingerprint = evidence.Sha256Fingerprint;
                    _hostKeyAlgorithm = evidence.HostKeyAlgorithm;
                }
                else
                {
                    _rejection ??= decision;
                }
            }
        }
    }
}
