using Pudding.Contracts;

namespace Pudding.DesktopConnection;

/// <summary>
/// 连接监督端口：反复建立/维持一条能力通道，并把状态暴露给宿主。
/// 存在的理由：宿主（DesktopCapabilityHost）只依赖这个端口，因此生命周期、单实例约束与
/// 停止语义可以在<b>不启动任何真实端点</b>的情况下确定性测试；生产实现是
/// <see cref="DesktopConnectionRunner"/>。
/// </summary>
public interface IDesktopConnectionSupervisor
{
    DesktopConnectionState State { get; }

    ConnectionGeneration Generation { get; }

    int AttemptCount { get; }

    DesktopCapabilityError? LastError { get; }

    event Action<DesktopConnectionState>? StateChanged;

    /// <summary>运行到取消为止（内部按退避重连）。</summary>
    Task RunAsync(CancellationToken cancellationToken);

    ValueTask DisposeAsync();
}
