using Pudding.Contracts;
using Proto = Pudding.Rpc.Protocol.V1;

namespace Pudding.DesktopConnection;

/// <summary>连接状态。<see cref="Disconnected"/> 是正常结束，<see cref="Faulted"/> 表示协议/传输/存活故障。</summary>
public enum DesktopConnectionState
{
    Idle,
    Connecting,
    Handshaking,
    Ready,
    Disconnected,
    Faulted,
}

/// <summary>一次连接（单流生命周期）的结局。</summary>
public sealed record DesktopConnectionOutcome(
    DesktopConnectionState FinalState,
    ConnectionGeneration Generation,
    DesktopCapabilityError? Error)
{
    public bool IsFaulted => FinalState == DesktopConnectionState.Faulted;
}

/// <summary>
/// 一条双向流的抽象。存在的理由：让连接状态机（关联、取消、背压、世代）可以<b>不启动 gRPC 服务端</b>
/// 就被确定性测试 —— 生产实现是 <see cref="GrpcDesktopChannelStreamFactory"/>。
/// </summary>
public abstract class DesktopChannelStream : IAsyncDisposable
{
    /// <summary>读取下一帧；对端正常结束（half-close）返回 <c>null</c>。</summary>
    public abstract ValueTask<Proto.CoreFrame?> ReadAsync(CancellationToken cancellationToken);

    public abstract ValueTask WriteAsync(Proto.DesktopFrame frame, CancellationToken cancellationToken);

    /// <summary>半关闭请求流（表示不再发送帧）。</summary>
    public abstract ValueTask CompleteRequestStreamAsync();

    public abstract ValueTask DisposeAsync();
}

public interface IDesktopChannelStreamFactory
{
    ValueTask<DesktopChannelStream> OpenAsync(CancellationToken cancellationToken);
}
