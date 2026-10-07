using System.Net.Http;
using System.Net.Sockets;

namespace PuddingCode.Diagnostics;

/// <summary>
/// 异常链检视（可诊断基础设施设计 §5.3）。
/// <para>
/// 只读 BCL 异常对象：把内因链、socket 错误码、<c>HttpIOException</c> 的错误种类与
/// **堆栈帧指纹**取出来。堆栈是唯一能区分「写请求体」与「读响应」的客户端信号
/// （.NET 把 <c>HttpContent.CopyToAsync</c> 的失败包装成
/// <c>HttpRequestException("Error while copying content to a stream.")</c>），
/// 因此它是证据的一部分，而不是猜测。
/// </para>
/// </summary>
public static class ExceptionChainInspector
{
    public const int MaxDepth = 8;

    /// <summary>
    /// 「正在写出请求体」的框架帧指纹。取值来自 2026-10-07 事故的真实堆栈
    /// （<c>System.Net.Http.HttpContent.&lt;CopyToAsync&gt;g__WaitAsync</c>、
    /// <c>System.Net.Http.HttpConnection.SendRequestContentAsync</c>）。
    /// <para>
    /// 公开只读是刻意的：这些指纹是**经验知识**，必须能有断言锁住它们不被静默删掉
    /// （见 PuddingDiagnosticsTests 的指纹锚定用例）。
    /// </para>
    /// </summary>
    public static readonly string[] DefaultUploadFrames =
    [
        "SendRequestContentAsync",
        "HttpContent.<CopyToAsync>",
        "HttpContent.CopyToAsync",
    ];

    private static readonly int[] DnsErrorCodes = [11001, 11002, 11003, 11004];
    private static readonly int[] ConnectErrorCodes = [10050, 10051, 10060, 10061, 10064, 10065];
    private static readonly int[] ResetErrorCodes = [10052, 10053, 10054, 104, 10058];

    /// <summary>展开内因链（含自身），最多 <see cref="MaxDepth"/> 层，按引用去重防环。</summary>
    public static IReadOnlyList<Exception> Flatten(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var chain = new List<Exception>(MaxDepth);
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var current = exception;

        while (current is not null && chain.Count < MaxDepth && seen.Add(current))
        {
            chain.Add(current);
            current = current.InnerException;
        }

        return chain;
    }

    /// <summary>把链路压成一行证据（不截断由证据构建器统一负责）。</summary>
    public static string Describe(IReadOnlyList<Exception> chain)
    {
        var parts = new List<string>(chain.Count);
        foreach (var exception in chain)
        {
            var socketCode = exception is SocketException socket ? $"({socket.ErrorCode})" : string.Empty;
            parts.Add($"{exception.GetType().Name}{socketCode}: {exception.Message}");
        }

        return string.Join(" <- ", parts);
    }

    /// <summary>链路中是否含某类型（等价于 <c>ex is T</c> 的链式版本）。</summary>
    public static bool Contains<T>(IReadOnlyList<Exception> chain)
        where T : Exception
        => chain.Any(exception => exception is T);

    public static bool ContainsSocketError(IReadOnlyList<Exception> chain)
        => Contains<SocketException>(chain);

    /// <summary>取链路中第一个 <see cref="SocketException"/> 的 Windows 错误码。</summary>
    public static int? SocketErrorCode(IReadOnlyList<Exception> chain)
        => chain.OfType<SocketException>().Select(exception => (int?)exception.ErrorCode).FirstOrDefault();

    /// <summary>取链路中第一个 <c>HttpIOException</c> 的错误种类名（例如 ResponseEnded）。</summary>
    public static string? HttpRequestErrorName(IReadOnlyList<Exception> chain)
        => chain.OfType<HttpIOException>().Select(exception => exception.HttpRequestError.ToString()).FirstOrDefault();

    public static bool IsDnsFailure(int? socketErrorCode)
        => socketErrorCode is { } code && DnsErrorCodes.Contains(code);

    public static bool IsConnectFailure(int? socketErrorCode)
        => socketErrorCode is { } code && ConnectErrorCodes.Contains(code);

    public static bool IsConnectionReset(int? socketErrorCode)
        => socketErrorCode is { } code && ResetErrorCodes.Contains(code);

    /// <summary>链路中是否有「正在写出请求体」的框架帧；返回命中的帧名（用于证据）。</summary>
    public static string? FindUploadFrame(IReadOnlyList<Exception> chain)
        => FindUploadFrame(chain, DefaultUploadFrames);

    /// <summary>
    /// 可注入指纹版本：生产用 <see cref="DefaultUploadFrames"/>，测试用自定义帧名，
    /// 这样「帧指纹判定」这一机制本身是可取红验证的，而不是只能靠读代码相信。
    /// </summary>
    public static string? FindUploadFrame(IReadOnlyList<Exception> chain, IReadOnlyList<string> uploadFrames)
    {
        ArgumentNullException.ThrowIfNull(uploadFrames);

        foreach (var exception in chain)
        {
            var stack = exception.StackTrace;
            if (string.IsNullOrEmpty(stack))
                continue;

            foreach (var frame in uploadFrames)
            {
                if (stack.Contains(frame, StringComparison.Ordinal))
                    return frame;
            }
        }

        return null;
    }
}
