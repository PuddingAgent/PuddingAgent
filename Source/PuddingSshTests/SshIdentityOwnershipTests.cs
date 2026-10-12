using System.Diagnostics;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Execution;
using Renci.SshNet.Common;

namespace PuddingSshTests;

/// <summary>
/// L2 回归（ADR-096 D3 补充约束）：**身份所有权在交付 connect 之前转移**。
/// <para>
/// 对应独立复核 F7：<c>TransferLateOwnership()</c> 只在迟到回调里置位，业务阶段超时返回时尚未置位，
/// finally 立即清零材料——而迟到的 connect 可能仍在读它。
/// 门禁：用非零合成字节保持 connect 未完成，取消业务后材料仍可读且**逐字节不变**；
/// 迟到成功或失败后恰好清零一次且零 exec；两个入口（Test/Execute）都要覆盖。
/// </para>
/// </summary>
[TestClass]
public sealed class SshIdentityOwnershipTests
{
    private static readonly byte[] SyntheticKey = [0x11, 0x22, 0x33, 0x44, 0x55];

    private static SshIdentityMaterial SyntheticMaterial() =>
        new("/tmp/synthetic-ssh", "id_synthetic", (byte[])SyntheticKey.Clone());

    private static async Task<T> WaitOrFailAsync<T>(Task<T> operation, int timeoutMs = 5000)
    {
        var winner = await Task.WhenAny(operation, Task.Delay(timeoutMs));
        Assert.AreSame(operation, winner, $"业务调用在 {timeoutMs} ms 内未返回");
        return await operation;
    }

    private static byte[] ReadMaterial(SshIdentityMaterial material)
    {
        using var stream = material.OpenKeyStream();
        var buffer = new byte[stream.Length];
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = stream.Read(buffer, read, buffer.Length - read);
            if (chunk <= 0)
            {
                break;
            }

            read += chunk;
        }

        return buffer;
    }

    /// <summary>用例前提：材料仍可读且逐字节等于合成密钥。</summary>
    private static void AssertMaterialIntact(SshIdentityMaterial material)
    {
        try
        {
            CollectionAssert.AreEqual(SyntheticKey, ReadMaterial(material), "connect 未结束前材料必须逐字节不变");
        }
        catch (ObjectDisposedException)
        {
            Assert.Fail("connect 仍在进行时材料已被提前清零（F7 回归）");
        }
    }

    private static void AssertMaterialZeroed(SshIdentityMaterial material)
    {
        Assert.ThrowsExactly<ObjectDisposedException>(() => material.OpenKeyStream());
        Assert.AreEqual(1, material.DisposeCount, "清零效果必须恰好发生一次");
    }

    // ---------- Execute 入口 ----------

    [TestMethod]
    public async Task LateConnect_PendingConnectKeepsMaterialReadable_ThenLateSuccessZeroesOnce()
    {
        var connectGate = new TaskCompletionSource<ISshTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeTransportFactory { OnConnect = (_, _) => connectGate.Task };
        var material = SyntheticMaterial();
        var identities = new FakeIdentityProvider { OnGet = _ => material };

        await using var coordinator = new SshOperationCoordinator(
            factory,
            identities,
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(80) });

        using var cts = new CancellationTokenSource();
        var operation = coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), cts.Token);

        // 等 connect 真正进入「未完成」状态。
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => factory.ConnectCount == 1));
        await Task.Delay(80);
        await cts.CancelAsync();

        var result = await WaitOrFailAsync(operation);
        Assert.AreEqual(SshExecutionStatus.Cancelled, result.Status);

        // 业务已返回，connect 仍挂起：材料必须**仍然有效**（F7 的核心断言）。
        Assert.IsFalse(connectGate.Task.IsCompleted);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => coordinator.ActiveStageWorkers >= 1), "connect 仍占着 worker");
        AssertMaterialIntact(material);

        // 迟到成功：会话被回收、材料清零一次、零 exec。
        var late = new FakeSession();
        connectGate.SetResult(late);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => late.Disposed, 8000), "迟到连接必须被关闭");
        Assert.AreEqual(0, late.ExecuteCount, "迟到连接不得提交命令");
        AssertMaterialZeroed(material);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => coordinator.PendingResourceLeases == 0, 8000));
    }

    [TestMethod]
    public async Task LateConnect_LateFailure_ZeroesMaterialOnce()
    {
        var connectGate = new TaskCompletionSource<ISshTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeTransportFactory { OnConnect = (_, _) => connectGate.Task };
        var material = SyntheticMaterial();
        var identities = new FakeIdentityProvider { OnGet = _ => material };

        await using var coordinator = new SshOperationCoordinator(
            factory,
            identities,
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(80) });

        using var cts = new CancellationTokenSource();
        var operation = coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), cts.Token);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => factory.ConnectCount == 1));
        await Task.Delay(80);
        await cts.CancelAsync();
        await WaitOrFailAsync(operation);

        AssertMaterialIntact(material);

        connectGate.SetException(new SshConnectionException("late connect failure"));
        Assert.IsTrue(
            await FakeSsh.WaitUntilAsync(
                () =>
                {
                    try
                    {
                        material.OpenKeyStream().Dispose();
                        return false;
                    }
                    catch (ObjectDisposedException)
                    {
                        return true;
                    }
                },
                8000),
            "迟到失败后材料必须被清零");
        Assert.AreEqual(1, material.DisposeCount);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => coordinator.PendingResourceLeases == 0, 8000));
    }

    // ---------- Test 入口 ----------

    [TestMethod]
    public async Task LateConnect_TestEntry_KeepsMaterialReadableUntilConnectSettles()
    {
        var connectGate = new TaskCompletionSource<ISshTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeTransportFactory { OnConnect = (_, _) => connectGate.Task };
        var material = SyntheticMaterial();
        var identities = new FakeIdentityProvider { OnGet = _ => material };

        await using var coordinator = new SshOperationCoordinator(
            factory,
            identities,
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(80) });

        using var cts = new CancellationTokenSource();
        var operation = coordinator.TestAsync(FakeSsh.Host(), FakeSsh.Test(), cts.Token);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => factory.ConnectCount == 1));
        await Task.Delay(80);
        await cts.CancelAsync();
        await WaitOrFailAsync(operation);

        AssertMaterialIntact(material);

        connectGate.SetResult(new FakeSession());
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => coordinator.PendingResourceLeases == 0, 8000));
        AssertMaterialZeroed(material);
    }

    // ---------- 迟到会话 Dispose 卡住：材料保持有效、资源如实报告 ----------

    [TestMethod]
    public async Task LateSessionDisposeHangs_KeepsMaterialAndReportsPending()
    {
        var connectGate = new TaskCompletionSource<ISshTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeTransportFactory { OnConnect = (_, _) => connectGate.Task };
        var material = SyntheticMaterial();
        var identities = new FakeIdentityProvider { OnGet = _ => material };

        await using var coordinator = new SshOperationCoordinator(
            factory,
            identities,
            new SshCoordinatorOptions
            {
                CleanupGrace = TimeSpan.FromMilliseconds(60),
                MaxQuarantinedResources = 4,
            });

        using var cts = new CancellationTokenSource();
        var operation = coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), cts.Token);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => factory.ConnectCount == 1));
        await Task.Delay(80);
        await cts.CancelAsync();
        await WaitOrFailAsync(operation);
        AssertMaterialIntact(material);

        // 业务已返回、connect 仍未结束：资源必须**立刻**已经占账（L1/F8 的核心断言）。
        Assert.IsFalse(connectGate.Task.IsCompleted);
        Assert.IsTrue(HasPendingResource(coordinator), PendingDetail(coordinator, "业务返回后 connect 未结束"));

        var late = new FakeSession { DisposeNeverCompletes = true };
        connectGate.SetResult(late);

        // 迟到关闭卡住：资源继续占账，材料**不得**在会话仍可能使用它时清零。
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => late.Disposed, 8000), "迟到会话必须进入关闭流程");
        Assert.IsTrue(
            await FakeSsh.WaitUntilAsync(() => HasPendingResource(coordinator), 8000),
            PendingDetail(coordinator, "迟到关闭卡住"));
        var beforeStop = PendingDetail(coordinator, "stop 之前");
        var report = await WaitOrFailAsync(coordinator.StopAsync());
        Assert.IsGreaterThan(
            0,
            report.PendingResources + report.PendingCleanups,
            $"Stop 必须如实报告仍未结束的迟到资源｜{beforeStop}｜report=(ops={report.PendingOperations},cleanups={report.PendingCleanups},resources={report.PendingResources},timedOut={report.TimedOut})");
        AssertMaterialIntact(material);
    }

    private static bool HasPendingResource(SshOperationCoordinator coordinator) =>
        coordinator.PendingResourceLeases > 0
        || coordinator.QuarantinedResources > 0
        || coordinator.ActiveCloseWorkers > 0
        || coordinator.ActiveStageWorkers > 0;

    private static string PendingDetail(SshOperationCoordinator coordinator, string stage) =>
        $"{stage}：pending={coordinator.PendingResourceLeases} quarantine={coordinator.QuarantinedResources} "
        + $"workers={coordinator.ActiveStageWorkers} close={coordinator.ActiveCloseWorkers}";

    // ---------- 并发：及时路径与迟到回调竞争也只有一个释放者 ----------

    [TestMethod]
    public async Task LateConnect_RacingWithBusinessReturn_ReleasesIdentityExactlyOnce()
    {
        for (var round = 0; round < 8; round++)
        {
            var connectGate = new TaskCompletionSource<ISshTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new FakeTransportFactory { OnConnect = (_, _) => connectGate.Task };
            var material = SyntheticMaterial();
            var identities = new FakeIdentityProvider { OnGet = _ => material };

            await using var coordinator = new SshOperationCoordinator(
                factory,
                identities,
                new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(30) });

            using var cts = new CancellationTokenSource();
            var operation = coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), cts.Token);
            Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => factory.ConnectCount == 1));

            // 让 deadline 与迟到交还尽可能靠近：先取消，再立刻交还连接。
            await cts.CancelAsync();
            connectGate.SetResult(new FakeSession());

            await WaitOrFailAsync(operation);
            Assert.IsTrue(
                await FakeSsh.WaitUntilAsync(() => coordinator.PendingResourceLeases == 0, 8000),
                $"第 {round + 1} 轮：资源必须收敛");
            Assert.AreEqual(1, material.DisposeCount, $"第 {round + 1} 轮：身份只能清零一次");
        }
    }

    [TestMethod]
    public async Task LateConnect_NeverExecutesMarkerCommand()
    {
        var connectGate = new TaskCompletionSource<ISshTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeTransportFactory { OnConnect = (_, _) => connectGate.Task };
        var material = SyntheticMaterial();
        var identities = new FakeIdentityProvider { OnGet = _ => material };

        await using var coordinator = new SshOperationCoordinator(
            factory,
            identities,
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(30) });

        var stopwatch = Stopwatch.StartNew();
        var result = await WaitOrFailAsync(
            coordinator.ExecuteAsync(
                FakeSsh.Host(deadline: DateTimeOffset.UtcNow.AddMilliseconds(60)),
                FakeSsh.Execute(),
                CancellationToken.None));
        stopwatch.Stop();

        Assert.AreEqual(SshErrorCodes.Timeout, result.ErrorCode);
        var late = new FakeSession();
        connectGate.SetResult(late);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => late.Disposed, 8000));
        Assert.AreEqual(0, late.ExecuteCount, "deadline 之后交还的连接绝不能执行命令");
        Assert.IsLessThan(3000, (int)stopwatch.ElapsedMilliseconds);
    }
}
