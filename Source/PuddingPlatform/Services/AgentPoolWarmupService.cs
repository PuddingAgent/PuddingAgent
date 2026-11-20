using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;

namespace PuddingPlatform.Services;

/// <summary>
/// 启动预热：把持久化的池条目加载成内存视图（持久子代理池 S4 的「就绪门」）。
/// <para>
/// <b>为什么需要它：</b>池从「纯内存字典」变成「从库重建的视图」之后，进程刚起来时池是空的，
/// 而不是「确实没有成员」。在这两种状态之间不加区分，GC 会把池化目录当成孤儿隔离
/// （详见 <see cref="PoolNotReadyException"/>）。预热把加载挪到启动期，让第一次真正的调用
/// 不必自己等一次库。
/// </para>
/// <para>
/// <b>为什么失败不让宿主启动失败：</b>池不可用只影响池化能力（工具层会明确报
/// <see cref="PoolNotReadyException"/> 并带上原因），不该把整个 Core 拖下水。
/// 因此这里只记 LogError，池会停在 Failed 状态——失败可见，但不会退化成「空池」。
/// </para>
/// </summary>
public sealed class AgentPoolWarmupService(
    ISubAgentPool subAgentPool,
    ILogger<AgentPoolWarmupService> logger) : IHostedService
{
    /// <summary>
    /// 触发加载并等待就绪。
    /// <para>
    /// 用 <see cref="ISubAgentPool.GetSnapshotAsync"/> 而不是直接调实现类：
    /// 「会等待/触发加载」的语义在接口上就是这一个入口，预热与首次取快照走同一条路径，
    /// 不会出现「预热调用的加载」与「调用方触发的加载」两套行为。
    /// </para>
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await subAgentPool.GetSnapshotAsync(cancellationToken);
            logger.LogInformation(
                "[AgentPoolWarmup] Pool is ready with {Count} entries", snapshot.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 宿主在启动中途被取消（重启/关停）：不是池的故障，池会停在 NotStarted，
            // 后续任何 List() 都会明确报错而不是返回空集合。
            logger.LogInformation("[AgentPoolWarmup] Warmup cancelled during startup");
        }
        catch (Exception ex)
        {
            // 池停在 Failed；List() 会抛 PoolNotReadyException（带原始异常），GC 会因此跳过扫描。
            logger.LogError(
                ex,
                "[AgentPoolWarmup] Pool warmup failed; pool stays Failed and reads will fail explicitly");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
