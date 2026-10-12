using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Messaging;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// R2 门禁（复核 §3）：后台可选维护必须接入**前后台准入**——
/// <list type="number">
/// <item>新消息受理/前台预留时立刻取消同会话维护（不必等下一次 Schedule）；</item>
/// <item>「维护生成被屏障卡住 → 前台预留 → 取消被观察到 → 新 Turn 完成水合并主 dispatch」
/// 必须都发生在**释放屏障之前**；</item>
/// <item>迟到结果零应用；</item>
/// <item>同候选合并返回 false、不同候选抢占、旧 finally 不得移除新 entry；</item>
/// <item>停机与 deadline 生效。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class PostTurnMaintenanceForegroundPreemptionTests
{
    private const string WorkspaceId = "workspace-r2";
    private const string SessionId = "session-r2";

    /// <summary>
    /// **核心门禁**：维护的生成阶段被人为屏障卡住；此时前台预留（新消息受理）必须
    /// 立刻取消它，并且新 Turn 能完成「水合 + 主 dispatch」，全部发生在释放屏障之前。
    /// 释放屏障后，迟到的维护结果**零应用**。
    /// </summary>
    [TestMethod]
    public async Task ForegroundReservation_CancelsBlockedMaintenance_AndNextTurnProceedsBeforeBarrierRelease()
    {
        var gate = new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance);
        var admission = new AgentExecutionAdmissionCoordinator();
        var scheduler = new PostTurnMaintenanceScheduler(gate, NullLogger.Instance, admission);

        var generationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenanceCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appliedLateResults = 0;

        scheduler.Schedule(
            WorkspaceId, SessionId, "post_turn",
            async ct =>
            {
                generationStarted.SetResult();
                try
                {
                    // 模拟「生成阶段卡在人工屏障」：只有屏障释放才会返回。
                    await barrier.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    maintenanceCancelled.SetResult();
                    throw;
                }

                // 迟到结果：只有没被取消时才会走到这里 ⇒ 必须零应用。
                Interlocked.Increment(ref appliedLateResults);
            },
            candidateKey: "candidate-1");

        // ① 维护已开工并卡在生成阶段。
        await generationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(barrier.Task.IsCompleted, "前提：屏障此刻必须仍未释放");

        // ② 新消息受理 ⇒ 会话级前台预留（MessageDeliveryDispatcher 的同一调用）。
        admission.ReserveSessionForeground(WorkspaceId, SessionId);

        // ③ 取消必须被观察到，且发生在屏障释放之前。
        var cancelled = await Task.WhenAny(maintenanceCancelled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.AreSame(maintenanceCancelled.Task, cancelled, "前台预留必须立刻取消同会话维护");
        Assert.IsFalse(barrier.Task.IsCompleted, "取消必须发生在释放屏障之前");

        // ④ 新 Turn 的「水合 + 主 dispatch」也必须能在屏障释放之前完成：
        //    它拿会话门 + 前台租约，不依赖维护退出。
        var hydrationAndDispatch = Task.Run(async () =>
        {
            using var foreground = admission.AcquireSessionForeground(WorkspaceId, SessionId);
            await using var lease = await gate.EnterAsync(SessionId, "next_turn");
            await Task.Delay(30); // 模拟水合 + 主 dispatch 的最小工作
            return true;
        });

        var turnDone = await Task.WhenAny(hydrationAndDispatch, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.AreSame(hydrationAndDispatch, turnDone, "新 Turn 必须在屏障释放之前完成水合与主 dispatch");
        Assert.IsTrue(await hydrationAndDispatch);
        Assert.IsFalse(barrier.Task.IsCompleted, "前提复核：屏障此刻仍未被释放");

        // ⑤ 释放屏障：仍不得有任何迟到结果被应用。
        barrier.SetResult();
        await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, Volatile.Read(ref appliedLateResults), "迟到结果必须零应用");
        Assert.AreEqual(0, scheduler.InFlightCount);
    }

    /// <summary>同候选重复调度必须**合并并返回 false**（不重复执行、不抢占）。</summary>
    [TestMethod]
    public async Task SameCandidate_DuplicateSchedule_IsMerged_AndReturnsFalse()
    {
        var gate = new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance);
        var scheduler = new PostTurnMaintenanceScheduler(gate, NullLogger.Instance);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        var first = scheduler.Schedule(WorkspaceId, SessionId, "post_turn", async _ =>
        {
            Interlocked.Increment(ref runs);
            await barrier.Task;
        }, candidateKey: "same");

        var second = scheduler.Schedule(WorkspaceId, SessionId, "post_turn", _ =>
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        }, candidateKey: "same");

        Assert.IsTrue(first, "首次调度必须被接受");
        Assert.IsFalse(second, "同候选必须合并并返回 false");

        barrier.SetResult();
        await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, runs, "同候选只能执行一次");
        Assert.AreEqual(0, scheduler.InFlightCount);
    }

    /// <summary>不同候选：抢占旧的；且旧的 finally **不得**移除新的 entry。</summary>
    [TestMethod]
    public async Task DifferentCandidate_PreemptsEarlier_AndOldCompletionDoesNotRemoveNewEntry()
    {
        var gate = new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance);
        var scheduler = new PostTurnMaintenanceScheduler(gate, NullLogger.Instance);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        scheduler.Schedule(WorkspaceId, SessionId, "old", async ct =>
        {
            firstStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                oldCancelled.SetResult();
                throw;
            }
        }, candidateKey: "candidate-old");

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var accepted = scheduler.Schedule(WorkspaceId, SessionId, "new", async _ =>
        {
            await newBarrier.Task;
        }, candidateKey: "candidate-new");

        Assert.IsTrue(accepted, "不同候选必须抢占并接受");
        await oldCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 旧任务已经取消并走过 finally：新 entry 必须仍在途。
        await Task.Delay(100);
        Assert.AreEqual(1, scheduler.InFlightCount, "旧的 finally 不得移除新的 entry");
        CollectionAssert.Contains(scheduler.DescribeInFlight().ToList(), "new");

        newBarrier.SetResult();
        await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, scheduler.InFlightCount);
    }

    /// <summary>启动时已有前台需求 ⇒ 维护直接放弃（不排队阻塞新消息）。</summary>
    [TestMethod]
    public async Task ForegroundDemandAtStart_SkipsMaintenance()
    {
        var gate = new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance);
        var admission = new AgentExecutionAdmissionCoordinator();
        var scheduler = new PostTurnMaintenanceScheduler(gate, NullLogger.Instance, admission);
        var ran = false;

        admission.ReserveSessionForeground(WorkspaceId, SessionId);

        scheduler.Schedule(WorkspaceId, SessionId, "post_turn", _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(ran, "有前台需求时可选维护必须放弃，而不是排队");
        Assert.AreEqual(0, scheduler.InFlightCount);
    }

    /// <summary>停机：取消在途、等待退出；之后新调度一律返回 false。</summary>
    [TestMethod]
    public async Task StopAsync_CancelsInFlight_AndLaterScheduleReturnsFalse()
    {
        var gate = new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance);
        var scheduler = new PostTurnMaintenanceScheduler(gate, NullLogger.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        scheduler.Schedule(WorkspaceId, SessionId, "post_turn", async ct =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.StopAsync(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(cancelled.Task.IsCompleted, "停机必须取消在途维护");
        Assert.AreEqual(0, scheduler.InFlightCount);

        var afterStop = scheduler.Schedule(WorkspaceId, SessionId, "post_turn", _ => Task.CompletedTask);
        Assert.IsFalse(afterStop, "停机后不得再接受新的维护调度");
    }

    /// <summary>deadline 到期即协作取消（维护是可选能力，不得无限期占用）。</summary>
    [TestMethod]
    public async Task MaintenanceDeadline_CancelsWork()
    {
        var gate = new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance);
        var scheduler = new PostTurnMaintenanceScheduler(
            gate, NullLogger.Instance, admission: null, maintenanceTimeout: TimeSpan.FromMilliseconds(200));
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        scheduler.Schedule(WorkspaceId, SessionId, "post_turn", async ct =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }
        });

        var done = await Task.WhenAny(cancelled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.AreSame(cancelled.Task, done, "超过 deadline 必须取消维护");
        await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, scheduler.InFlightCount);
    }
}
