using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// T5：终态后维护调度的语义 —— 必须是「**前台租约释放之后**」才开始、不得长期占锁（D2：
/// 维护不得阻塞安全新消息）、同会话去重且**新请求抢占旧请求**、失败有界。
/// </summary>
[TestClass]
public sealed class PostTurnMaintenanceSchedulerTests
{
    private const string WorkspaceId = "workspace-post-turn";
    private const string SessionId = "session-post-turn";

    /// <summary>核心：Turn 持有门租约期间维护不得开始；释放后立刻开始。</summary>
    [TestMethod]
    public async Task Work_StartsOnlyAfterTheGateIsReleased()
    {
        var gate = new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance);
        var scheduler = new PostTurnMaintenanceScheduler(gate, NullLogger.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var turnLease = await gate.EnterAsync(SessionId, "agent_execute");

        scheduler.Schedule(WorkspaceId, SessionId, "post_turn", _ =>
        {
            started.SetResult();
            return Task.CompletedTask;
        });

        // 租约仍被 Turn 持有 ⇒ 维护必须还在等。
        var early = await Task.WhenAny(started.Task, Task.Delay(300));
        Assert.AreNotSame(started.Task, early, "前台租约未释放时维护不得开始");

        await turnLease.DisposeAsync();
        await scheduler.DrainAsync();

        Assert.IsTrue(started.Task.IsCompletedSuccessfully, "租约释放后维护必须开始");
        Assert.AreEqual(0, scheduler.InFlightCount);
    }

    /// <summary>D2：维护本身**不长期占锁** —— 它跑的时候另一条会话级获取必须能立刻成功。</summary>
    [TestMethod]
    public async Task Work_DoesNotHoldTheGateWhileRunning()
    {
        var gate = new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance);
        var scheduler = new PostTurnMaintenanceScheduler(gate, NullLogger.Instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        scheduler.Schedule(WorkspaceId, SessionId, "post_turn", async _ =>
        {
            workStarted.SetResult();
            await release.Task;
        });

        await workStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 维护正在跑：新的 Turn 必须能进入（否则就是把维护变成了阻塞新消息的锁）。
        var nextTurnAcquire = gate.EnterAsync(SessionId, "next_turn").AsTask();
        var acquired = await Task.WhenAny(nextTurnAcquire, Task.Delay(500));
        Assert.AreSame(nextTurnAcquire, acquired, "维护运行期间不得阻塞新的会话级获取");

        await using (var lease = await nextTurnAcquire)
        {
            Assert.IsNotNull(lease);
        }

        release.SetResult();
        await scheduler.DrainAsync();
    }

    /// <summary>
    /// 同会话「最新者胜」：更新的请求抢占更早的（旧结果必然过期）。
    /// 断言口径刻意不依赖取消落在哪一步 —— 抢占可能发生在①门探测阶段（更早的请求根本没跑）或
    /// ②执行体内部（协作取消）；两种都算抢占成功，因此断言「更早的**没有正常完成**」+「最新的执行了」
    /// +「留下抢占日志」。
    /// </summary>
    [TestMethod]
    public async Task NewerRequest_PreemptsEarlierOne()
    {
        var gate = new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance);
        var logger = new RecordingLogger();
        var scheduler = new PostTurnMaintenanceScheduler(gate, logger);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCompletedNormally = false;

        scheduler.Schedule(WorkspaceId, SessionId, "first", async ct =>
        {
            firstStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            firstCompletedNormally = true;
        });

        // 等第一个真正开始执行（若抢占抢先发生，这一步可能已经不成立——那也属于抢占成功）。
        await Task.WhenAny(firstStarted.Task, Task.Delay(1_000));

        scheduler.Schedule(WorkspaceId, SessionId, "second", _ =>
        {
            secondRan.TrySetResult();
            return Task.CompletedTask;
        });

        await scheduler.DrainAsync();

        Assert.IsFalse(firstCompletedNormally, "被抢占的维护不得正常完成");
        Assert.IsTrue(secondRan.Task.IsCompletedSuccessfully, "最新请求必须执行");
        Assert.AreEqual(0, scheduler.InFlightCount);
        Assert.IsTrue(
            logger.Messages.Any(m => m.Contains("Preempting earlier maintenance", StringComparison.Ordinal)),
            "抢占必须留下可归因的日志");
    }

    /// <summary>跨会话互不干扰。</summary>
    [TestMethod]
    public async Task DifferentSessions_RunIndependently()
    {
        var gate = new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance);
        var scheduler = new PostTurnMaintenanceScheduler(gate, NullLogger.Instance);
        var ran = new System.Collections.Concurrent.ConcurrentBag<string>();

        scheduler.Schedule(WorkspaceId, "session-a", "post_turn", _ => { ran.Add("a"); return Task.CompletedTask; });
        scheduler.Schedule(WorkspaceId, "session-b", "post_turn", _ => { ran.Add("b"); return Task.CompletedTask; });

        await scheduler.DrainAsync();

        CollectionAssert.AreEquivalent(new[] { "a", "b" }, ran.ToArray());
    }

    /// <summary>失败有界：维护抛错只记日志，不得冒泡到调用方（DrainAsync 正常返回）。</summary>
    [TestMethod]
    public async Task WorkFailure_IsSwallowedAndLogged()
    {
        var gate = new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance);
        var logger = new RecordingLogger();
        var scheduler = new PostTurnMaintenanceScheduler(gate, logger);

        scheduler.Schedule(WorkspaceId, SessionId, "post_turn", _ => throw new InvalidOperationException("boom"));

        await scheduler.DrainAsync();

        Assert.AreEqual(0, scheduler.InFlightCount);
        Assert.IsTrue(
            logger.Messages.Any(m => m.Contains("Failed", StringComparison.Ordinal)
                && m.Contains("session-post-turn", StringComparison.Ordinal)),
            "失败必须留下可归因的日志");
    }

    /// <summary>编译期之外的行为约束：work 为 null / sessionId 为空必须立刻抛（编程错误不静默）。</summary>
    [TestMethod]
    public void Schedule_ValidatesArguments()
    {
        var gate = new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance);
        var scheduler = new PostTurnMaintenanceScheduler(gate, NullLogger.Instance);

        Assert.ThrowsExactly<ArgumentNullException>(() => scheduler.Schedule(WorkspaceId, SessionId, "r", null!));
        Assert.ThrowsExactly<ArgumentException>(() => scheduler.Schedule(WorkspaceId, "  ", "r", _ => Task.CompletedTask));
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_gate)
                    return _messages.ToArray();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
                _messages.Add(formatter(state, exception));
        }
    }
}
