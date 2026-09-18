using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Goals;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// G92-1 S1-c（片 3）：Runner 的 text-assertion 分支（判定与证据）。
/// 锁定：无 WorkingDirectory 仍产出终态报告；ordinal 精确匹配——大小写 / 前后空白 / contains
/// 差异一律 failed；终态回复不可得 ⇒ 终态 failed（evidence_missing，不回 pending）；
/// build/test/file-evidence 的 WorkingDirectory 门禁行为保持不变。
/// </summary>
[TestClass]
public sealed class GoalCheckTextAssertionRunnerTests
{
    private const string TurnId = "turn-1";
    private const long ReplySequence = 7;

    /// <summary>进程桩：StartAsync 即抛——text-assertion 与门禁路径绝不启动进程，被调用即测试失败。</summary>
    private sealed class NeverProcesses : ITerminalProcessManager
    {
        public Task<TerminalProcessInfo> StartAsync(
            string sessionId, string command, string workingDir, CancellationToken ct = default)
            => throw new InvalidOperationException("text-assertion/gated checks must never start a process.");

        public IAsyncEnumerable<string> SubscribeAsync(string processId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<TerminalOutputSnapshot?> ReadOutputAsync(
            string processId, int offset = 0, int? maxLines = null, int? maxChars = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> WriteInputAsync(string processId, string input, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<bool> KillAsync(string processId) => Task.FromResult(false);

        public IReadOnlyList<TerminalProcessInfo> ListProcesses(string? sessionId = null) => [];

        public Task<int> ReapAsync() => Task.FromResult(0);
    }

    /// <summary>准入桩：本文件验证纯文本判定与门禁语义，不验证准入策略。</summary>
    private sealed class AllowAllAdmission : ITerminalCommandAdmission
    {
        public void EnsureAllowed(string command, bool isYoloMode)
        {
        }
    }

    private static GoalCheckSpec TextSpec(string checkId, string expectedText, string fingerprint = "fp-1") => new()
    {
        CheckId = checkId,
        CriterionId = "criterion-1",
        CriterionRevision = 1,
        Kind = GoalVerificationSpecKinds.TextAssertion,
        DefinitionRef = GoalCheckDefinitionRegistry.TextAssertionRef,
        DefinitionHash = GoalCheckDefinitionRegistry.TryGetDefinitionHash(
            GoalCheckDefinitionRegistry.TextAssertionRef, out var hash) ? hash : string.Empty,
        InputFingerprint = fingerprint,
        ExecutorRole = "core",
        ExpectedText = expectedText,
    };

    private static GoalCheckSpec GateSpec(string checkId, string kind, string definitionRef, string inputRef, string fingerprint) => new()
    {
        CheckId = checkId,
        CriterionId = "criterion-1",
        CriterionRevision = 1,
        Kind = kind,
        DefinitionRef = definitionRef,
        DefinitionHash = "hash-a",
        InputRefs = [inputRef],
        InputFingerprint = fingerprint,
        ExecutorRole = "core",
    };

    private static GoalCheckContext Context(string? workingDirectory, GoalFinalAssistantReply? reply) => new()
    {
        GoalRunId = "goal-1",
        ActivationEpoch = 1,
        WorkspaceId = "ws",
        AgentInstanceId = "agent-1",
        IterationNo = 1,
        Scope = GoalVerificationScopes.WorkUnit,
        WorkingDirectory = workingDirectory,
        TimeoutSeconds = 30,
        FinalAssistantReply = reply,
    };

    private static GoalFinalAssistantReply Reply(string text) => new()
    {
        TurnId = TurnId,
        Sequence = ReplySequence,
        Text = text,
    };

    [TestMethod]
    public async Task TextAssertion_WithoutWorkingDirectory_ExactMatch_IsTerminalPassed()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var runner = new GoalCheckRunner(store, new NeverProcesses(), new AllowAllAdmission());

        var reports = await runner.RunAsync(
            [TextSpec("check-text-green", "READY")],
            Context(workingDirectory: null, reply: Reply("READY")));

        Assert.AreEqual(1, reports.Count);
        Assert.AreEqual(GoalCriterionResultStatuses.Passed, reports[0].Status);
        Assert.IsNull(reports[0].FailureCode);
        Assert.AreEqual(1, reports[0].EvidenceRefs.Count);
        Assert.AreEqual($"assistant-output:{TurnId}@{ReplySequence}", reports[0].EvidenceRefs[0]);
    }

    [TestMethod]
    public async Task TextAssertion_CaseWhitespaceAndContainsDifferences_AreFailed()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var runner = new GoalCheckRunner(store, new NeverProcesses(), new AllowAllAdmission());

        var cases = new (string Expected, string Reply)[]
        {
            ("OK", "ok"),   // 大小写差异
            ("OK", " OK"),  // 前导空白（不 Trim）
            ("OK", "OK "),  // 尾随空白（不 Trim）
            ("OK", "OK!"),  // 期望是回复前缀（不得当作 contains 通过）
            ("OK", "xOKx"), // contains 关系
        };

        for (var i = 0; i < cases.Length; i++)
        {
            var (expected, reply) = cases[i];
            var reports = await runner.RunAsync(
                [TextSpec($"check-text-neg-{i}", expected, $"fp-case-{i}")],
                Context(workingDirectory: null, reply: Reply(reply)));

            var report = reports.Single(item =>
                string.Equals(item.CheckId, $"check-text-neg-{i}", StringComparison.Ordinal));
            Assert.AreEqual(
                GoalCriterionResultStatuses.Failed, report.Status, $"{expected} vs {reply}");
            Assert.AreEqual(
                $"assistant-output:{TurnId}@{ReplySequence}",
                report.EvidenceRefs.Single(),
                $"{expected} vs {reply}");
        }
    }

    [TestMethod]
    public async Task TextAssertion_ReplyUnavailable_IsTerminalFailedNotPending()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var runner = new GoalCheckRunner(store, new NeverProcesses(), new AllowAllAdmission());

        var reports = await runner.RunAsync(
            [TextSpec("check-text-missing", "READY")],
            Context(workingDirectory: null, reply: null));

        Assert.AreEqual(1, reports.Count);
        Assert.AreEqual(GoalCriterionResultStatuses.Failed, reports[0].Status);
        Assert.AreEqual(GoalCheckFailureCodes.EvidenceMissing, reports[0].FailureCode);
        Assert.AreEqual(0, reports[0].EvidenceRefs.Count);

        // 「不回 pending」落在持久层：记录必须是 finished 终态，而不是 pending 重试。
        var records = await store.ReadForEpochAsync("goal-1", 1, CancellationToken.None);
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(GoalCheckRecordStatuses.Finished, records[0].Status);
    }

    [TestMethod]
    public async Task BuildTestFileEvidence_WithoutWorkingDirectory_StillEvidenceMissing()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var runner = new GoalCheckRunner(store, new NeverProcesses(), new AllowAllAdmission());

        var checks = new GoalCheckSpec[]
        {
            GateSpec("check-build-gate", GoalVerificationSpecKinds.Build,
                "checks/build.md#dotnet-build", "Source/PuddingCoreTests/PuddingCoreTests.csproj", "fp-build"),
            GateSpec("check-test-gate", GoalVerificationSpecKinds.Test,
                "checks/test.md#dotnet-test", "Source/PuddingCoreTests/PuddingCoreTests.csproj", "fp-test"),
            GateSpec("check-file-gate", GoalVerificationSpecKinds.FileEvidence,
                "checks/file-evidence.md#file-exists-nonempty", "README.md", "fp-file"),
        };

        var reports = await runner.RunAsync(checks, Context(workingDirectory: null, reply: Reply("READY")));

        Assert.AreEqual(3, reports.Count);
        foreach (var report in reports)
        {
            Assert.AreEqual(GoalCriterionResultStatuses.Failed, report.Status, report.CheckId);
            Assert.AreEqual(GoalCheckFailureCodes.EvidenceMissing, report.FailureCode, report.CheckId);
            Assert.IsFalse(string.IsNullOrWhiteSpace(report.Message), report.CheckId);
        }
    }
}

/// <summary>
/// G92-1 S1-c（片 3）：文本供给链——GoalSettlementStore.GetCandidatesAsync 按
/// (conversationId, turnId) 读取 turn.completed 的 payload.reply 并随候选携带；
/// 非法 JSON / 缺 reply / 非 string 一律视为不可得（null），不得抛异常中断结算。
/// </summary>
[TestClass]
public sealed class GoalSettlementFinalReplySupplyTests
{
    private const string WorkspaceId = "ws";
    private const string ConversationId = "conv-1";
    private const string GoalId = "goal-1";
    private const string TurnId = "turn-1";
    private const long TerminalSequence = 7;

    private sealed class NoopCommittedSignal : ICommittedEventSignal
    {
        public ValueTask WaitForChangeAsync(string conversationId, long knownHead, CancellationToken ct)
            => ValueTask.FromCanceled(ct);

        public void Signal(string conversationId, long committedSequence)
        {
        }
    }

    private static async Task<GoalSettlementStore> SeedAsync(
        IDbContextFactory<PlatformDbContext> factory,
        string terminalPayload)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync();

        db.GoalRuns.Add(new GoalRunEntity
        {
            GoalRunId = GoalId,
            WorkspaceId = WorkspaceId,
            CurrentConversationId = ConversationId,
            AgentInstanceId = "agent-1",
            Objective = "只输出 READY",
            ObjectiveVersion = 1,
            Status = GoalPhase.Active,
            ActivationEpoch = 1,
            MaxIterations = 8,
            IterationsStarted = 1,
            SourceCommandId = "cmd-1",
        });
        db.ConversationTurns.Add(new ConversationTurnEntity
        {
            ConversationId = ConversationId,
            TurnId = TurnId,
            WorkspaceId = WorkspaceId,
            Status = "completed",
            AcceptedSequence = TerminalSequence - 1,
            TerminalSequence = TerminalSequence,
            TerminalKind = "completed",
            CreatedAt = 0,
            CompletedAt = 1,
        });
        db.GoalIterations.Add(new GoalIterationEntity
        {
            GoalIterationId = "gi-1",
            GoalRunId = GoalId,
            ActivationEpoch = 1,
            IterationNo = 1,
            Status = "accepted",
            TurnId = TurnId,
            RunId = "run-1",
            AcceptedSequence = TerminalSequence - 1,
            StartedAtUtc = now.AddMinutes(-1),
        });
        db.ConversationEvents.Add(new ConversationEventEntity
        {
            ConversationId = ConversationId,
            Sequence = TerminalSequence,
            EventId = "evt-terminal",
            WorkspaceId = WorkspaceId,
            TurnId = TurnId,
            Type = ConversationEventTypes.TurnCompleted,
            SchemaVersion = 1,
            Payload = terminalPayload,
            OccurredAt = now.ToString("O"),
            CommittedAt = now.ToString("O"),
        });
        await db.SaveChangesAsync();

        return new GoalSettlementStore(factory, new NoopCommittedSignal(), new GoalOutboxSignal());
    }

    [TestMethod]
    public async Task GetCandidatesAsync_CarriesFinalReplyFromTurnCompletedPayload()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = await SeedAsync(factory, "{\"reply\":\"READY\",\"usage\":{}}");

        var candidates = await store.GetCandidatesAsync(8);

        Assert.AreEqual(1, candidates.Count);
        var reply = candidates[0].FinalAssistantReply;
        Assert.IsNotNull(reply);
        Assert.AreEqual(TurnId, reply.TurnId);
        Assert.AreEqual(TerminalSequence, reply.Sequence);
        Assert.AreEqual("READY", reply.Text);
    }

    [TestMethod]
    public async Task GetCandidatesAsync_InvalidOrMissingReply_IsUnavailableNotThrowing()
    {
        var payloads = new[] { "not-json{{", "{\"usage\":{}}", "{\"reply\":123}" };
        foreach (var payload in payloads)
        {
            var (connection, factory) = await GoalWritePathHarness.CreateAsync();
            await using var _ = connection;
            var store = await SeedAsync(factory, payload);

            var candidates = await store.GetCandidatesAsync(8);

            Assert.AreEqual(1, candidates.Count, payload);
            Assert.IsNull(candidates[0].FinalAssistantReply, payload);
        }
    }
}
