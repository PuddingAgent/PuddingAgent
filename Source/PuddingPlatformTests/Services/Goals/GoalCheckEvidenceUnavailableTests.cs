using PuddingCode.Abstractions;
using PuddingCode.Goals;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// 证据不可用（EvidenceUnavailable）回归锁：受控检查在 test 检查「无工作单元证据」——
/// 无可解析汇总、零执行——时不得落成 finished。否则去重键在本 epoch 内永久固化无效证据，
/// 检查永远无法重跑（真实事故：GoalRun 7ef90f2c 因外部并发冲突 exit 1 且无测试汇总，
/// iter5–iter8 连续四轮复用同一失败结果空转，只能 resume 递增 epoch 才能重跑）。
/// 真实测试失败与构建失败是有效判定，仍必须缓存为 finished，不得无限重跑。
/// <para>
/// 边界（刻意不覆盖）：exit_code_unknown（进程终态但退出码不可得）是「平台没能给出结论」的宿主异常，
/// 平台有意让其成为终态以便 triage —— 由 GoalCheckRunnerTests
/// .Run_MissingExitCode_FailsClosedWithExplicitReason 的 R4 锁保证
/// （failed + exit_code_unknown + 记录 finished + 不当等待）。本文件不改变该契约。
/// </para>
/// </summary>
[TestClass]
public sealed class GoalCheckEvidenceUnavailableTests
{
    private static readonly string[] GreenSummary =
        ["Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 1 s"];

    [TestMethod]
    public async Task TestCheck_NonZeroExitWithoutSummary_ReturnsRecordToPendingForRetry()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var stub = new StubProcessManager
        {
            ExitCode = 1,
            Output = ["error MSB3021: Unable to copy file, build failed."],
        };
        var runner = new GoalCheckRunner(store, stub, new AllowAllAdmission());

        var reports = await runner.RunAsync([Spec()], Context());

        // 报告语义不变：仍是 failed + non_zero_exit_code，只是补上"证据不可用"标记。
        Assert.AreEqual(GoalCriterionResultStatuses.Failed, reports[0].Status);
        Assert.AreEqual(GoalCheckFailureCodes.NonZeroExitCode, reports[0].FailureCode);
        Assert.IsTrue(reports[0].EvidenceUnavailable);

        // 不落 finished：回 pending 可重跑、无缓存报告、保留失败码供 triage。
        var records = await store.ReadForEpochAsync("goal-1", 1);
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(GoalCheckRecordStatuses.Pending, records[0].Status);
        Assert.IsNull(records[0].ReportJson);
        Assert.AreEqual(GoalCheckFailureCodes.NonZeroExitCode, records[0].FailureCode);
        Assert.AreEqual(0, GoalVerificationPersistence.ReadReports(records).Count);

        // 核心验收：去重键不再固化无效证据 —— 记录可被再次认领重跑。
        var reclaimed = await store.LeaseAsync(
            "goal-check-runner:retry", "goal-1", 1, TimeSpan.FromMinutes(1), 4);
        Assert.AreEqual(1, reclaimed.Count);
        Assert.AreEqual("check-1", reclaimed[0].CheckId);
    }

    [TestMethod]
    public async Task TestCheck_GreenRun_StillFinishes()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var stub = new StubProcessManager { ExitCode = 0, Output = GreenSummary };
        var runner = new GoalCheckRunner(store, stub, new AllowAllAdmission());

        var reports = await runner.RunAsync([Spec()], Context());

        // 正常路径不受影响：passed 报告照常落 finished。
        Assert.AreEqual(GoalCriterionResultStatuses.Passed, reports[0].Status);
        Assert.IsFalse(reports[0].EvidenceUnavailable);

        var records = await store.ReadForEpochAsync("goal-1", 1);
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(GoalCheckRecordStatuses.Finished, records[0].Status);
        var persisted = GoalVerificationPersistence.ReadReports(records);
        Assert.AreEqual(1, persisted.Count);
        Assert.IsFalse(persisted[0].EvidenceUnavailable);
    }

    [TestMethod]
    public async Task TestCheck_NonZeroExitWithRealFailures_StillFinishes()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var stub = new StubProcessManager
        {
            ExitCode = 1,
            Output = ["Failed!  - Failed:     2, Passed:     3, Skipped:     0, Total:     5, Duration: 1 s"],
        };
        var runner = new GoalCheckRunner(store, stub, new AllowAllAdmission());

        var reports = await runner.RunAsync([Spec()], Context());

        // 有真实测试证据的失败是有效判定：不得标记证据不可用，必须缓存为 finished（禁止无限重跑）。
        Assert.AreEqual(GoalCriterionResultStatuses.Failed, reports[0].Status);
        Assert.AreEqual(GoalCheckFailureCodes.NonZeroExitCode, reports[0].FailureCode);
        Assert.IsFalse(reports[0].EvidenceUnavailable, "有真实测试证据的失败是有效判定，不得回 pending。");

        var records = await store.ReadForEpochAsync("goal-1", 1);
        Assert.AreEqual(GoalCheckRecordStatuses.Finished, records[0].Status);
        var persisted = GoalVerificationPersistence.ReadReports(records);
        Assert.AreEqual(1, persisted.Count);
        Assert.AreEqual(5, persisted[0].ExecutedTestCount);
        Assert.AreEqual(2, persisted[0].FailedTestCount);
    }

    [TestMethod]
    public async Task BuildCheck_NonZeroExit_StillFinishes()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var stub = new StubProcessManager
        {
            ExitCode = 1,
            Output = ["error CS1002: ; expected"],
        };
        var runner = new GoalCheckRunner(store, stub, new AllowAllAdmission());

        var reports = await runner.RunAsync([BuildSpec()], Context());

        // 构建失败是真实判定（非"无证据"）：必须落 finished，不得回 pending。
        Assert.AreEqual(GoalCriterionResultStatuses.Failed, reports[0].Status);
        Assert.AreEqual(GoalCheckFailureCodes.NonZeroExitCode, reports[0].FailureCode);
        Assert.IsFalse(reports[0].EvidenceUnavailable);

        var records = await store.ReadForEpochAsync("goal-1", 1);
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(GoalCheckRecordStatuses.Finished, records[0].Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(records[0].ReportJson));
    }

    private static GoalCheckSpec Spec() => new()
    {
        CheckId = "check-1",
        CriterionId = "criterion-1",
        CriterionRevision = 1,
        Kind = GoalVerificationSpecKinds.Test,
        DefinitionRef = "checks/test.md#dotnet-test",
        DefinitionHash = "hash-a",
        InputRefs = ["Source/PuddingCoreTests/PuddingCoreTests.csproj"],
        InputFingerprint = "fp-1",
        ExecutorRole = "core",
        ExpectedEvidence = "真实执行的测试数量",
        ExpectedTestCount = 5,
    };

    private static GoalCheckSpec BuildSpec() => new()
    {
        CheckId = "check-build",
        CriterionId = "criterion-build",
        CriterionRevision = 1,
        Kind = GoalVerificationSpecKinds.Build,
        DefinitionRef = "checks/build.md#dotnet-build",
        DefinitionHash = "hash-build",
        InputRefs = ["Source/PuddingCore/PuddingCore.csproj"],
        InputFingerprint = "fp-build",
        ExecutorRole = "core",
        ExpectedEvidence = "构建退出码为 0",
    };

    private static GoalCheckContext Context() => new()
    {
        GoalRunId = "goal-1",
        ActivationEpoch = 1,
        WorkspaceId = "ws",
        AgentInstanceId = "agent-1",
        IterationNo = 1,
        Scope = GoalVerificationScopes.WorkUnit,
        SessionId = "session-1",
        WorkingDirectory = ".",
        TimeoutSeconds = 30,
    };

    private sealed class StubProcessManager : ITerminalProcessManager
    {
        public List<string> Commands { get; } = [];
        public IReadOnlyList<string> Output { get; set; } = [];
        public int? ExitCode { get; set; }
        public string ProcessId { get; set; } = "job-1";

        private TerminalProcessInfo Info() => new()
        {
            ProcessId = ProcessId,
            SessionId = "session-1",
            Command = Commands.Count > 0 ? Commands[^1] : string.Empty,
            WorkingDir = ".",
            StartedAt = DateTimeOffset.UtcNow,
            ExitCode = ExitCode,
            Status = ExitCode == 0 ? TerminalProcessStatus.Exited : TerminalProcessStatus.Failed,
        };

        public Task<TerminalProcessInfo> StartAsync(
            string sessionId,
            string command,
            string workingDir,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(Info());
        }

        public IAsyncEnumerable<string> SubscribeAsync(string processId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<TerminalOutputSnapshot?> ReadOutputAsync(
            string processId,
            int offset = 0,
            int? maxLines = null,
            int? maxChars = null,
            CancellationToken ct = default)
        {
            var start = Math.Clamp(offset, 0, Output.Count);
            var take = Math.Min(maxLines ?? Output.Count, Output.Count - start);
            var lines = Output.Skip(start).Take(take).ToList();
            return Task.FromResult<TerminalOutputSnapshot?>(new TerminalOutputSnapshot
            {
                Process = Info(),
                Offset = start,
                NextOffset = start + lines.Count,
                TotalLines = Output.Count,
                Truncated = start + lines.Count < Output.Count,
                Lines = lines,
            });
        }

        public Task<bool> WriteInputAsync(string processId, string input, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<bool> KillAsync(string processId) => Task.FromResult(true);

        public IReadOnlyList<TerminalProcessInfo> ListProcesses(string? sessionId = null) => [Info()];

        public Task<int> ReapAsync() => Task.FromResult(0);
    }

    /// <summary>准入桩：本文件验证证据语义，不验证准入策略。</summary>
    private sealed class AllowAllAdmission : ITerminalCommandAdmission
    {
        public void EnsureAllowed(string command, bool isYoloMode)
        {
        }
    }
}
