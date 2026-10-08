namespace PuddingSsh.Diagnostics;

/// <summary>
/// 组件公开失败：**携带稳定错误码**（设计 §10），因此上层不需要解析第三方异常文本。
/// 不入公开消息的内容：私钥、路径细节、第三方异常文本（只保留在 <see cref="Exception.InnerException"/> 供诊断栈使用）。
/// </summary>
public class SshComponentException : Exception
{
    public SshComponentException(string errorCode, string? diagnostic = null, Exception? innerException = null)
        : base(diagnostic ?? errorCode, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

/// <summary>
/// 握手阶段被本端验证器拒绝（未知/变化/撤销指纹）。单独成类型是为了让上层能精确区分
/// 「密钥不可信」（需要用户核对）与「网络不可达」（可安全重试）。
/// </summary>
public sealed class SshHostKeyRejectedException : SshComponentException
{
    public SshHostKeyRejectedException(string errorCode, string? diagnostic = null, Exception? innerException = null)
        : base(errorCode, diagnostic, innerException)
    {
    }
}

/// <summary>
/// A1 的最小异常分类器：输入**异常类别 + 提交事实**，输出稳定错误码（设计 §10）。
/// A2 会在此之上补齐目录/known_hosts/脱敏等分类；这里不靠异常文本猜语义。
/// </summary>
public static class SshFailureClassifier
{
    /// <param name="exception">第三方或 BCL 异常。</param>
    /// <param name="commandSubmitted">命令是否已经提交到远端（提交后断线 ⇒ 状态可能未知）。</param>
    public static string Classify(Exception exception, bool commandSubmitted)
    {
        ArgumentNullException.ThrowIfNull(exception);

        switch (exception)
        {
            case SshComponentException component:
                return component.ErrorCode;

            case Renci.SshNet.Common.SshPassPhraseNullOrEmptyException:
                return SshErrorCodes.PassphraseRequired;

            case Renci.SshNet.Common.SshAuthenticationException:
                return SshErrorCodes.AuthenticationFailed;

            case Renci.SshNet.Common.SshOperationTimeoutException:
                return SshErrorCodes.Timeout;

            case Renci.SshNet.Common.SshConnectionException:
                return commandSubmitted ? SshErrorCodes.ConnectionLost : SshErrorCodes.ConnectFailed;

            case System.Net.Sockets.SocketException socket:
                return socket.SocketErrorCode is System.Net.Sockets.SocketError.HostNotFound
                    or System.Net.Sockets.SocketError.TryAgain
                    or System.Net.Sockets.SocketError.NoData
                    ? SshErrorCodes.DnsFailed
                    : SshErrorCodes.ConnectFailed;

            case Renci.SshNet.Common.SshException:
                return commandSubmitted ? SshErrorCodes.ConnectionLost : SshErrorCodes.TransportFault;

            case System.IO.IOException or System.IO.FileNotFoundException or UnauthorizedAccessException:
                return SshErrorCodes.CredentialUnavailable;

            default:
                return SshErrorCodes.TransportFault;
        }
    }

    /// <summary>建连失败（未提交）是否可安全再次尝试。命令一旦提交过，一律为 <see langword="false"/>。</summary>
    public static bool IsRetrySafe(Exception exception, bool commandSubmitted)
    {
        if (commandSubmitted)
        {
            return false;
        }

        var code = Classify(exception, commandSubmitted: false);
        return code is SshErrorCodes.DnsFailed or SshErrorCodes.ConnectFailed or SshErrorCodes.Busy;
    }
}
