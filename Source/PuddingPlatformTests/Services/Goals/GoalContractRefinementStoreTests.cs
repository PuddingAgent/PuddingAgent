using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Goals;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// G92-1 S1-c 片6 3a：GoalContractRefinementStore 的 CAS 提交存储测试（隔离内存 SQLite）。
/// 覆盖：①合法 proposal 原地 CAS + 同事务审计；②版本陈旧拒绝；③source 非 bounded_planning
/// 拒绝（一次性闸）；④同 operation key 重放 already-applied（不二次覆盖）；⑤自报字段拒绝；
/// ⑥无 proposal/目标非 Active/身份不一致拒绝；审计 payload 无自然语言。
/// </summary>
[TestClass]
public sealed class GoalContractRefinementStoreTests
{
    private const string GoalRunId = "goal-1";
    private const string ContractId = "gc-goal-1-1-3";
    private const string Objective = "只输出 READY";

    private static async Task<(SqliteConnection Connection, IDbContextFactory<PlatformDbContext> Factory)>
        CreateAsync()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var db = await factory.CreateDbContextAsync();
        db.GoalRuns.Add(new GoalRunEntity
        {
            GoalRunId = GoalRunId,
            WorkspaceId = "ws-1",
            CurrentConversationId = "conv-1",
            AgentInstanceId = "agent-1",
            Objective = Objective,
            ObjectiveVersion = 3,
            Status = GoalPhase.Active,
            ActivationEpoch = 1,
            AggregateVersion = 7,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        db.GoalAcceptanceContracts.Add(new GoalAcceptanceContractEntity
        {
            ContractId = ContractId,
            GoalRunId = GoalRunId,
            ActivationEpoch = 1,
            ObjectiveVersion = 3,
            ContractVersion = 1,
            CriteriaJson = """[{"id":"build","revision":1,"requirement":"构建通过","required":true,"kind":"build"}]""",
            ChecksJson = "[]",
            Source = GoalAcceptanceContractSources.BoundedPlanning,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (connection, factory);
    }

    private static GoalContractProposalFacts Facts(
        string? proposalJson = null,
        string objective = Objective,
        int activationEpoch = 1,
        int objectiveVersion = 3,
        string turnId = "turn-1") => new()
    {
        GoalRunId = GoalRunId,
        ActivationEpoch = activationEpoch,
        ObjectiveVersion = objectiveVersion,
        TurnId = turnId,
        AggregateVersion = 7,
        Objective = objective,
        TerminalKind = "completed",
        EvidenceComplete = true,
        ProposalJson = proposalJson ?? ValidProposalJson(),
    };

    private static string ValidProposalJson(int expectedVersion = 1, string expectedText = "READY")
        => $$$"""
            {"schemaVersion":1,"kind":"refine_acceptance_contract","expectedContractVersion":{{{expectedVersion}}},"criteria":[{"requirement":"最终输出必须严格等于期望文本","requirementRefs":["只输出 READY"],"verification":{"kind":"text-assertion","definitionRef":"checks/text-assertion.md#equals","inputRefs":[],"expectedText":"{{{expectedText}}}"}}]}
            """;

    // ── ① 合法 proposal：CAS 成功 + source 变更 + 同事务审计 ──

    [TestMethod]
    public async Task Apply_ValidProposal_CasUpdatesContractAndWritesAuditEvent()
    {
        var (connection, factory) = await CreateAsync();
        await using var _ = connection;
        var store = new GoalContractRefinementStore(factory);

        var result = await store.TryApplyAsync(Facts());

        Assert.AreEqual(GoalContractRefinementStatus.Applied, result.Status, result.Reason);
        Assert.AreEqual(2, result.ContractVersion);

        await using var db = await factory.CreateDbContextAsync();
        var contract = await db.GoalAcceptanceContracts.SingleAsync(item => item.ContractId == ContractId);
        Assert.AreEqual(GoalAcceptanceContractSources.AgentRefined, contract.Source);
        Assert.AreEqual(2, contract.ContractVersion);
        Assert.IsNull(contract.PlanFingerprint); // 计划指纹保留原值（整理不改计划绑定事实）

        var criteria = GoalVerificationPersistence.ReadCriteria(contract.CriteriaJson);
        Assert.AreEqual(1, criteria.Count);
        Assert.AreEqual(GoalVerificationSpecKinds.TextAssertion, criteria[0].Kind);
        Assert.IsTrue(criteria[0].DefinitionHash!.StartsWith("sha256:", StringComparison.Ordinal));
        var checks = GoalVerificationPersistence.ReadChecks(contract.ChecksJson);
        Assert.AreEqual("READY", checks[0].ExpectedText);
        Assert.AreEqual(criteria[0].DefinitionHash, checks[0].DefinitionHash);

        // 审计事件：同事务恰好一条，payload 只存 operation key/版本/IDs/hash，无自然语言。
        var audit = await db.ConversationEvents
            .SingleAsync(item => item.Type == GoalEventTypes.ContractRefined);
        Assert.AreEqual("goal", audit.SourceKind);
        Assert.AreEqual(GoalRunId, audit.CorrelationId);
        Assert.AreEqual("turn-1", audit.CausationId);
        using var payload = JsonDocument.Parse(audit.Payload);
        Assert.AreEqual(1, payload.RootElement.GetProperty("oldContractVersion").GetInt32());
        Assert.AreEqual(2, payload.RootElement.GetProperty("newContractVersion").GetInt32());
        Assert.AreEqual(result.OperationKey, payload.RootElement.GetProperty("operationKey").GetString());
        Assert.IsFalse(audit.Payload.Contains("READY", StringComparison.Ordinal));
        Assert.IsFalse(audit.Payload.Contains("最终输出", StringComparison.Ordinal));
    }

    // ── ② expectedContractVersion 不匹配 ⇒ 陈旧拒绝，合同原样 ──

    [TestMethod]
    public async Task Apply_VersionMismatch_ReturnsStaleAndLeavesContractUntouched()
    {
        var (connection, factory) = await CreateAsync();
        await using var _ = connection;
        var store = new GoalContractRefinementStore(factory);

        var result = await store.TryApplyAsync(Facts(ValidProposalJson(expectedVersion: 9)));

        Assert.AreEqual(GoalContractRefinementStatus.StaleContract, result.Status);
        Assert.AreEqual(1, result.ContractVersion);
        await using var db = await factory.CreateDbContextAsync();
        var contract = await db.GoalAcceptanceContracts.SingleAsync(item => item.ContractId == ContractId);
        Assert.AreEqual(GoalAcceptanceContractSources.BoundedPlanning, contract.Source);
        Assert.AreEqual(1, contract.ContractVersion);
        Assert.AreEqual(0, await db.ConversationEvents
            .CountAsync(item => item.Type == GoalEventTypes.ContractRefined));
    }

    // ── ③ source 非 bounded_planning ⇒ 一次性闸拒绝（不同提议不得二次覆盖）──

    [TestMethod]
    public async Task Apply_SourceAlreadyRefined_ReturnsNeedsUserAndLeavesContractUntouched()
    {
        var (connection, factory) = await CreateAsync();
        await using var _ = connection;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var contract = await db.GoalAcceptanceContracts.SingleAsync(item => item.ContractId == ContractId);
            contract.Source = GoalAcceptanceContractSources.AgentRefined;
            await db.SaveChangesAsync();
        }

        var store = new GoalContractRefinementStore(factory);
        // 不同 turn ⇒ 不同 operation key（无重放命中）⇒ 冲突转 needs_user。
        var result = await store.TryApplyAsync(Facts(ValidProposalJson(), turnId: "turn-2"));

        Assert.AreEqual(GoalContractRefinementStatus.NeedsUser, result.Status);
        Assert.AreEqual(1, result.ContractVersion);
        await using var verify = await factory.CreateDbContextAsync();
        var contract2 = await verify.GoalAcceptanceContracts.SingleAsync(item => item.ContractId == ContractId);
        Assert.AreEqual(GoalAcceptanceContractSources.AgentRefined, contract2.Source);
        Assert.AreEqual(1, contract2.ContractVersion);
        Assert.AreEqual(0, await verify.ConversationEvents
            .CountAsync(item => item.Type == GoalEventTypes.ContractRefined));
    }

    // ── ④ 同 operation key 重放 ⇒ already-applied，不二次覆盖 ──

    [TestMethod]
    public async Task Apply_ReplayedOperationKey_ReturnsAlreadyAppliedWithoutSecondWrite()
    {
        var (connection, factory) = await CreateAsync();
        await using var _ = connection;
        var store = new GoalContractRefinementStore(factory);

        var first = await store.TryApplyAsync(Facts());
        Assert.AreEqual(GoalContractRefinementStatus.Applied, first.Status, first.Reason);

        var replay = await store.TryApplyAsync(Facts());
        Assert.AreEqual(GoalContractRefinementStatus.AlreadyApplied, replay.Status);
        Assert.AreEqual(first.OperationKey, replay.OperationKey);
        Assert.AreEqual(2, replay.ContractVersion);

        await using var db = await factory.CreateDbContextAsync();
        var contract = await db.GoalAcceptanceContracts.SingleAsync(item => item.ContractId == ContractId);
        Assert.AreEqual(GoalAcceptanceContractSources.AgentRefined, contract.Source);
        Assert.AreEqual(2, contract.ContractVersion);
        Assert.AreEqual(1, GoalVerificationPersistence.ReadCriteria(contract.CriteriaJson).Count);
        Assert.AreEqual(1, await db.ConversationEvents
            .CountAsync(item => item.Type == GoalEventTypes.ContractRefined));
    }

    // ── ⑤ proposal 自报身份/hash 字段 ⇒ 拒绝且不触碰合同（D1–D3 核心防线）──

    [TestMethod]
    public async Task Apply_SelfReportedIdentityFields_AreRejectedBeforeAnyWrite()
    {
        var (connection, factory) = await CreateAsync();
        await using var _ = connection;
        var store = new GoalContractRefinementStore(factory);

        var forged = "{\"schemaVersion\":1,\"kind\":\"refine_acceptance_contract\","
            + "\"expectedContractVersion\":1,\"activationEpoch\":99,"
            + "\"criteria\":[{\"requirement\":\"r\",\"requirementRefs\":[\"只输出 READY\"],"
            + "\"verification\":{\"kind\":\"text-assertion\",\"definitionRef\":"
            + "\"checks/text-assertion.md#equals\",\"inputRefs\":[],\"expectedText\":\"READY\"}}]}";

        var result = await store.TryApplyAsync(Facts(forged));

        Assert.AreEqual(GoalContractRefinementStatus.Rejected, result.Status);
        Assert.IsTrue(result.Reason!.Contains("agent-forbidden field 'activationEpoch'", StringComparison.Ordinal));
        await using var db = await factory.CreateDbContextAsync();
        var contract = await db.GoalAcceptanceContracts.SingleAsync(item => item.ContractId == ContractId);
        Assert.AreEqual(GoalAcceptanceContractSources.BoundedPlanning, contract.Source);
        Assert.AreEqual(1, contract.ContractVersion);
        Assert.AreEqual(0, await db.ConversationEvents
            .CountAsync(item => item.Type == GoalEventTypes.ContractRefined));
    }

    // ── ⑥ 兼容/状态拒绝路径：无 proposal、目标非 Active、candidate 身份与 DB 不一致 ──

    [TestMethod]
    public async Task Apply_WithoutProposalPayload_IsRejectedAndContractUntouched()
    {
        var (connection, factory) = await CreateAsync();
        await using var _ = connection;
        var store = new GoalContractRefinementStore(factory);

        var result = await store.TryApplyAsync(Facts(proposalJson: ""));

        Assert.AreEqual(GoalContractRefinementStatus.Rejected, result.Status);
        await using var db = await factory.CreateDbContextAsync();
        var contract = await db.GoalAcceptanceContracts.SingleAsync(item => item.ContractId == ContractId);
        Assert.AreEqual(GoalAcceptanceContractSources.BoundedPlanning, contract.Source);
        Assert.AreEqual(1, contract.ContractVersion);
    }

    [TestMethod]
    public async Task Apply_InactiveGoal_IsRejected()
    {
        var (connection, factory) = await CreateAsync();
        await using var _ = connection;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var goal = await db.GoalRuns.SingleAsync(item => item.GoalRunId == GoalRunId);
            goal.Status = GoalPhase.Cancelled;
            await db.SaveChangesAsync();
        }

        var store = new GoalContractRefinementStore(factory);
        var result = await store.TryApplyAsync(Facts());

        Assert.AreEqual(GoalContractRefinementStatus.Rejected, result.Status);
        Assert.IsTrue(result.Reason!.Contains("active", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Apply_CandidateIdentityMismatchWithDb_IsRejected()
    {
        var (connection, factory) = await CreateAsync();
        await using var _ = connection;
        var store = new GoalContractRefinementStore(factory);

        // candidate 自报的 objective 与持久 Goal 不一致 ⇒ fail-closed（身份以 DB 为准）。
        // 注意：带全角句号的变体能通过内容级覆盖门（词元相同），才能走到 DB 身份比对。
        var result = await store.TryApplyAsync(Facts(objective: "只输出 READY。"));

        Assert.AreEqual(GoalContractRefinementStatus.Rejected, result.Status);
        Assert.IsTrue(result.Reason!.Contains("does not match the persisted goal", StringComparison.Ordinal));
        await using var db = await factory.CreateDbContextAsync();
        Assert.AreEqual(0, await db.ConversationEvents
            .CountAsync(item => item.Type == GoalEventTypes.ContractRefined));
    }
}
