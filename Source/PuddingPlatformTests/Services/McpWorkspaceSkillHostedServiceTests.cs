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

        var startedAt = Stopwatch.GetTimestamp();
        await service.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        Assert.IsTrue(
            elapsed < TimeSpan.FromSeconds(1),
            $"StartAsync 不应等待外部对账，实测 {elapsed.TotalMilliseconds:N0} ms");
        Assert.AreEqual(1, manager.Started, "对账应在后台被启动恰好一次");

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
        public int Started { get; private set; }
        public bool Cancelled { get; private set; }

        public async Task RefreshAllAsync(CancellationToken ct = default)
        {
            Started++;
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
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
