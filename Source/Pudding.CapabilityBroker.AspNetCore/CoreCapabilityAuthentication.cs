using Grpc.Core;
using Pudding.Contracts;
using CapabilityBrokerHost = Pudding.CapabilityBroker.CapabilityBroker;
using Proto = Pudding.Rpc.Protocol.V1;

namespace Pudding.CapabilityBroker.AspNetCore;

/// <summary>
/// 连接级认证（认证先于握手）。产品实现复用既有 ControlToken 校验
/// （<c>DesktopControlTokenValidator</c>：按 DataRoot 的 system.json 常量时间比较）；
/// 本工程只定义接缝，不把宿主的具体校验搬进来。
/// </summary>
public interface ICoreCapabilityAuthenticator
{
    /// <summary>校验请求元数据里的凭据；通过返回 <c>null</c>，否则返回拒绝原因（不含凭据原文）。</summary>
    ValueTask<DesktopCapabilityError?> AuthenticateAsync(
        IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);
}

/// <summary>默认认证器：<b>拒绝一切</b>（未装配认证的宿主不得开放能力通道）。</summary>
public sealed class RejectAllCapabilityAuthenticator : ICoreCapabilityAuthenticator
{
    public static readonly RejectAllCapabilityAuthenticator Instance = new();

    private RejectAllCapabilityAuthenticator()
    {
    }

    public ValueTask<DesktopCapabilityError?> AuthenticateAsync(
        IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken) =>
        ValueTask.FromResult<DesktopCapabilityError?>(
            DesktopCapabilityError.Unauthorized("no capability authenticator is configured"));
}

/// <summary>静态 Header 认证器：用于测试与受控探针（生产必须换成真实校验）。</summary>
public sealed class StaticHeaderCapabilityAuthenticator : ICoreCapabilityAuthenticator
{
    private readonly string _headerName;
    private readonly string _expectedValue;

    public StaticHeaderCapabilityAuthenticator(string headerName, string expectedValue)
    {
        _headerName = string.IsNullOrWhiteSpace(headerName)
            ? throw new ArgumentException("Header name is required.", nameof(headerName))
            : headerName;
        _expectedValue = string.IsNullOrEmpty(expectedValue)
            ? throw new ArgumentException("Expected value is required.", nameof(expectedValue))
            : expectedValue;
    }

    public ValueTask<DesktopCapabilityError?> AuthenticateAsync(
        IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var presented = headers.TryGetValue(_headerName, out var value) ? value : null;

        return ValueTask.FromResult<DesktopCapabilityError?>(
            CryptographicEquals(presented, _expectedValue)
                ? null
                : DesktopCapabilityError.Unauthorized("capability credentials were rejected"));
    }

    private static bool CryptographicEquals(string? presented, string expected)
    {
        if (presented is null)
        {
            return false;
        }

        var presentedBytes = System.Text.Encoding.UTF8.GetBytes(presented);
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        return presentedBytes.Length == expectedBytes.Length
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes);
    }
}

/// <summary>
/// 服务端单向流适配：把 ASP.NET Core 的服务端流接到 <see cref="ICoreDesktopChannel"/>。
/// <c>IServerStreamWriter</c> 不支持并发写，因此这里串行化发送。
/// </summary>
internal sealed class ServerStreamDesktopChannel : ICoreDesktopChannel
{
    private readonly IAsyncStreamReader<Proto.DesktopFrame> _requests;
    private readonly IServerStreamWriter<Proto.CoreFrame> _responses;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public ServerStreamDesktopChannel(
        IAsyncStreamReader<Proto.DesktopFrame> requests,
        IServerStreamWriter<Proto.CoreFrame> responses)
    {
        _requests = requests;
        _responses = responses;
    }

    public async ValueTask SendAsync(Proto.CoreFrame frame, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _responses.WriteAsync(frame).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<Proto.DesktopFrame?> ReadAsync(CancellationToken cancellationToken) =>
        await _requests.MoveNext(cancellationToken).ConfigureAwait(false) ? _requests.Current : null;
}
