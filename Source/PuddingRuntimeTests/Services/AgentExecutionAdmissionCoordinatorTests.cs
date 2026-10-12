using PuddingRuntime.Services.Messaging;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class AgentExecutionAdmissionCoordinatorTests
{
    [TestMethod]
    public void AcquireForeground_PreemptsBackgroundAndBlocksReplacementUntilRelease()
    {
        var coordinator = new AgentExecutionAdmissionCoordinator();
        using var backgroundCts = new CancellationTokenSource();
        using var background = coordinator.TryRegisterBackground("default", "agent-1", backgroundCts);

        Assert.IsNotNull(background);
        Assert.IsTrue(coordinator.CanStartBackground("default", "agent-2"));

        using (coordinator.AcquireForeground("default", "agent-1"))
        {
            Assert.IsTrue(backgroundCts.IsCancellationRequested);
            Assert.IsTrue(background!.WasPreempted);
            Assert.IsTrue(coordinator.HasForegroundDemand("default", "agent-1"));
            Assert.IsFalse(coordinator.CanStartBackground("default", "agent-1"));
        }

        background.Dispose();
        Assert.IsFalse(coordinator.HasForegroundDemand("default", "agent-1"));
        Assert.IsTrue(coordinator.CanStartBackground("default", "agent-1"));
    }

    [TestMethod]
    public void ReserveForeground_BlocksBackgroundUntilCanonicalTurnConsumesReservation()
    {
        var coordinator = new AgentExecutionAdmissionCoordinator();

        coordinator.ReserveForeground("default", "agent-1", TimeSpan.FromMinutes(1));

        Assert.IsTrue(coordinator.HasForegroundDemand("default", "agent-1"));
        using var backgroundCts = new CancellationTokenSource();
        Assert.IsNull(coordinator.TryRegisterBackground("default", "agent-1", backgroundCts));

        using (coordinator.AcquireForeground("default", "agent-1"))
            Assert.IsTrue(coordinator.HasForegroundDemand("default", "agent-1"));

        Assert.IsFalse(coordinator.HasForegroundDemand("default", "agent-1"));
    }

    // ── R2：会话键的前后台准入（后台可选维护与下一条会话消息的直接冲突面） ──────────

    /// <summary>会话级后台注册：前台预留时立刻被抢占；抢占后 CTS 已取消。</summary>
    [TestMethod]
    public void SessionBackground_IsPreemptedBySessionForegroundReservation()
    {
        var coordinator = new AgentExecutionAdmissionCoordinator();
        using var maintenanceCts = new CancellationTokenSource();
        using var background = coordinator.TryRegisterSessionBackground(
            "default", "session-1", maintenanceCts);

        Assert.IsNotNull(background, "无前台需求时必须能注册会话级后台工作");
        Assert.IsFalse(coordinator.HasSessionForegroundDemand("default", "session-1"));

        coordinator.ReserveSessionForeground("default", "session-1", TimeSpan.FromMinutes(1));

        Assert.IsTrue(maintenanceCts.IsCancellationRequested, "新消息受理必须立刻取消同会话后台工作");
        Assert.IsTrue(background!.WasPreempted);
        Assert.IsTrue(coordinator.HasSessionForegroundDemand("default", "session-1"));
        Assert.IsNull(
            coordinator.TryRegisterSessionBackground("default", "session-1", new CancellationTokenSource()),
            "有前台需求时不得再注册会话级后台工作");
    }

    /// <summary>会话级前台租约同样抢占；释放后前台需求消失、可再次注册后台。</summary>
    [TestMethod]
    public void SessionForegroundLease_PreemptsAndBlocksUntilReleased()
    {
        var coordinator = new AgentExecutionAdmissionCoordinator();
        var firstCts = new CancellationTokenSource();
        var first = coordinator.TryRegisterSessionBackground("default", "session-1", firstCts);
        Assert.IsNotNull(first);

        using (coordinator.AcquireSessionForeground("default", "session-1"))
        {
            Assert.IsTrue(firstCts.IsCancellationRequested);
            Assert.IsTrue(coordinator.HasSessionForegroundDemand("default", "session-1"));
        }

        Assert.IsFalse(coordinator.HasSessionForegroundDemand("default", "session-1"));

        // 被抢占的后台工作在退出时释放自己的租约（生产路径：维护任务 finally 里 Dispose）。
        first!.Dispose();
        firstCts.Dispose();

        using var secondCts = new CancellationTokenSource();
        using var second = coordinator.TryRegisterSessionBackground("default", "session-1", secondCts);
        Assert.IsNotNull(second, "前台释放且旧后台退出后必须能再次注册会话级后台工作");
    }

    /// <summary>会话键互相隔离：另一会话的前台预留不得取消本会话维护。</summary>
    [TestMethod]
    public void SessionAdmission_IsIsolatedPerSession()
    {
        var coordinator = new AgentExecutionAdmissionCoordinator();
        using var cts = new CancellationTokenSource();
        using var background = coordinator.TryRegisterSessionBackground("default", "session-1", cts);
        Assert.IsNotNull(background);

        coordinator.ReserveSessionForeground("default", "session-2", TimeSpan.FromMinutes(1));

        Assert.IsFalse(cts.IsCancellationRequested, "别的会话的前台预留不得取消本会话维护");
        Assert.IsFalse(coordinator.HasSessionForegroundDemand("default", "session-1"));
        Assert.IsTrue(coordinator.HasSessionForegroundDemand("default", "session-2"));
    }
}
