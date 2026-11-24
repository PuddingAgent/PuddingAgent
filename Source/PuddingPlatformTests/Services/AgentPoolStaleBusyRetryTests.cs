using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.AgentPool;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 启动期 stale-busy 复位的<b>收敛重试</b>。
/// <para>
/// 为什么要单独锁这一条：复位在池就绪之前跑，而把上一进程遗留的 <c>running</c> 收成终态的是
/// 另一个 BackgroundService。宿主只按注册顺序调 <c>StartAsync</c>，而 <c>StartAsync</c> 不 await
/// <c>ExecuteAsync</c> 主体 —— 所以「注册在后面」并不等于「恢复已经跑完」。复位先落地时判据还看得见
/// <c>running</c>，于是返回 0、什么也没改，条目就停到下次重启。
/// </para>
/// <para>
/// 这两个用例把「有 busy 才重试」和「没 busy 不白等」分别钉住：前者证明竞态被补上，
/// 后者证明代价只在真的需要时才付。
/// </para>
/// </summary>
[TestClass]
public sealed class AgentPoolStaleBusyRetryTests
{
    private const string PoolId = "pool-1";
    private const string SubSessionId = "parent-session-sub-abc";

    [TestMethod]
    public async Task Sweep_RetriesWhileBusyEntryExists_UntilInterruptedRunRecoveryConverges()
    {
        // 前两次复位「什么都没改」（此刻 run 仍是 running），第三次才成功 —— 正是竞态的形态。
        var store = new ScriptedStore([0, 0, 1]);
        var pool = CreatePool(store);

        var snapshot = await pool.GetSnapshotAsync();

        Assert.AreEqual(3, store.ResetCalls,
            "存在 busy 条目时必须重试到复位生效（1 次成功前先 2 次空转）");
        Assert.AreEqual(PooledSubAgentStatus.Sleeping, snapshot[0].Status,
            "复位之后内存视图必须跟上库，否则 panel 会一直显示「忙碌」");
    }

    [TestMethod]
    public async Task Sweep_DoesNotRetry_WhenNoEntryIsBusy()
    {
        var store = new ScriptedStore([0], initialStatus: AgentPoolEntity.StatusIdle);
        var pool = CreatePool(store);

        await pool.GetSnapshotAsync();

        Assert.AreEqual(1, store.ResetCalls,
            "没有 busy 条目时必须只试一次：正常启动不该为此白等 1.75 秒");
    }

    // ── 脚手架 ─────────────────────────────────────────────

    private static SubAgentPool CreatePool(IAgentPoolEntryStore store) => new(
        new TestSubAgentManager(),
        new ConfigurationBuilder().Build(),
        NullLogger<SubAgentPool>.Instance,
        store);

    /// <summary>
    /// 按剧本返回复位行数，并在「成功」时把自己的快照改成 sleeping ——
    /// 否则复位之后的 <c>LoadAllAsync</c> 会把内存又拉回 busy，测不出真行为。
    /// </summary>
    private sealed class ScriptedStore(int[] resetResults, string initialStatus = AgentPoolEntity.StatusBusy)
        : IAgentPoolEntryStore
    {
        private readonly int[] _resetResults = resetResults;
        private string _status = initialStatus;

        public int ResetCalls { get; private set; }

        public Task<IReadOnlyList<AgentPoolEntrySnapshot>> LoadAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentPoolEntrySnapshot>>([Snapshot()]);

        public Task<int> ResetStaleBusyAsync(DateTimeOffset now, CancellationToken ct)
        {
            var index = Math.Min(ResetCalls, _resetResults.Length - 1);
            var result = _resetResults[index];
            ResetCalls++;
            if (result > 0)
                _status = AgentPoolEntity.StatusSleeping;
            return Task.FromResult(result);
        }

        private AgentPoolEntrySnapshot Snapshot()
        {
            var now = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);
            return new AgentPoolEntrySnapshot(
                PoolId: PoolId,
                Name: "stale-agent",
                SubSessionId: SubSessionId,
                TemplateId: "workspace-task-agent",
                Role: "worker",
                OwnerWorkspaceId: "default",
                OwnerSessionId: "parent-session",
                OwnerAgentId: null,
                ActiveExecutionId: null,
                Status: _status,
                StatusReason: null,
                TaskCount: 1,
                LastSuccess: null,
                CreatedAt: now,
                LastUsedAt: now);
        }

        public Task<int> TryInsertAsync(AgentPoolInsertRequest request, int capacity, CancellationToken ct)
            => throw new NotSupportedException("本用例只覆盖复位路径");

        public Task<int> TryAcquireAsync(string poolId, string ownerSessionId, string ownerWorkspaceId,
            string executionId, DateTimeOffset now, CancellationToken ct)
            => throw new NotSupportedException("本用例只覆盖复位路径");

        public Task<int> TryCompleteAsync(string poolId, string subSessionId, string executionId,
            bool success, DateTimeOffset now, CancellationToken ct)
            => throw new NotSupportedException("本用例只覆盖复位路径");

        public Task<int> TrySetStatusAsync(string poolId, string expectedStatus, string newStatus,
            string? statusReason, bool clearExecution, DateTimeOffset now, CancellationToken ct)
            => throw new NotSupportedException("本用例只覆盖复位路径");

        public Task<string?> TryEvictOldestSleepingAsync(DateTimeOffset now, CancellationToken ct)
            => throw new NotSupportedException("本用例只覆盖复位路径");

        public Task<AgentPoolEntrySnapshot?> GetByIdAsync(string poolId, CancellationToken ct)
            => throw new NotSupportedException("本用例只覆盖复位路径");

        public Task<AgentPoolEntrySnapshot?> GetBySubSessionAsync(string subSessionId, CancellationToken ct)
            => throw new NotSupportedException("本用例只覆盖复位路径");
    }
}
