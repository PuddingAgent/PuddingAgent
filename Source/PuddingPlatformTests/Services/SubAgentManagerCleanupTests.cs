using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.SubAgents;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// <see cref="SubAgentManager.CleanupAsync"/> 的清理编排：run 行删除与投影行删除的决策链。
/// <para>
/// <b>为什么单独锁这层：</b>过滤器语义已由 <c>SubAgentCleanupScopeTests</c> 锁住（纯函数），
/// 但「删了 run 行之后要不要连 <c>session_sub_agents</c> 投影一起删」是管理器里的分支，
/// 此前只有工具层 mock 与运行时取证，没有任何直接单测。它是 2026-11-25 那次真实回归的位置：
/// 只删 run 行时，成员销毁、归档删掉，会话面板里却仍挂着一条 completed 的子代理，用户没有入口去掉它。
/// </para>
/// <para>
/// <b>三条用例构成两组对照，缺一组就分不出「无差别删投影」和「保守保留」：</b>
/// ② 与 ③ 只差 <c>RunId</c> 的形态（占位值 vs 真实 runId），前者必须删投影、后者必须保留。
/// </para>
/// </summary>
[TestClass]
public sealed class SubAgentManagerCleanupTests
{
    // ═══════════════════════════════════════════════════════
    // ① run 行删成功 → 计入 cleaned，并删投影
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task CleanupAsync_DeletedRun_CountsAndRemovesProjection()
    {
        var ssm = new RecordingSessionStateManager
        {
            SubAgents = [Status("sub-a", runId: "run-a")],
        };
        var runStore = new RecordingSubAgentRunStore { DeleteResult = true };

        var cleaned = await CreateManager(ssm, runStore).CleanupAsync(
            "parent-session",
            new SubAgentCleanupFilter { SubSessionId = "sub-a", MaxCount = 1 });

        Assert.AreEqual(1, cleaned, "删掉一条 run 行必须计入返回值");
        CollectionAssert.AreEqual(
            new[] { "sub-a" },
            ssm.LastRemovedProjections?.ToArray(),
            "run 行已删，投影必须一起删——否则会话面板永久挂着已销毁的子代理");
    }

    // ═══════════════════════════════════════════════════════
    // ② run 行本来就没有（占位 RunId）→ 不计 cleaned，但投影必须删
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// 快照在没有 run 行时会把 <c>RunId</c> 回退成 <c>SubSessionId</c> 这个占位值。
    /// 这种条目恰恰是最需要清的：归档早已不存在，只剩投影挂在面板上。
    /// </summary>
    [TestMethod]
    public async Task CleanupAsync_PlaceholderRunId_RemovesProjectionWithoutTouchingStore()
    {
        var ssm = new RecordingSessionStateManager
        {
            // RunId == SubSessionId 即占位值。
            SubAgents = [Status("sub-dead", runId: "sub-dead")],
        };
        var runStore = new RecordingSubAgentRunStore
        {
            DeleteResult = false,
            ArchiveBehavior = ArchiveBehavior.Throws,
        };

        var cleaned = await CreateManager(ssm, runStore).CleanupAsync(
            "parent-session",
            new SubAgentCleanupFilter { SubSessionId = "sub-dead", MaxCount = 1 });

        Assert.AreEqual(0, cleaned, "没有 run 行可删，计数必须为 0（返回值语义是删掉的 run 行数）");
        Assert.AreEqual(0, runStore.ArchiveProbeCount,
            "占位 RunId 不可能命中归档，不该为此多查一次库");
        CollectionAssert.AreEqual(
            new[] { "sub-dead" },
            ssm.LastRemovedProjections?.ToArray(),
            "归档确实不存在 ⇒ 投影是唯一的残留线索，必须删掉");
    }

    // ═══════════════════════════════════════════════════════
    // ③ 删 run 行失败且归档探测也失败 → 投影必须保留
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// <c>DeleteRunAsync</c> 返回 <c>false</c> 是两义的：没有这条，或者删的时候出错。
    /// 探测归档时再抛错，就无法区分两者——此时投影是那条 run 最后的定位线索，
    /// 删掉等于把一个可重试状态变成不可重试。
    /// </summary>
    [TestMethod]
    public async Task CleanupAsync_ArchiveProbeThrows_KeepsProjection()
    {
        var ssm = new RecordingSessionStateManager
        {
            SubAgents = [Status("sub-probe", runId: "run-probe")],
        };
        var runStore = new RecordingSubAgentRunStore
        {
            DeleteResult = false,
            ArchiveBehavior = ArchiveBehavior.Throws,
        };

        var cleaned = await CreateManager(ssm, runStore).CleanupAsync(
            "parent-session",
            new SubAgentCleanupFilter { SubSessionId = "sub-probe", MaxCount = 1 });

        Assert.AreEqual(0, cleaned);
        Assert.AreEqual(1, runStore.ArchiveProbeCount, "真实 runId 必须去复核归档是否存在");
        Assert.IsNull(ssm.LastRemovedProjections,
            "归档是否真的不存在无法判定时，投影必须保留：它还能被重试，删了就再也找不到");
    }

    // ═══════════════════════════════════════════════════════
    // ④ 正向对照：归档探测成功返回 null → 投影删；返回非 null → 保留
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task CleanupAsync_ArchiveProbeSucceeds_DecidesOnActualExistence()
    {
        var absentSsm = new RecordingSessionStateManager
        {
            SubAgents = [Status("sub-absent", runId: "run-absent")],
        };
        var absentStore = new RecordingSubAgentRunStore
        {
            DeleteResult = false,
            ArchiveBehavior = ArchiveBehavior.ReturnsNull,
        };

        await CreateManager(absentSsm, absentStore).CleanupAsync(
            "parent-session",
            new SubAgentCleanupFilter { SubSessionId = "sub-absent", MaxCount = 1 });

        CollectionAssert.AreEqual(
            new[] { "sub-absent" },
            absentSsm.LastRemovedProjections?.ToArray(),
            "探测确认归档不存在 ⇒ 删投影");

        var presentSsm = new RecordingSessionStateManager
        {
            SubAgents = [Status("sub-present", runId: "run-present")],
        };
        var presentStore = new RecordingSubAgentRunStore
        {
            DeleteResult = false,
            ArchiveBehavior = ArchiveBehavior.ReturnsArchive,
        };

        await CreateManager(presentSsm, presentStore).CleanupAsync(
            "parent-session",
            new SubAgentCleanupFilter { SubSessionId = "sub-present", MaxCount = 1 });

        Assert.IsNull(presentSsm.LastRemovedProjections,
            "归档仍在 ⇒ 投影是它的最后线索，不得删");
    }

    // ═══════════════════════════════════════════════════════
    // ⑤⑥ 预演：批量清理没有撤销路径，必须在删之前就能看到候选集
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// 预演只选不删：候选按最旧优先、被 <c>MaxCount</c> 截断，且不碰 run 存储与投影行。
    /// </summary>
    /// <remarks>
    /// 「不删」必须同时断言三条通道（run 行、归档探测、投影）——只断言其中一条时，
    /// 一个先把投影删了再列候选的实现照样能骗过测试。
    /// </remarks>
    [TestMethod]
    public async Task PreviewCleanupAsync_ReturnsOldestFirstAndDeletesNothing()
    {
        var ssm = new RecordingSessionStateManager
        {
            SubAgents =
            [
                Status("sub-new", spawnedAt: Now.AddMinutes(-10)),
                Status("sub-old", spawnedAt: Now.AddHours(-5)),
                Status("sub-mid", spawnedAt: Now.AddHours(-2)),
            ],
        };
        var runStore = new RecordingSubAgentRunStore { DeleteResult = true };

        var preview = await CreateManager(ssm, runStore).PreviewCleanupAsync(
            "parent-session",
            new SubAgentCleanupFilter { MaxCount = 2 });

        CollectionAssert.AreEqual(
            new[] { "sub-old", "sub-mid" },
            preview.Select(p => p.SubSessionId).ToArray(),
            "预演必须与真删同序（最旧优先），否则「先看清再删」看到的是另一批");
        Assert.AreEqual(0, runStore.DeletedRunIds.Count, "预演不得删任何 run 行");
        Assert.AreEqual(0, runStore.ArchiveProbeCount, "预演不得探测归档（说明它压根没进删除分支）");
        Assert.IsNull(ssm.LastRemovedProjections, "预演不得删投影行");
    }

    /// <summary>
    /// 预演必须<b>准确预言</b>真删会动哪些 run：两条路径共用同一份候选选择。
    /// </summary>
    /// <remarks>
    /// 两侧各写一份筛选时，预演说 A、真删动 B 也能各自「看起来合理」——只有把
    /// 「预演 ids」与「实际被删 runIds」直接对比才能取红。
    /// </remarks>
    [TestMethod]
    public async Task PreviewCleanupAsync_PredictsExactlyWhatCleanupDeletes()
    {
        var ssm = new RecordingSessionStateManager
        {
            SubAgents =
            [
                Status("sub-c", runId: "run-c", spawnedAt: Now.AddMinutes(-1)),
                Status("sub-a", runId: "run-a", spawnedAt: Now.AddHours(-6)),
                Status("sub-b", runId: "run-b", spawnedAt: Now.AddHours(-3)),
            ],
        };
        var runStore = new RecordingSubAgentRunStore { DeleteResult = true };
        var manager = CreateManager(ssm, runStore);
        var filter = new SubAgentCleanupFilter { MaxCount = 2 };

        var preview = await manager.PreviewCleanupAsync("parent-session", filter);
        var cleaned = await manager.CleanupAsync("parent-session", filter);

        Assert.AreEqual(preview.Count, cleaned, "预演说几条，真删就得是几条");
        CollectionAssert.AreEqual(
            preview.Select(p => p.RunId).ToArray(),
            runStore.DeletedRunIds.ToArray(),
            "预演列出的候选与真正被删的 run 必须一一对应（含顺序）");
    }

    // ── 脚手架 ──────────────────────────────────────────────

    private static readonly DateTimeOffset Now = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);

    private static SubAgentStatus Status(string subSessionId, string? runId = null, DateTimeOffset? spawnedAt = null) => new()
    {
        RunId = runId ?? ("run-" + subSessionId),
        ParentSessionId = "parent-session",
        SubSessionId = subSessionId,
        Status = "completed",
        TemplateId = "workspace-task-agent",
        TaskSummary = "task of " + subSessionId,
        SpawnedAt = spawnedAt ?? Now.AddHours(-1),
    };

    private static SubAgentManager CreateManager(
        RecordingSessionStateManager ssm,
        RecordingSubAgentRunStore runStore)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new SubAgentManager(
            ssm,
            services,
            new NullInternalEventBus(),
            runStore,
            NullLogger<SubAgentManager>.Instance,
            new NullRuntimeActivitySink(),
            new NullRuntimeTraceAccessor());
    }

    // ═══════════════════════════════════════════════════════
    // 测试替身
    // ═══════════════════════════════════════════════════════

    internal enum ArchiveBehavior
    {
        ReturnsNull,
        ReturnsArchive,
        Throws,
    }

    private sealed class RecordingSubAgentRunStore : ISubAgentRunStore
    {
        public bool DeleteResult { get; init; }
        public ArchiveBehavior ArchiveBehavior { get; init; } = ArchiveBehavior.ReturnsNull;
        public int ArchiveProbeCount { get; private set; }

        /// <summary>被真正删除过的 runId（按调用顺序）；预演必须让它恒为空。</summary>
        public List<string> DeletedRunIds { get; } = [];

        public Task<bool> DeleteRunAsync(string runId, CancellationToken ct = default)
        {
            DeletedRunIds.Add(runId);
            return Task.FromResult(DeleteResult);
        }

        public Task<SubAgentRunArchive?> GetRunArchiveAsync(string runId, CancellationToken ct = default)
        {
            ArchiveProbeCount++;
            return ArchiveBehavior switch
            {
                ArchiveBehavior.Throws => throw new InvalidOperationException("archive store unavailable"),
                ArchiveBehavior.ReturnsArchive => Task.FromResult<SubAgentRunArchive?>(new SubAgentRunArchive
                {
                    // 只需「非 null」这一个事实；Manifest 的其余字段与本用例无关。
                    Manifest = new SubAgentRunManifest
                    {
                        RunId = runId,
                        ParentSessionId = "parent-session",
                        SubSessionId = "sub-present",
                        WorkspaceId = "default",
                        AgentInstanceId = "agent-sub",
                        TemplateId = "workspace-task-agent",
                        Task = "cleanup probe",
                        Status = "completed",
                        StartedAt = Now.AddHours(-1),
                    },
                }),
                _ => Task.FromResult<SubAgentRunArchive?>(null),
            };
        }

        public Task<bool> HasSubAgentAncestryAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<SubAgentRunHandle> CreateRunAsync(SubAgentRunCreateRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("CleanupAsync 不创建 run");

        public Task AppendEventAsync(string runId, string eventType, object payload, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task AppendToolAuditAsync(string runId, SubAgentToolAuditEntry entry, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<SubAgentRunTerminalWriteResult> CompleteRunAsync(
            string runId,
            SubAgentRunCompletion completion,
            CancellationToken ct = default) =>
            Task.FromResult(SubAgentRunTerminalWriteResult.Applied);

        public Task<int> RecoverInterruptedRunsAsync(
            DateTimeOffset startedBeforeUtc,
            int maxRuns,
            CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<int> ReplayPendingConversationEventsAsync(int maxRuns, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<int> GetRunningCountByParentTurnAsync(string? parentTurnId, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<string?> FindRunningRunIdAsync(string subSessionId, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class RecordingSessionStateManager : ISessionStateManager
    {
        public IReadOnlyList<SubAgentStatus> SubAgents { get; init; } = [];

        /// <summary>null 表示从未被调用——用例靠它区分「删了」与「没删」。</summary>
        public IReadOnlyCollection<string>? LastRemovedProjections { get; private set; }

        public Task<IReadOnlyList<SubAgentStatus>> GetSubAgentsAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(SubAgents);

        public Task<int> RemoveSubAgentProjectionsAsync(
            string parentSessionId,
            IReadOnlyCollection<string> subSessionIds,
            CancellationToken ct = default)
        {
            LastRemovedProjections = subSessionIds;
            return Task.FromResult(subSessionIds.Count);
        }

        public Task<int> GetRunningSubAgentDescendantCountAsync(string rootSessionId, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<int> GetRunningSubAgentCountAsync(string parentSessionId, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<long> AppendAsync(
            string sessionId,
            string workspaceId,
            ServerSentEventFrame frame,
            CancellationToken ct = default,
            RuntimeTraceContext? trace = null,
            string? component = null,
            string? operation = null) =>
            Task.FromResult(1L);

        public System.Threading.Channels.ChannelReader<ServerSentEventFrame>? Subscribe(string sessionId) => null;

        public void Unsubscribe(string sessionId, System.Threading.Channels.ChannelReader<ServerSentEventFrame> reader) { }

        public System.Threading.Channels.ChannelReader<SessionNotification> SubscribeWorkspace(string workspaceId) =>
            System.Threading.Channels.Channel.CreateUnbounded<SessionNotification>().Reader;

        public void UnsubscribeWorkspace(
            string workspaceId,
            System.Threading.Channels.ChannelReader<SessionNotification> reader) { }

        public Task<SessionState> GetSessionStateAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(SessionState.Completed);

        public Task TrackSubAgentStartAsync(
            string parentSessionId,
            SubAgentSpawnInfo info,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task TrackSubAgentCompleteAsync(
            string subSessionId,
            SubAgentResult result,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<int> RecoverInterruptedSubAgentsAsync(
            DateTimeOffset startedBeforeUtc,
            int maxRows,
            CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task MarkStreamCompleteAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;

        public Task MarkSessionClosedAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<SessionTraceReport> GetTraceReportAsync(
            string sessionId,
            bool includeSubAgents = false,
            CancellationToken ct = default) =>
            Task.FromResult(new SessionTraceReport
            {
                SessionId = sessionId,
                TraceIds = [],
                ComponentTimeline = [],
                LlmCalls = [],
                ToolCalls = [],
                SubAgents = [],
            });

        public void Restore(string sessionId, SessionState state) { }
    }

    private sealed class NullInternalEventBus : IInternalEventBus
    {
        public Task PublishAsync(InternalEvent evt, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IEventSubscriptionHandle> SubscribeAsync(
            string eventTypePattern,
            Func<InternalEvent, Task> handler,
            CancellationToken ct = default) =>
            throw new NotSupportedException("CleanupAsync 不订阅事件");

        public Task UnsubscribeAsync(IEventSubscriptionHandle handle) => Task.CompletedTask;
    }

    private sealed class NullRuntimeActivitySink : IRuntimeActivitySink
    {
        public Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<RuntimeActivity>> QueryAsync(
            RuntimeActivityQuery query,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RuntimeActivity>>([]);
    }

    private sealed class NullRuntimeTraceAccessor : IRuntimeTraceAccessor
    {
        public RuntimeTraceContext? Current { get; set; }
    }
}
