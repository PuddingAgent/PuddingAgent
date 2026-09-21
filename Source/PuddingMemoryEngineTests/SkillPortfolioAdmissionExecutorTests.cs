using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingMemoryEngine.Services;

namespace PuddingMemoryEngineTests;

/// <summary>
/// G3 执行器契约：把"置换＝禁用价值最低者（**可回滚**）"这一条验收标准从判定器层
/// 一路钉到**副作用层**——禁用、再建、失败回滚，以及对非置换路径的**零写盘**保证。
/// <para>
/// 用探针式假仓：它记录**每一次**写盘（顺序 + 参数），因此"回滚"不是靠读码断言，
/// 而是"假仓最终状态 + 调用序列"两重证据。
/// </para>
/// </summary>
[TestClass]
public sealed class SkillPortfolioAdmissionExecutorTests
{
    private const string Agent = "agent-evolution";

    // ───────────────────────── create ─────────────────────────

    [TestMethod]
    public async Task Create_ShouldMaterializeOnce_AndNeverToggleEnabled()
    {
        var store = new ProbeSkillStore();
        var executor = new SkillPortfolioAdmissionExecutor(store);
        var invocations = 0;

        var outcome = await executor.ExecuteAsync(
            Agent,
            CreateAdmission(),
            () =>
            {
                invocations++;
                return Task.FromResult<string?>("skill-new");
            });

        Assert.AreEqual(1, invocations, "create 路径只允许物化一次");
        Assert.AreEqual(0, store.Writes.Count, "执行器自身不物化技能；create 路径上它不得对技能仓写盘（启用/禁用更不得碰）");
        Assert.AreEqual(SkillAdmissionActions.Create, outcome.Action);
        Assert.AreEqual("skill-new", outcome.SkillId);
        Assert.IsNull(outcome.DisplacedSkillId);
        Assert.IsFalse(outcome.DisplacementRolledBack);
    }

    [TestMethod]
    public async Task Create_WhenMaterializationFails_ShouldReportNoSkillId_WithoutWriting()
    {
        var store = new ProbeSkillStore();
        var executor = new SkillPortfolioAdmissionExecutor(store);

        var outcome = await executor.ExecuteAsync(Agent, CreateAdmission(), () => Task.FromResult<string?>(null));

        Assert.AreEqual(SkillAdmissionActions.Create, outcome.Action);
        Assert.IsNull(outcome.SkillId, "建不出来时不得谎报一个 id —— 调用方要靠它区分 promoted/skipped");
        Assert.AreEqual(0, store.Writes.Count);
    }

    // ───────────────────────── displace ─────────────────────────

    [TestMethod]
    public async Task Displace_ShouldDisableTargetBeforeMaterializing_ThenReportBothIds()
    {
        var store = new ProbeSkillStore();
        var executor = new SkillPortfolioAdmissionExecutor(store);
        var writesSeenFromCallback = -1;

        var outcome = await executor.ExecuteAsync(
            Agent,
            DisplaceAdmission("skill-lowest"),
            () =>
            {
                // 物化候选时目标**必须已经**被禁用：否则预算瞬间被突破（旧新并存）。
                writesSeenFromCallback = store.Writes.Count;
                return Task.FromResult<string?>("skill-replacement");
            });

        Assert.AreEqual(1, writesSeenFromCallback);
        Assert.AreEqual("set-enabled:skill-lowest:false", store.Writes[0]);
        Assert.AreEqual(SkillAdmissionActions.Displace, outcome.Action);
        Assert.AreEqual("skill-replacement", outcome.SkillId);
        Assert.AreEqual("skill-lowest", outcome.DisplacedSkillId);
        Assert.IsFalse(outcome.DisplacementRolledBack);
        Assert.IsFalse(store.IsEnabled("skill-lowest"), "置换成功后被置换者保持禁用（禁用而非删除 ⇒ 事后仍可人工恢复）");
    }

    [TestMethod]
    public async Task Displace_WhenMaterializationFails_ShouldRollBackTargetToEnabled()
    {
        var store = new ProbeSkillStore();
        var executor = new SkillPortfolioAdmissionExecutor(store);

        var outcome = await executor.ExecuteAsync(
            Agent,
            DisplaceAdmission("skill-lowest"),
            () => Task.FromResult<string?>(null));

        // 核心不变式：不允许出现"净减一个技能"的中间态。
        CollectionAssert.AreEqual(
            new[] { "set-enabled:skill-lowest:false", "set-enabled:skill-lowest:true" },
            store.Writes);
        Assert.IsTrue(store.IsEnabled("skill-lowest"));
        Assert.AreEqual(SkillAdmissionActions.Defer, outcome.Action);
        Assert.IsTrue(outcome.DisplacementRolledBack);
        Assert.IsNull(outcome.SkillId);
        Assert.AreEqual("skill-lowest", outcome.DisplacedSkillId, "回滚后仍要留下「曾经动过谁」的追责线索");
    }

    [TestMethod]
    public async Task Displace_WhenMaterializationThrows_ShouldRollBack_AndRethrow()
    {
        var store = new ProbeSkillStore();
        var executor = new SkillPortfolioAdmissionExecutor(store);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            Agent,
            DisplaceAdmission("skill-lowest"),
            () => throw new InvalidOperationException("store write blew up")));

        CollectionAssert.AreEqual(
            new[] { "set-enabled:skill-lowest:false", "set-enabled:skill-lowest:true" },
            store.Writes);
        Assert.IsTrue(store.IsEnabled("skill-lowest"));
    }

    [TestMethod]
    public async Task Displace_WithoutTargetSkillId_ShouldDefer_WithoutAnyWrite()
    {
        var store = new ProbeSkillStore();
        var executor = new SkillPortfolioAdmissionExecutor(store);
        var callbackInvoked = false;

        var outcome = await executor.ExecuteAsync(
            Agent,
            new SkillAdmissionResult
            {
                Action = SkillAdmissionActions.Displace,
                TargetSkillId = null,
                Confidence = 0.5,
                Reason = "displace_lowest_value",
            },
            () =>
            {
                callbackInvoked = true;
                return Task.FromResult<string?>("skill-replacement");
            });

        Assert.IsFalse(callbackInvoked, "fail-closed：没有置换目标就不许先建后算");
        Assert.AreEqual(0, store.Writes.Count, "fail-closed 路径必须一次写盘都没有");
        Assert.AreEqual(SkillAdmissionActions.Defer, outcome.Action);
        Assert.IsNull(outcome.DisplacedSkillId);
    }

    // ───────────────────────── 零副作用 ─────────────────────────

    [TestMethod]
    [DataRow(SkillAdmissionActions.Merge)]
    [DataRow(SkillAdmissionActions.Skip)]
    [DataRow(SkillAdmissionActions.Defer)]
    public async Task NonMutatingActions_ShouldNeverTouchTheStore(string action)
    {
        var store = new ProbeSkillStore();
        var executor = new SkillPortfolioAdmissionExecutor(store);
        var callbackInvoked = false;

        var outcome = await executor.ExecuteAsync(
            Agent,
            new SkillAdmissionResult { Action = action, TargetSkillId = "skill-any", Confidence = 0.5, Reason = "r" },
            () =>
            {
                callbackInvoked = true;
                return Task.FromResult<string?>("skill-replacement");
            });

        Assert.AreEqual(0, store.Writes.Count, $"{action} 是零副作用裁决，执行器不得碰技能仓");
        Assert.IsFalse(callbackInvoked);
        Assert.AreEqual(action, outcome.Action);
    }

    [TestMethod]
    public async Task Executor_ShouldRejectMissingDependencies()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new SkillPortfolioAdmissionExecutor(null!));

        var executor = new SkillPortfolioAdmissionExecutor(new ProbeSkillStore());
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => executor.ExecuteAsync(Agent, null!, () => Task.FromResult<string?>(null)));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => executor.ExecuteAsync(Agent, CreateAdmission(), null!));
    }

    // ───────────────────────── helpers ─────────────────────────

    private static SkillAdmissionResult CreateAdmission()
        => new() { Action = SkillAdmissionActions.Create, Confidence = 0.9, Reason = "from-dedup" };

    private static SkillAdmissionResult DisplaceAdmission(string targetSkillId)
        => new()
        {
            Action = SkillAdmissionActions.Displace,
            TargetSkillId = targetSkillId,
            Confidence = 0.9,
            Reason = "displace_lowest_value",
        };

    /// <summary>探针式假仓：记录每一次写盘的**顺序与参数**，并维护可查询的启用态。</summary>
    private sealed class ProbeSkillStore : IAgentSkillEvolutionStore
    {
        private readonly Dictionary<string, AgentSkillEvolutionDocument> _skills = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Writes { get; } = [];

        public bool IsEnabled(string skillId)
            => _skills.TryGetValue(skillId, out var skill) && skill.Enabled;

        public Task<AgentSkillEvolutionDocument?> GetAsync(
            string agentInstanceId,
            string skillId,
            CancellationToken ct = default)
            => Task.FromResult(_skills.TryGetValue(skillId, out var skill) ? skill : null);

        public Task<IReadOnlyList<AgentSkillEvolutionDocument>> ListAutoGeneratedAsync(
            string agentInstanceId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentSkillEvolutionDocument>>(new List<AgentSkillEvolutionDocument>(_skills.Values));

        public Task<AgentSkillEvolutionDocument> CreateAsync(
            string agentInstanceId,
            AgentSkillEvolutionWriteRequest request,
            CancellationToken ct = default)
        {
            Writes.Add("create:" + request.SkillId);
            var created = new AgentSkillEvolutionDocument
            {
                SkillId = request.SkillId,
                Name = request.Name,
                Version = request.Version,
                Description = request.Description,
                Tags = request.Tags,
                Keywords = request.Keywords,
                Markdown = request.Markdown,
                Enabled = true,
            };
            _skills[created.SkillId] = created;
            return Task.FromResult(created);
        }

        public Task<AgentSkillEvolutionDocument> UpdateAsync(
            string agentInstanceId,
            string skillId,
            AgentSkillEvolutionWriteRequest request,
            CancellationToken ct = default)
        {
            Writes.Add("update:" + skillId);
            return Task.FromResult(_skills[skillId]);
        }

        public Task<AgentSkillEvolutionDocument> SetEnabledAsync(
            string agentInstanceId,
            string skillId,
            bool enabled,
            CancellationToken ct = default)
        {
            Writes.Add($"set-enabled:{skillId}:{enabled.ToString().ToLowerInvariant()}");
            var current = _skills.TryGetValue(skillId, out var existing)
                ? existing
                : new AgentSkillEvolutionDocument
                {
                    SkillId = skillId,
                    Name = skillId,
                    Version = "1.0.0",
                    Markdown = "# stub",
                };
            var updated = current with { Enabled = enabled };
            _skills[skillId] = updated;
            return Task.FromResult(updated);
        }
    }
}
