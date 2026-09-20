using PuddingCode.Classification;
using PuddingCode.Tools;
using PuddingRuntime.Classification;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Classification;

/// <summary>
/// <see cref="SystemRuleClassifier"/> 的离线单测（方案 v2 §14.12.2 / §1 / §11.3 / §14.5，切片 S2b）。
/// <para>
/// 覆盖映射：allow 命中 ⇒ AllowOnce（01）、deny 命中 ⇒ DenyOnce 候选而非 DenyPermanent（02）、
/// 冲突 deny_wins（03）、未命中 ⇒ Unknown（04）、禁用规则不参与匹配（05）、
/// store 异常 fail-closed 不冒泡（06）、OperationCanceledException 传播（07）、
/// 溯源字段完备性（08）、LatencyMs 经注入时钟计量（09）、非命令工具按参数哈希匹配（10）。
/// 全部用例零网络、注入固定时钟、不接触任何真实模型。
/// </para>
/// </summary>
[TestClass]
public sealed class SystemRuleClassifierTests
{
    private const string TestWorkspace = "ws-test";
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    // —— 01 命中 allow 规则 ⇒ AllowOnce（§14.12.2 快路径候选） ——

    [TestMethod]
    public async Task ClassifyAsync_AllowRuleHit_ReturnsAllowOnceWithRuleId()
    {
        var (classifier, store, _) = CreateClassifier();
        await store.SaveAsync(Rule("rule-allow-1", ToolApprovalRuleEffect.Allow));

        var verdict = await classifier.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.AllowOnce, verdict.Outcome, "allow 命中必须是 AllowOnce。");
        Assert.AreEqual(SystemRuleClassifier.ReasonAllowHit, verdict.ReasonCode);
        Assert.AreEqual("rule-allow-1", verdict.AppliedRuleId, "AppliedRuleId 必须指向命中规则。");
    }

    // —— 02 命中 deny 规则 ⇒ DenyOnce 候选（绝不是 DenyPermanent，覆盖由上层管线执行，§11.3） ——

    [TestMethod]
    public async Task ClassifyAsync_DenyRuleHit_ReturnsDenyOnceCandidate_NeverPermanent()
    {
        var (classifier, store, _) = CreateClassifier();
        await store.SaveAsync(Rule("rule-deny-1", ToolApprovalRuleEffect.Deny));

        var verdict = await classifier.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.DenyOnce, verdict.Outcome, "deny 命中是候选结论。");
        Assert.AreNotEqual(
            ClassificationOutcome.DenyPermanent,
            verdict.Outcome,
            "系统规则 deny 是候选，绝不返回 DenyPermanent（§11.3 覆盖权）。");
        Assert.AreNotEqual(ClassificationOutcome.AllowPermanent, verdict.Outcome, "也不得返回任何永久结论。");
        Assert.AreEqual(SystemRuleClassifier.ReasonDenyCandidate, verdict.ReasonCode);
        Assert.AreEqual("rule-deny-1", verdict.AppliedRuleId);
    }

    // —— 03 同键同时命中 allow 与 deny ⇒ §14.12.4 deny_wins，AppliedRuleId 指向 deny 规则 ——

    [TestMethod]
    public async Task ClassifyAsync_AllowDenyConflict_ReturnsDenyWinsCandidate()
    {
        var (classifier, store, _) = CreateClassifier();
        await store.SaveAsync(Rule("rule-allow-1", ToolApprovalRuleEffect.Allow));
        await store.SaveAsync(Rule("rule-deny-1", ToolApprovalRuleEffect.Deny));

        var verdict = await classifier.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.DenyOnce, verdict.Outcome, "冲突时生效 deny（deny_wins）。");
        Assert.AreEqual(SystemRuleClassifier.ReasonConflictDenyWins, verdict.ReasonCode);
        Assert.AreEqual("rule-deny-1", verdict.AppliedRuleId, "AppliedRuleId 必须指向生效的 deny 规则。");
    }

    // —— 04 未命中 ⇒ Unknown（绝不是 AllowOnce/DenyOnce） ——

    [TestMethod]
    public async Task ClassifyAsync_NoRuleMatch_ReturnsUnknown()
    {
        var (classifier, store, _) = CreateClassifier();

        var empty = await classifier.ClassifyAsync(ToolContext(command: "dotnet build"));
        Assert.AreEqual(ClassificationOutcome.Unknown, empty.Outcome, "空 store 必须返回 Unknown。");
        Assert.AreEqual(SystemRuleClassifier.ReasonNoMatch, empty.ReasonCode);
        Assert.IsNull(empty.AppliedRuleId, "未命中不得携带 AppliedRuleId。");

        // 键不同（命令不同）也不命中：逐字节相等，无任何通配语义。
        await store.SaveAsync(Rule("rule-allow-1", ToolApprovalRuleEffect.Allow, command: "git status"));
        var otherCommand = await classifier.ClassifyAsync(ToolContext(command: "git status --short"));
        Assert.AreEqual(ClassificationOutcome.Unknown, otherCommand.Outcome, "命令不同必须不命中。");
        Assert.AreEqual(SystemRuleClassifier.ReasonNoMatch, otherCommand.ReasonCode);
    }

    // —— 05 Status=Disabled 的规则不参与匹配（§14.12.7） ——

    [TestMethod]
    public async Task ClassifyAsync_DisabledRule_IsExcludedFromMatching()
    {
        var (classifier, store, _) = CreateClassifier();
        await store.SaveAsync(Rule(
            "rule-disabled", ToolApprovalRuleEffect.Allow,
            status: ToolApprovalAllowlistRuleStatus.Disabled));

        var verdict = await classifier.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.Unknown, verdict.Outcome, "禁用规则不得参与匹配。");
        Assert.AreEqual(SystemRuleClassifier.ReasonNoMatch, verdict.ReasonCode);

        // 对照：同一键的规则恢复 Enabled 后应命中（证明 05 未命中确因 Status 过滤）。
        await store.SaveAsync(Rule("rule-disabled", ToolApprovalRuleEffect.Allow));
        var enabled = await classifier.ClassifyAsync(ToolContext());
        Assert.AreEqual(ClassificationOutcome.AllowOnce, enabled.Outcome, "同规则 Enabled 后必须命中。");
    }

    // —— 06 store 抛异常 ⇒ Unknown + store_error，且绝不冒泡（fail-closed） ——

    [TestMethod]
    public async Task ClassifyAsync_StoreThrows_ReturnsUnknownStoreError_WithoutBubble()
    {
        var classifier = new SystemRuleClassifier(new ThrowingAllowlistStore(), new FakeClock());

        var verdict = await classifier.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.Unknown, verdict.Outcome, "store 异常必须折叠为 Unknown。");
        Assert.AreEqual(SystemRuleClassifier.ReasonStoreError, verdict.ReasonCode);
        Assert.IsFalse(string.IsNullOrEmpty(verdict.Reason), "降级场景也必须写明原因。");
        Assert.IsNull(verdict.AppliedRuleId);
    }

    // —— 07 OperationCanceledException 照常传播（不得折叠成 Unknown） ——

    [TestMethod]
    public async Task ClassifyAsync_Cancellation_PropagatesOperationCanceledException()
    {
        var (classifier, _, clock) = CreateClassifier();

        // ① 取消令牌已取消：分类器在 store IO 前的取消检查即抛出。
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => classifier.ClassifyAsync(ToolContext(), new CancellationToken(canceled: true)),
            "已取消令牌必须让 OCE 照常传播。");

        // ② 令牌未取消但 store 本身抛 OCE：同样必须传播，不得被 fail-closed 分支吞掉。
        var cancelingClassifier = new SystemRuleClassifier(new OperationCanceledAllowlistStore(), clock);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => cancelingClassifier.ClassifyAsync(ToolContext()),
            "store 抛出的 OCE 同样必须传播。");
    }

    // —— 08 溯源契约：每个结论都有非空 Reason / ReasonCode / ClassifierId，纯规则实现无模型 ——

    [TestMethod]
    public async Task ClassifyAsync_EveryVerdict_CarriesProvenanceAndNoModel()
    {
        var (classifier, store, _) = CreateClassifier();
        await store.SaveAsync(Rule("rule-allow-1", ToolApprovalRuleEffect.Allow));

        var verdicts = new[]
        {
            await classifier.ClassifyAsync(ToolContext()),                                          // allow 命中
            await classifier.ClassifyAsync(ToolContext(command: "never-matched-command")),          // 未命中
        };

        foreach (var verdict in verdicts)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(verdict.Reason), "Reason 不得为空。");
            Assert.IsFalse(string.IsNullOrWhiteSpace(verdict.ReasonCode), "ReasonCode 不得为空。");
            Assert.AreEqual("system-rules", verdict.ClassifierId, "ClassifierId 必须是 system-rules。");
            Assert.IsNull(verdict.ClassifierModel, "纯规则实现不得声明模型。");
            Assert.IsTrue(verdict.LatencyMs.HasValue, "默认实现应产出耗时计量。");
        }
    }

    // —— 09 LatencyMs 用注入的 TimeProvider 计量（§14.12.9 可测性） ——

    [TestMethod]
    public async Task ClassifyAsync_LatencyMs_MeasuredViaInjectedTimeProvider()
    {
        // 门控 store：把 store IO 挂起，确保时钟推进落在「计时开始」与「计量落笔」之间。
        var store = new GatedAllowlistStore();
        var clock = new FakeClock();
        var classifier = new SystemRuleClassifier(store, clock);

        var task = classifier.ClassifyAsync(ToolContext());
        clock.Advance(TimeSpan.FromMilliseconds(5));
        store.Release();
        var verdict = await task;

        Assert.IsTrue(verdict.LatencyMs.HasValue, "LatencyMs 必须有值。");
        Assert.IsTrue(
            verdict.LatencyMs!.Value >= 4.9,
            $"LatencyMs 应基于注入时钟（期望 ≥5ms，实际 {verdict.LatencyMs.Value}）。");
    }

    // —— 10 非命令工具按参数哈希匹配（键序 / 空白差异不影响命中，§14.12.1） ——

    [TestMethod]
    public async Task ClassifyAsync_NonCommandTool_MatchesByCanonicalArgumentsHash()
    {
        var (classifier, store, _) = CreateClassifier();
        await store.SaveAsync(Rule(
            "rule-args-1", ToolApprovalRuleEffect.Allow,
            command: null,
            argumentsJson: "{\"path\":\"a.txt\"}",
            shell: null,
            toolId: "file_read"));

        // 请求侧 JSON 键序与空白不同 ⇒ 规范化后 subject 相同，必须命中。
        var verdict = await classifier.ClassifyAsync(ToolContext(
            command: null,
            argumentsJson: "{\n  \"path\" : \"a.txt\"\n}",
            shell: null,
            toolId: "file_read"));

        Assert.AreEqual(ClassificationOutcome.AllowOnce, verdict.Outcome, "参数哈希规范化后应命中。");
        Assert.AreEqual("rule-args-1", verdict.AppliedRuleId);
        Assert.AreEqual(SystemRuleClassifier.ReasonAllowHit, verdict.ReasonCode);
    }

    // —— 测试辅助 ——

    private static (SystemRuleClassifier, InMemoryToolApprovalAllowlistStore, FakeClock) CreateClassifier()
    {
        var store = new InMemoryToolApprovalAllowlistStore();
        var clock = new FakeClock();
        return (new SystemRuleClassifier(store, clock), store, clock);
    }

    private static ToolCallClassificationContext ToolContext(
        string? command = "git status",
        string? argumentsJson = null,
        string? workingDirectory = "E:/repo",
        string? shell = "pwsh",
        string toolId = "terminal_execute",
        string workspaceId = TestWorkspace)
        => new()
        {
            ToolId = toolId,
            CommandName = command,
            ArgumentsJson = argumentsJson,
            WorkingDirectory = workingDirectory,
            Shell = shell,
            WorkspaceId = workspaceId,
            SessionId = "sess-1",
            AgentInstanceId = "agent-1",
            UserId = "user-1",
        };

    private static ToolApprovalAllowlistRule Rule(
        string ruleId,
        ToolApprovalRuleEffect effect,
        string? command = "git status",
        string? argumentsJson = null,
        ToolApprovalAllowlistRuleSource source = ToolApprovalAllowlistRuleSource.Human,
        ToolApprovalAllowlistRuleStatus status = ToolApprovalAllowlistRuleStatus.Enabled,
        string? workingDirectory = "E:/repo",
        string? shell = "pwsh",
        string toolId = "terminal_execute",
        string? workspaceId = TestWorkspace)
        => new()
        {
            RuleId = ruleId,
            WorkspaceId = workspaceId,
            ToolId = toolId,
            Command = command,
            ArgumentsJson = argumentsJson,
            Source = source,
            Status = status,
            Effect = effect,
            CreatedAtUtc = T0,
            WorkingDirectory = workingDirectory,
            Shell = shell,
        };

    /// <summary>固定时钟：时间与计时刻度只经注入的 TimeProvider 获取（§14.12.9 无静态可变状态）。</summary>
    private sealed class FakeClock : TimeProvider
    {
        private long _timestamp;

        public DateTimeOffset Now { get; private set; } = T0;

        public void Advance(TimeSpan delta)
        {
            Now += delta;
            _timestamp += (long)(delta.TotalSeconds * TimestampFrequency);
        }

        public override DateTimeOffset GetUtcNow() => Now;

        public override long GetTimestamp() => _timestamp;
    }

    /// <summary>全部读操作抛普通异常的 allowlist store 存根（验证 fail-closed 不冒泡）。</summary>
    private sealed class ThrowingAllowlistStore : IToolApprovalAllowlistStore
    {
        public Task SaveAsync(ToolApprovalAllowlistRule rule, CancellationToken ct = default) => Task.CompletedTask;

        public Task<ToolApprovalAllowlistRule?> GetAsync(string ruleId, CancellationToken ct = default)
            => throw new InvalidOperationException("get-boom");

        public Task<IReadOnlyList<ToolApprovalAllowlistRule>> ListAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("list-boom");
    }

    /// <summary>读操作抛 OperationCanceledException 的 store 存根（验证 OCE 传播不被吞掉）。</summary>
    private sealed class OperationCanceledAllowlistStore : IToolApprovalAllowlistStore
    {
        public Task SaveAsync(ToolApprovalAllowlistRule rule, CancellationToken ct = default) => Task.CompletedTask;

        public Task<ToolApprovalAllowlistRule?> GetAsync(string ruleId, CancellationToken ct = default)
            => throw new OperationCanceledException("get-canceled");

        public Task<IReadOnlyList<ToolApprovalAllowlistRule>> ListAsync(CancellationToken ct = default)
            => throw new OperationCanceledException("list-canceled");
    }

    /// <summary>门控 store：ListAsync 挂起直到显式 Release（用于验证 LatencyMs 基于注入时钟计量）。</summary>
    private sealed class GatedAllowlistStore : IToolApprovalAllowlistStore
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public Task SaveAsync(ToolApprovalAllowlistRule rule, CancellationToken ct = default) => Task.CompletedTask;

        public Task<ToolApprovalAllowlistRule?> GetAsync(string ruleId, CancellationToken ct = default)
            => Task.FromResult<ToolApprovalAllowlistRule?>(null);

        public async Task<IReadOnlyList<ToolApprovalAllowlistRule>> ListAsync(CancellationToken ct = default)
        {
            await _gate.Task.ConfigureAwait(false);
            return [];
        }
    }
}
