using System.Diagnostics;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Execution;

namespace PuddingSshTests;

/// <summary>
/// L1 回归（ADR-096 D3 补充约束）：**原子容量**与**完整资源记账**。
/// <para>
/// 对应独立复核 F8/F9：
/// <list type="bullet">
///   <item>F9：隔离区容量不是硬上限——已排队请求仍能越限进入，capacity=1 时 pending 可达 4。</item>
///   <item>F8：未结束的 exec 脱离记账——业务已返回但 exec 仍挂起时，所有资源计数为 0，Stop 错报已收敛。</item>
/// </list>
/// 本轮先把复核证据转成失败断言（取红），再实现原子令牌与任务集合记账（复绿）。
/// </para>
/// </summary>
[TestClass]
public sealed class SshResourceAccountingTests
{
    private static async Task<T> WaitOrFailAsync<T>(Task<T> operation, int timeoutMs = 5000)
    {
        var winner = await Task.WhenAny(operation, Task.Delay(timeoutMs));
        Assert.AreSame(operation, winner, $"业务调用在 {timeoutMs} ms 内未返回");
        return await operation;
    }

    // ---------- F9：隔离区容量是硬上限，排队请求不得越限 ----------

    [TestMethod]
    public async Task QuarantineCapacity_IsHardLimit_EvenForAlreadyQueuedRequests()
    {
        var factory = new FakeTransportFactory
        {
            // 每个连接都返回一个「释放永不结束」的会话：清理必然进隔离区。
            OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(new FakeSession { DisposeNeverCompletes = true }),
        };
        await using var coordinator = new SshOperationCoordinator(
            factory,
            new FakeIdentityProvider(),
            new SshCoordinatorOptions
            {
                CoreMaxConcurrentOperations = 1,
                MaxWaitQueueLength = 32,
                MaxQuarantinedResources = 1,
                CleanupGrace = TimeSpan.FromMilliseconds(60),
            });

        var host = FakeSsh.Host(maxConcurrentOperations: 1);
        var peakQuarantine = 0;

        // 1 个执行中 + 3 个已排队（全部在隔离区为空时被受理）。
        var operations = new List<Task<SshExecuteResult>>();
        for (var i = 0; i < 4; i++)
        {
            operations.Add(coordinator.ExecuteAsync(host, FakeSsh.Execute(), CancellationToken.None));
            peakQuarantine = Math.Max(peakQuarantine, coordinator.QuarantinedResources);
        }

        var results = await WaitOrFailAsync(Task.WhenAll(operations), 10000);
        peakQuarantine = Math.Max(peakQuarantine, coordinator.QuarantinedResources);

        Assert.IsLessThanOrEqualTo(
            coordinator.QuarantineCapacity,
            coordinator.QuarantinedResources,
            $"隔离区 pending 必须恒 ≤ capacity（peak={peakQuarantine}）");
        Assert.IsLessThanOrEqualTo(
            coordinator.QuarantineCapacity,
            coordinator.PeakQuarantinedResources,
            "隔离区历史峰值也不得越限");
        Assert.AreEqual(1, factory.ConnectCount, "隔离区填满后，已排队请求不得再开启新连接");
        Assert.IsTrue(
            results.Count(r => r.ErrorCode == SshErrorCodes.Busy) >= 1,
            "无法预留隔离令牌的后续请求应以 ssh.busy 拒绝，而不是越限进入");
        Assert.IsTrue(
            results[0].CleanupState == SshCleanupState.Pending,
            "第一个请求的清理未结束，必须如实报告 Pending");
    }

    [TestMethod]
    public async Task QuarantineCapacityZero_DisablesTransfer_ButKeepsNormalRequestsWorking()
    {
        var factory = new FakeTransportFactory
        {
            OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(new FakeSession()),
        };
        await using var coordinator = new SshOperationCoordinator(
            factory,
            new FakeIdentityProvider(),
            new SshCoordinatorOptions
            {
                MaxQuarantinedResources = 0,
                CleanupGrace = TimeSpan.FromMilliseconds(60),
            });

        var result = await WaitOrFailAsync(coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None));

        // capacity=0 的语义是「禁用转移、保留原槽」，不能悄悄把正常请求全部拒掉。
        Assert.AreEqual(SshExecutionStatus.Completed, result.Status, result.ErrorCode ?? "null");
        Assert.AreEqual(0, coordinator.QuarantinedResources);
    }

    // ---------- F8：未结束的 exec 必须继续占账，Stop 如实报告 ----------

    [TestMethod]
    public async Task OrphanExec_StaysAccountedAfterBusinessReturn_AndStopReportsPending()
    {
        var execGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            // 忽略取消：业务等待必须靠预算返回，任务本身仍在跑。
            OnExecute = async (_, _, _, _) =>
            {
                await execGate.Task.ConfigureAwait(false);
                return FakeResults.Completed();
            },
        };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };

        await using var coordinator = new SshOperationCoordinator(
            factory,
            new FakeIdentityProvider(),
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(80) });

        using var cts = new CancellationTokenSource();
        var operation = coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), cts.Token);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => session.ExecuteCount == 1));
        await Task.Delay(80);
        await cts.CancelAsync();

        var stopwatch = Stopwatch.StartNew();
        var result = await WaitOrFailAsync(operation);
        stopwatch.Stop();
        Assert.IsLessThan(3000, (int)stopwatch.ElapsedMilliseconds, "业务等待必须有界");

        // 业务已返回，但 exec 仍在跑：资源必须继续占账，且 Stop 如实报告。
        Assert.IsFalse(execGate.Task.IsCompleted, "用例前提：exec 仍未结束");
        Assert.IsTrue(
            coordinator.PendingResourceLeases > 0,
            "业务已返回但 exec 未结束时，必须有未完成资源租约计数");
        Assert.IsTrue(
            coordinator.QuarantinedResources + coordinator.ActiveStageWorkers > 0,
            "未结束的 exec 必须留在某种记账里（隔离区或 worker）");

        var report = await WaitOrFailAsync(coordinator.StopAsync());
        Assert.IsTrue(
            report.PendingOperations + report.PendingCleanups + report.PendingResources > 0,
            "StopAsync 必须如实报告仍未结束的资源，不能宣称全部收敛");

        // 交还 gate：资源真正结束后计数归零。
        execGate.SetResult();
        Assert.IsTrue(
            await FakeSsh.WaitUntilAsync(
                () => coordinator.PendingResourceLeases == 0
                    && coordinator.QuarantinedResources == 0
                    && coordinator.ActiveStageWorkers == 0,
                8000),
            "资源真正结束后所有计数必须归零");
        Assert.AreEqual(SshExecutionStatus.Cancelled, result.Status);
    }

    // ---------- L1：原子令牌（并发 barrier 峰值）与关闭容量 ----------

    [TestMethod]
    public async Task StageWorkerPool_ConcurrentBarrier_PeakNeverExceedsCapacity()
    {
        var pool = new SshStageWorkerPool(3);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = 0;
        var rejected = 0;
        var tasks = new List<Task>();

        for (var i = 0; i < 24; i++)
        {
            try
            {
                tasks.Add(pool.StartAsync(async () =>
                {
                    Interlocked.Increment(ref admitted);
                    await gate.Task.ConfigureAwait(false);
                    return 1;
                }));
            }
            catch (SshStageCapacityExceededException)
            {
                rejected++;
            }
        }

        Assert.AreEqual(3, pool.Active, "并发 barrier：只允许 3 个令牌被取得");
        Assert.IsLessThanOrEqualTo(pool.Capacity, pool.Peak, "峰值不得超过容量");
        Assert.AreEqual(3, pool.Peak);
        Assert.AreEqual(21, rejected, "取不到令牌的请求必须立即被拒绝（不是排队）");
        Assert.AreEqual(21, pool.Rejected, "池自身的拒绝计数与调用方观察一致");

        gate.SetResult();
        await Task.WhenAll(tasks);
        Assert.AreEqual(3, admitted);
        Assert.AreEqual(0, pool.Active);

        // 令牌在真实工作结束后归还：池可以再次接纳。
        var again = await pool.StartAsync(() => Task.FromResult(7));
        Assert.AreEqual(7, again);
    }

    [TestMethod]
    public async Task CloseCapacityExhausted_RefusesNewResourceCreation()
    {
        var factory = new FakeTransportFactory
        {
            OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(new FakeSession { DisposeNeverCompletes = true }),
        };
        await using var coordinator = new SshOperationCoordinator(
            factory,
            new FakeIdentityProvider(),
            new SshCoordinatorOptions
            {
                MaxCloseWorkers = 1,
                MaxQuarantinedResources = 8,
                CleanupGrace = TimeSpan.FromMilliseconds(60),
            });

        var first = await WaitOrFailAsync(coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None));
        Assert.AreEqual(SshExecutionStatus.Completed, first.Status, first.ErrorCode ?? "null");
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => coordinator.ActiveCloseWorkers == 1), "关闭工作必须占住独立关闭池");

        var second = await WaitOrFailAsync(coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None));
        Assert.AreEqual(SshErrorCodes.Busy, second.ErrorCode, "关闭容量耗尽时必须拒绝新的资源创建");
        Assert.AreEqual(1, factory.ConnectCount, "拒绝发生在建连之前");
    }
    [TestMethod]
    public async Task OrphanExec_LateFault_IsObserved_AndCountersReturnToZero()
    {
        var execGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            OnExecute = async (_, _, _, _) =>
            {
                await execGate.Task.ConfigureAwait(false);
                throw new IOException("late exec fault");
            },
        };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };

        await using var coordinator = new SshOperationCoordinator(
            factory,
            new FakeIdentityProvider(),
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(80) });

        using var cts = new CancellationTokenSource();
        var operation = coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), cts.Token);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => session.ExecuteCount == 1));
        await Task.Delay(80);
        await cts.CancelAsync();
        await WaitOrFailAsync(operation);

        execGate.SetException(new IOException("late exec fault"));

        Assert.IsTrue(
            await FakeSsh.WaitUntilAsync(
                () => coordinator.PendingResourceLeases == 0 && coordinator.ActiveStageWorkers == 0,
                8000),
            "迟到 fault 被观察后计数必须归零（不得泄漏）");
    }
}
