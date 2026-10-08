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

/// <summary>分类输入事实：**最后可靠的 phase** 与命令是否已提交到远端（设计 §7.3/§10）。</summary>
public sealed record SshFailureFacts(SshPhase Phase, bool CommandSubmitted);

/// <summary>分类输出：稳定错误码 + phase + 是否可安全再次尝试。</summary>
public sealed record SshFailure(string ErrorCode, SshPhase Phase, bool RetrySafe);

/// <summary>
/// 异常分类器（设计 §10）：输入**异常类别 + 执行事实**，输出稳定错误码。
/// 不用异常文本做语义判断，也不把第三方文本透传给用户。
/// </summary>
public static class SshFailureClassifier
{
    /// <summary>A1 调用点的兼容入口：只给「是否已提交」。</summary>
    public static string Classify(Exception exception, bool commandSubmitted) =>
        Classify(exception, new SshFailureFacts(SshPhase.Validation, commandSubmitted)).ErrorCode;

    public static SshFailure Classify(Exception exception, SshFailureFacts facts)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(facts);

        var code = ResolveCode(exception, facts);
        return new SshFailure(code, facts.Phase, IsRetrySafe(code, facts.CommandSubmitted));
    }

    /// <summary>
    /// 可安全再次尝试只可能发生在**确定未提交**且失败属连通/排队类别时（设计 §9.2）；
    /// 密钥不可信、认证失败、命令已提交后的任何失败都不在此列。
    /// </summary>
    public static bool IsRetrySafe(string errorCode, bool commandSubmitted)
    {
        if (commandSubmitted)
        {
            return false;
        }

        return errorCode is SshErrorCodes.DnsFailed or SshErrorCodes.ConnectFailed or SshErrorCodes.Busy;
    }

    public static bool IsRetrySafe(Exception exception, bool commandSubmitted) =>
        IsRetrySafe(Classify(exception, commandSubmitted), commandSubmitted);

    private static string ResolveCode(Exception exception, SshFailureFacts facts)
    {
        switch (exception)
        {
            case SshComponentException component:
                return component.ErrorCode;

            case System.OperationCanceledException:
                return SshErrorCodes.Cancelled;

            case Renci.SshNet.Common.SshPassPhraseNullOrEmptyException:
                return SshErrorCodes.PassphraseRequired;

            case Renci.SshNet.Common.SshAuthenticationException:
                return SshErrorCodes.AuthenticationFailed;

            case Renci.SshNet.Common.SshOperationTimeoutException:
            case TimeoutException:
                return SshErrorCodes.Timeout;

            case Renci.SshNet.Common.SshConnectionException:
                return facts.CommandSubmitted ? SshErrorCodes.ConnectionLost : SshErrorCodes.ConnectFailed;

            case System.Net.Sockets.SocketException socket:
                return socket.SocketErrorCode is System.Net.Sockets.SocketError.HostNotFound
                    or System.Net.Sockets.SocketError.TryAgain
                    or System.Net.Sockets.SocketError.NoData
                    ? SshErrorCodes.DnsFailed
                    : SshErrorCodes.ConnectFailed;

            case Renci.SshNet.Common.SshException:
                return facts.CommandSubmitted ? SshErrorCodes.ConnectionLost : SshErrorCodes.TransportFault;

            case ObjectDisposedException:
                return SshErrorCodes.TransportFault;

            // 凭据阶段的读/解析失败 = 选定私钥不可用；其他阶段的 IO 属传输/连接问题。
            // 注意：FileNotFoundException 派生自 IOException，不单列（单列即不可达 case）。
            case System.IO.IOException:
            case UnauthorizedAccessException:
            case FormatException:
            case ArgumentException:
                return facts.Phase == SshPhase.Credentials
                    ? SshErrorCodes.CredentialUnavailable
                    : facts.CommandSubmitted ? SshErrorCodes.ConnectionLost : SshErrorCodes.TransportFault;

            default:
                return SshErrorCodes.TransportFault;
        }
    }
}
