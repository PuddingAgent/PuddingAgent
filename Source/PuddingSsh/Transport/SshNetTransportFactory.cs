using System.Text;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace PuddingSsh.Transport;

/// <summary>
/// SSH.NET 适配的建连工厂（设计 §4.1 <c>Transport/</c>）。第三方类型只出现在 <c>Transport/</c> 内：
/// 公开合同（<see cref="ISshTransportFactory"/> / <see cref="ISshTransportSession"/>）不含 <c>SshClient</c>。
/// <para>
/// 依赖库的默认值对本组件**不安全**，必须显式覆盖：
/// <list type="bullet">
///   <item><c>ConnectionInfo.RetryAttempts</c> 默认 <b>10</b> ⇒ 固定 1（组件不自动重连重放）；</item>
///   <item><c>ConnectionInfo.Timeout</c> 默认 30 秒 ⇒ 由主机建连 deadline 决定；</item>
///   <item><c>MaxSessions</c> 默认 10 ⇒ 独占连接固定 1；</item>
///   <item><c>HostKeyReceived</c> ⇒ 一律先装**默认拒绝**回调（设计 §6.3），且在认证之前。</item>
/// </list>
/// </para>
/// </summary>
public sealed class SshNetTransportFactory : ISshTransportFactory
{
    public async Task<ISshTransportSession> ConnectAsync(
        SshTransportConnectRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Identity);
        ArgumentNullException.ThrowIfNull(request.HostKeyVerifier);

        var keyStream = request.Identity.OpenKeyStream();
        PrivateKeyFile? privateKey = null;
        SshClient? client = null;

        try
        {
            privateKey = CreatePrivateKeyFile(keyStream);
        }
        catch (Exception ex)
        {
            keyStream.Dispose();
            throw WrapCredentialFailure(ex);
        }

        var capture = new HostKeyCapture();

        try
        {
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

            client = new SshClient(connectionInfo);

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

            var session = new SshNetTransportSession(
                client,
                request.Target,
                request.Identity,
                privateKey,
                keyStream,
                capture.VerifiedFingerprint,
                capture.HostKeyAlgorithm);

            client = null;
            privateKey = null;
            keyStream = null!;
            return session;
        }
        catch (Exception ex)
        {
            client?.Dispose();
            privateKey?.Dispose();
            keyStream?.Dispose();
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

    private static PrivateKeyFile CreatePrivateKeyFile(Stream keyStream) => new(keyStream);

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

    /// <summary>握手回调的线程安全落点：回调可能在工作线程执行，不能用普通闭包变量跨线程读。</summary>
    private sealed class HostKeyCapture
    {
        private readonly object _gate = new();
        private string? _verifiedFingerprint;
        private string? _hostKeyAlgorithm;

        public SshHostKeyDecision? Rejection { get; private set; }

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
                    Rejection = decision;
                }
            }
        }
    }
}
