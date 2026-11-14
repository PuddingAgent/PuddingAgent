using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Events;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Services;
using PuddingCode.SubAgents;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// D1 接线验证：CancelAllAsync 必须**真正**向正在执行的子代理发出取消信号，
/// 而不只是改状态记录、写归档、发事件。
///
/// 缺陷原形（修复前）：SubAgentManager 内驱动执行的 CancellationTokenSource 是
/// ExecuteAsyncSubAgentWithLimitsAsync 的 using var 局部变量，外界取不到；
/// CancelAllAsync 只做 TrackSubAgentCompleteAsync + CompleteRunAsync + PublishAsync，
/// 因此「取消」之后 loop 仍继续计费，直到超时或轮次耗尽。
///
/// 取红方式：把 CancelAllAsync 里的 CancelRunningRun(sa.SubSessionId) 删掉，
/// 本套件第一条用例立即失败（令牌未被取消）。
///
/// 这里刻意**不走 SpawnAsync**：那需要真实 LLM 配置与 dispatch 路径，
/// 会把「接线是否正确」与「执行链是否能跑」两件事混在一起。
/// 直接登记令牌 + 让状态层报告 running，即可隔离出接线这一件事。
/// </summary>
[TestClass]
public sealed class SubAgentCancellationWiringTests
{
    private const string ParentSession = "parent-session";
    private const string SubSession = "parent-session-sub-abc";

    private static SubAgentManager CreateManager(
        FakeSessionStateManager ssm,
        SubAgentRunCancellationRegistry registry)
        => new(
            ssm,
            new ServiceCollection().BuildServiceProvider(),
            new FakeEventBus(),
            new FakeRunStore(),
            NullLogger<SubAgentManager>.Instance,
            new FakeActivitySink(),
            new FakeTraceAccessor(),
            executionConfig: null,
            browserDelegation: null,
            runCancellations: registry);

    private static SubAgentStatus RunningStatus() => new()
    {
        RunId = "run-1",
        ParentSessionId = ParentSession,
        SubSessionId = SubSession,
        Status = "running",
        TaskSummary = "stays in flight until cancelled",
        SpawnedAt = DateTimeOffset.UtcNow,
    };

    // ═══════════════════════════════════════════════════════════
    // 核心红例
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task CancelAllAsync_SignalsTheRegisteredRunToken()
    {
        var registry = new SubAgentRunCancellationRegistry();
        var ssm = new FakeSessionStateManager();
        ssm.SubAgents.Add(RunningStatus());

        var manager = CreateManager(ssm, registry);

        // 模拟「运行中的子代理已把令牌登记进来」
        var token = registry.Register(SubSession, CancellationToken.None);
        Assert.IsFalse(token.IsCancellationRequested, "precondition: token starts uncancelled");

        var cancelled = await manager.CancelAllAsync(ParentSession);

        Assert.AreEqual(1, cancelled, "one running sub-agent was reported by the state layer");
        Assert.IsTrue(token.IsCancellationRequested,
            "D1: CancelAllAsync must signal the running token. Status-only bookkeeping leaves the loop spending.");
    }

    [TestMethod]
    public async Task CancelAllAsync_StillBooksTerminalState()
    {
        var registry = new SubAgentRunCancellationRegistry();
        var ssm = new FakeSessionStateManager();
        ssm.SubAgents.Add(RunningStatus());
        var manager = CreateManager(ssm, registry);
        registry.Register(SubSession, CancellationToken.None);

        await manager.CancelAllAsync(ParentSession);

        Assert.IsTrue(ssm.Completed.Contains(SubSession),
            "terminal bookkeeping must still happen — cancelling must not replace recording.");
        Assert.IsTrue(registry.IsRegistered(SubSession),
            "cancel must not remove the registry entry; removal belongs to the executor's finally.");
    }

    [TestMethod]
    public async Task CancelAllAsync_RunWithoutRegisteredToken_DoesNotThrow()
    {
        // 状态层说 running，但注册表里没有（例如 run 由别的路径启动，或已收尾）。
        // 这时取消应静默跳过，而不是抛异常 —— 未知不等于已取消。
        var registry = new SubAgentRunCancellationRegistry();
        var ssm = new FakeSessionStateManager();
        ssm.SubAgents.Add(RunningStatus());
        var manager = CreateManager(ssm, registry);

        var cancelled = await manager.CancelAllAsync(ParentSession);

        Assert.AreEqual(1, cancelled);
        Assert.IsTrue(ssm.Completed.Contains(SubSession));
    }

    [TestMethod]
    public async Task CancelAllAsync_NoRunningSubAgents_ReturnsZero()
    {
        var ssm = new FakeSessionStateManager();
        var manager = CreateManager(ssm, new SubAgentRunCancellationRegistry());

        Assert.AreEqual(0, await manager.CancelAllAsync(ParentSession));
    }

    [TestMethod]
    public async Task CancelAllAsync_OnlyCancelsRunningNotCompleted()
    {
        var registry = new SubAgentRunCancellationRegistry();
        var ssm = new FakeSessionStateManager();
        ssm.SubAgents.Add(RunningStatus());
        ssm.SubAgents.Add(new SubAgentStatus
        {
            RunId = "run-2",
            ParentSessionId = ParentSession,
            SubSessionId = "parent-session-sub-done",
            Status = "completed",
            TaskSummary = "already finished",
            SpawnedAt = DateTimeOffset.UtcNow,
        });
        var manager = CreateManager(ssm, registry);

        var runningToken = registry.Register(SubSession, CancellationToken.None);
        var doneToken = registry.Register("parent-session-sub-done", CancellationToken.None);

        var cancelled = await manager.CancelAllAsync(ParentSession);

        Assert.AreEqual(1, cancelled, "only the running sub-agent counts");
        Assert.IsTrue(runningToken.IsCancellationRequested);
        Assert.IsFalse(doneToken.IsCancellationRequested,
            "a completed sub-agent must not be signalled by CancelAllAsync");
    }

    // ═══════════════════════════════════════════════════════════
    // fakes
    // ═══════════════════════════════════════════════════════════

    // CancelAsync（单实例入口）
    // ═══════════════════════════════════════

    [TestMethod]
    public async Task CancelAsync_RunningSubAgent_SignalsAndBooks()
    {
        var registry = new SubAgentRunCancellationRegistry();
        var ssm = new FakeSessionStateManager();
        ssm.SubAgents.Add(RunningStatus());
        var manager = CreateManager(ssm, registry);
        var token = registry.Register(SubSession, CancellationToken.None);

        var ok = await manager.CancelAsync(SubSession, "user requested stop");

        Assert.IsTrue(ok, "CancelAsync must report success for a running sub-agent");
        Assert.IsTrue(token.IsCancellationRequested,
            "D1: single-instance cancel must reach the running token too");
        Assert.IsTrue(ssm.Completed.Contains(SubSession), "terminal state must be booked");
    }

    [TestMethod]
    public async Task CancelAsync_UnknownSubAgent_ReturnsFalse()
    {
        var ssm = new FakeSessionStateManager();
        var manager = CreateManager(ssm, new SubAgentRunCancellationRegistry());

        Assert.IsFalse(await manager.CancelAsync("parent-session-sub-missing", "x"));
        Assert.IsFalse(await manager.CancelAsync("", "x"));
        Assert.IsFalse(await manager.CancelAsync("   ", "x"));
    }

    [TestMethod]
    public async Task CancelAsync_CompletedSubAgent_DoesNotReBookTerminalState()
    {
        var registry = new SubAgentRunCancellationRegistry();
        var ssm = new FakeSessionStateManager();
        ssm.SubAgents.Add(new SubAgentStatus
        {
            RunId = "run-done",
            ParentSessionId = ParentSession,
            SubSessionId = SubSession,
            Status = "completed",
            TaskSummary = "already finished",
            SpawnedAt = DateTimeOffset.UtcNow,
        });
        var manager = CreateManager(ssm, registry);

        var ok = await manager.CancelAsync(SubSession, "too late");

        Assert.IsFalse(ok, "a finished sub-agent is not cancellable and must report so");
        Assert.AreEqual(0, ssm.Completed.Count,
            "re-booking a completed sub-agent would overwrite its real result with 'cancelled'");
    }

    [TestMethod]
    public async Task CancelAsync_DoesNotAffectSibling()
    {
        var registry = new SubAgentRunCancellationRegistry();
        var ssm = new FakeSessionStateManager();
        ssm.SubAgents.Add(RunningStatus());
        ssm.SubAgents.Add(new SubAgentStatus
        {
            RunId = "run-sibling",
            ParentSessionId = ParentSession,
            SubSessionId = "parent-session-sub-sibling",
            Status = "running",
            TaskSummary = "must survive",
            SpawnedAt = DateTimeOffset.UtcNow,
        });
        var manager = CreateManager(ssm, registry);
        var target = registry.Register(SubSession, CancellationToken.None);
        var sibling = registry.Register("parent-session-sub-sibling", CancellationToken.None);

        await manager.CancelAsync(SubSession, "only this one");

        Assert.IsTrue(target.IsCancellationRequested);
        Assert.IsFalse(sibling.IsCancellationRequested,
            "single-instance cancel must not spill over to sibling runs");
        Assert.IsFalse(ssm.Completed.Contains("parent-session-sub-sibling"));
    }

    private sealed class FakeSessionStateManager : ISessionStateManager
    {
        public List<SubAgentStatus> SubAgents { get; } = new();
        public List<string> Completed { get; } = new();

        public Task<IReadOnlyList<SubAgentStatus>> GetSubAgentsAsync(
            string sessionId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SubAgentStatus>>(
                SubAgents.Where(s => s.ParentSessionId == sessionId).ToList());

        public Task TrackSubAgentCompleteAsync(
            string subSessionId, SubAgentResult result, CancellationToken ct = default)
        {
            Completed.Add(subSessionId);
            return Task.CompletedTask;
        }

        public Task<long> AppendAsync(
            string sessionId, string workspaceId, ServerSentEventFrame frame,
            CancellationToken ct = default, RuntimeTraceContext? trace = null,
            string? component = null, string? operation = null)
            => throw new NotSupportedException();

        public ChannelReader<ServerSentEventFrame>? Subscribe(string sessionId)
            => throw new NotSupportedException();

        public void Unsubscribe(string sessionId, ChannelReader<ServerSentEventFrame> reader)
            => throw new NotSupportedException();

        public ChannelReader<SessionNotification>? SubscribeWorkspace(string workspaceId)
            => throw new NotSupportedException();

        public void UnsubscribeWorkspace(string workspaceId, ChannelReader<SessionNotification> reader)
            => throw new NotSupportedException();

        public Task<SessionState> GetSessionStateAsync(string sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Restore(string sessionId, SessionState state)
            => throw new NotSupportedException();

        public Task TrackSubAgentStartAsync(
            string parentSessionId, SubAgentSpawnInfo info, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> GetRunningSubAgentCountAsync(string parentSessionId, CancellationToken ct = default)
            => Task.FromResult(SubAgents.Count(s => s.ParentSessionId == parentSessionId && s.Status == "running"));

        public Task MarkStreamCompleteAsync(string sessionId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task MarkSessionClosedAsync(string sessionId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<SessionTraceReport> GetTraceReportAsync(
            string sessionId, bool includeSubAgents = false, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeEventBus : IInternalEventBus
    {
        public List<InternalEvent> Published { get; } = new();

        public Task PublishAsync(InternalEvent evt, CancellationToken ct = default)
        {
            Published.Add(evt);
            return Task.CompletedTask;
        }

        public Task<IEventSubscriptionHandle> SubscribeAsync(
            string eventTypePattern, Func<InternalEvent, Task> handler, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UnsubscribeAsync(IEventSubscriptionHandle handle)
            => throw new NotSupportedException();
    }

    private sealed class FakeActivitySink : IRuntimeActivitySink
    {
        public List<RuntimeActivity> Recorded { get; } = new();

        public Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default)
        {
            Recorded.Add(activity);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RuntimeActivity>> QueryAsync(
            RuntimeActivityQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RuntimeActivity>>(Array.Empty<RuntimeActivity>());
    }

    private sealed class FakeTraceAccessor : IRuntimeTraceAccessor
    {
        public RuntimeTraceContext? Current { get; set; }
    }

    private sealed class FakeRunStore : ISubAgentRunStore
    {
        public Task<SubAgentRunHandle> CreateRunAsync(SubAgentRunCreateRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task AppendEventAsync(string runId, string eventType, object payload, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task AppendToolAuditAsync(string runId, SubAgentToolAuditEntry entry, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SubAgentRunTerminalWriteResult> CompleteRunAsync(
            string runId, SubAgentRunCompletion completion, CancellationToken ct = default)
            => Task.FromResult(SubAgentRunTerminalWriteResult.Applied);

        public Task<SubAgentRunArchive?> GetRunArchiveAsync(string runId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> GetRunningCountByParentTurnAsync(string? parentTurnId, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> RecoverInterruptedRunsAsync(
            DateTimeOffset startedBeforeUtc, int maxRuns, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> ReplayPendingConversationEventsAsync(int maxRuns, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<bool> DeleteRunAsync(string runId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
