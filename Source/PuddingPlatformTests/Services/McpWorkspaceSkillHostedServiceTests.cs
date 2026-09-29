using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Services.Mcp;

namespace PuddingPlatformTests.Services;

/// <summary>
/// MCP 是外部集成：首次对账实测 2.15 秒（服务器不可达时更久），因此**不得**阻塞宿主启动。
/// 这些用例固定三条约束：StartAsync 不等待、停止会把取消送到在途对账、对账不收敛时停止仍有上界。
/// </summary>
[TestClass]
public sealed class McpWorkspaceSkillHostedServiceTests
{
    [TestMethod]
    public async Task StartDoesNotWaitForExternalReconciliation()
    {
        var manager = new CancelAwareManager();
        var service = new McpWorkspaceSkillHostedService(manager, NullLogger<McpWorkspaceSkillHostedService>.Instance);

        // 判据用"内部任务是否仍未完成"，不用墙钟阈值：后者在负载下会抖。
        // 先等它确实被调度（Task.Run 不保证 StartAsync 返回时就已启动），再断言"尚未完成"。
        await service.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await manager.StartedSignal.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsFalse(manager.Finished, "对账尚未完成时 StartAsync 就必须已经返回");

        await service.StopAsync(CancellationToken.None);
        Assert.IsTrue(manager.Cancelled, "停止必须把取消送到在途对账");
    }

    [TestMethod]
    public async Task StopConvergesWithinItsBoundEvenIfReconciliationNeverFinishes()
    {
        var manager = new NeverFinishingManager();
        var service = new McpWorkspaceSkillHostedService(manager, NullLogger<McpWorkspaceSkillHostedService>.Instance)
        {
            StopConvergenceTimeout = TimeSpan.FromMilliseconds(200),
        };
        await service.StartAsync(CancellationToken.None);

        var startedAt = Stopwatch.GetTimestamp();
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        Assert.IsTrue(
            elapsed < TimeSpan.FromSeconds(3),
            $"对账不收敛时停止仍必须有上界，实测 {elapsed.TotalMilliseconds:N0} ms");
    }

    [TestMethod]
    public async Task StopIsIdempotent()
    {
        var service = new McpWorkspaceSkillHostedService(
            new CancelAwareManager(), NullLogger<McpWorkspaceSkillHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    private sealed class CancelAwareManager : IMcpConnectionManager
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _finished;
        public bool Cancelled { get; private set; }

        /// <summary>对账已开始执行的信号（Task.Run 不保证 StartAsync 返回时就已启动）。</summary>
        public Task StartedSignal => _started.Task;

        /// <summary>对账是否已结束；用确定性判据替代墙钟阈值。</summary>
        public bool Finished => _finished;

        public async Task RefreshAllAsync(CancellationToken ct = default)
        {
            _started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            finally { _finished = true; }
        }

        public Task RefreshWorkspaceAsync(string workspaceId, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<McpServerRuntimeStatus> ListStatuses(string workspaceId) => [];
    }

    private sealed class NeverFinishingManager : IMcpConnectionManager
    {
        public Task RefreshAllAsync(CancellationToken ct = default)
            => new TaskCompletionSource().Task;   // 无视取消：用来验证停止的上界
        public Task RefreshWorkspaceAsync(string workspaceId, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<McpServerRuntimeStatus> ListStatuses(string workspaceId) => [];
    }
}
