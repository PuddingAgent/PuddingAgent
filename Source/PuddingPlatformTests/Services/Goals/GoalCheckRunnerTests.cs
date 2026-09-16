using PuddingCode.Abstractions;
using PuddingCode.Goals;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// ADR-092 §5.3（G92-1）：检查定义只能来自版本化注册表，命令不得由模型自由拼接。
/// </summary>
[TestClass]
public sealed class GoalCheckDefinitionRegistryTests
{
    private static GoalCheckSpec Spec(
        string definitionRef = "checks/test.md#dotnet-test",
        string kind = GoalVerificationSpecKinds.Test,
        params string[] inputRefs) => new()
    {
        CheckId = "check-1",
        CriterionId = "criterion-1",
        CriterionRevision = 1,
        Kind = kind,
        DefinitionRef = definitionRef,
        DefinitionHash = "hash-a",
        InputRefs = inputRefs.Length == 0 ? ["Source/PuddingCoreTests/PuddingCoreTests.csproj"] : inputRefs,
        InputFingerprint = "fp-1",
        ExecutorRole = "core",
    };

    [TestMethod]
    public void RegisteredDefinition_BuildsCanonicalCommand()
    {
        Assert.IsTrue(GoalCheckDefinitionRegistry.TryBuildCommand(Spec(), out var command, out var failure));

        Assert.AreEqual(string.Empty, failure);
        Assert.AreEqual(
            "dotnet test Source/PuddingCoreTests/PuddingCoreTests.csproj --no-restore --nologo",
            command);
    }

    [TestMethod]
    public void UnregisteredDefinition_IsRejected()
    {
        Assert.IsFalse(GoalCheckDefinitionRegistry.TryBuildCommand(
            Spec("checks/custom.md#whatever"),
            out var command,
            out var failure));

        Assert.AreEqual(string.Empty, command);
        Assert.AreEqual(GoalCheckFailureCodes.UnsupportedCheckKind, failure);
    }

    [TestMethod]
    public void KindMismatch_IsRejected()
    {
        Assert.IsFalse(GoalCheckDefinitionRegistry.TryBuildCommand(
            Spec(kind: GoalVerificationSpecKinds.Build),
            out _,
            out var failure));

        Assert.AreEqual(GoalCheckFailureCodes.UnsupportedCheckKind, failure);
    }

    [TestMethod]
    public void ShellMetacharactersOrTraversalInInputRefs_AreRejected()
    {
        foreach (var target in new[]
        {
            "../escape.csproj",
            "C:/abs/abs.csproj",
            "Source/a.csproj; rm -rf /",
            "Source/a.csproj && echo pwned",
            "Source/a.csproj | tee x",
            "Source/not-a-project.txt",
        })
        {
            Assert.IsFalse(
                GoalCheckDefinitionRegistry.TryBuildCommand(Spec(inputRefs: [target]), out var command, out var failure),
                $"target should be rejected: {target}");
            Assert.AreEqual(string.Empty, command);
            Assert.AreEqual(GoalCheckFailureCodes.UnsupportedCheckKind, failure);
        }
    }

    [TestMethod]
    public void MultipleInputRefs_AreRejected()
    {
        Assert.IsFalse(GoalCheckDefinitionRegistry.TryBuildCommand(
            Spec(inputRefs: ["Source/A/A.csproj", "Source/B/B.csproj"]),
            out _,
            out var failure));

        Assert.AreEqual(GoalCheckFailureCodes.InputFingerprintMissing, failure);
    }
}

/// <summary>ADR-092 §6：测试数量只能从真实输出解析，解析不到就必须判为无证据。</summary>
[TestClass]
public sealed class GoalCheckOutputParserTests
{
    [TestMethod]
    public void ParseTestSummary_ReadsVstestSummary()
    {
        var summary = GoalCheckOutputParser.ParseTestSummary(
            ["Starting test execution, please wait...",
             "Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 1 s"]);

        Assert.IsNotNull(summary);
        Assert.AreEqual(5, summary.ExecutedTestCount);
        Assert.AreEqual(5, summary.PassedTestCount);
        Assert.AreEqual(0, summary.FailedTestCount);
    }

    [TestMethod]
    public void ParseTestSummary_WithoutSummary_ReturnsNull()
    {
        Assert.IsNull(GoalCheckOutputParser.ParseTestSummary(["Build succeeded.", "no summary here"]));
        Assert.IsNull(GoalCheckOutputParser.ParseTestSummary(null));
    }

    [TestMethod]
    public void ParseTestSummary_UsesTheLastSummaryLine()
    {
        var summary = GoalCheckOutputParser.ParseTestSummary(
            ["Failed: 9, Passed: 1, Skipped: 0, Total: 10",
             "Failed: 0, Passed: 10, Skipped: 0, Total: 10"]);

        Assert.IsNotNull(summary);
        Assert.AreEqual(0, summary.FailedTestCount);
        Assert.AreEqual(10, summary.PassedTestCount);
    }

    [TestMethod]
    public void UnfinishedProcessMarker_IsDetected()
    {
        Assert.IsTrue(GoalCheckOutputParser.HasUnfinishedProcessMarker(
            ["The test host process crashed unexpectedly."]));
        Assert.IsFalse(GoalCheckOutputParser.HasUnfinishedProcessMarker(["Passed!  - Failed: 0"]));
    }
}

/// <summary>
/// ADR-092 §5.3/§6.2（G92-1）：runner 的真实行为——命令来自注册表、报告来自真实进程结果、
/// 未登记定义不执行任何进程、超时产生等待而非通过。
/// </summary>
[TestClass]
public sealed class GoalCheckRunnerTests
{
    private sealed class StubProcessManager : ITerminalProcessManager
    {
        public List<string> Commands { get; } = [];
        public List<string> Killed { get; } = [];
        public IReadOnlyList<string> Output { get; set; } = [];
        public int? ExitCode { get; set; }
        public bool NeverExits { get; set; }
        public TerminalProcessStatus? StatusOverride { get; set; }
        public string ProcessId { get; set; } = "job-1";

        private TerminalProcessInfo Info() => new()
        {
            ProcessId = ProcessId,
            SessionId = "session-1",
            Command = Commands.Count > 0 ? Commands[^1] : string.Empty,
            WorkingDir = ".",
            StartedAt = DateTimeOffset.UtcNow,
            ExitCode = NeverExits ? null : ExitCode,
            // 与生产 TerminalProcessManager 一致：按退出码派生终态（0 ⇒ Exited，非 0/未知 ⇒ Failed）。
            // 此前写死 Exited 会绕过生产的 Failed 状态机，让 CI 对「失败被误分类为等待」失明。
            Status = NeverExits
                ? TerminalProcessStatus.Running
                : StatusOverride ?? (ExitCode == 0 ? TerminalProcessStatus.Exited : TerminalProcessStatus.Failed),
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
            => Task.FromResult<TerminalOutputSnapshot?>(new TerminalOutputSnapshot
            {
                Process = Info(),
                Offset = 0,
                NextOffset = Output.Count,
                TotalLines = Output.Count,
                Lines = Output,
            });

        public Task<bool> WriteInputAsync(string processId, string input, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<bool> KillAsync(string processId)
        {
            Killed.Add(processId);
            NeverExits = false;
            return Task.FromResult(true);
        }

        public IReadOnlyList<TerminalProcessInfo> ListProcesses(string? sessionId = null) => [Info()];

        public Task<int> ReapAsync() => Task.FromResult(0);
    }

    private static GoalCheckSpec Spec(
        int? expectedTests = 5,
        string definitionRef = "checks/test.md#dotnet-test") => new()
    {
        CheckId = "check-1",
        CriterionId = "criterion-1",
        CriterionRevision = 1,
        Kind = GoalVerificationSpecKinds.Test,
        DefinitionRef = definitionRef,
        DefinitionHash = "hash-a",
        InputRefs = ["Source/PuddingCoreTests/PuddingCoreTests.csproj"],
        InputFingerprint = "fp-1",
        ExecutorRole = "core",
        ExpectedEvidence = "真实执行的测试数量",
        ExpectedTestCount = expectedTests,
    };

    private static GoalCheckContext Context(int? timeoutSeconds = 30) => new()
    {
        GoalRunId = "goal-1",
        ActivationEpoch = 1,
        WorkspaceId = "ws",
        AgentInstanceId = "agent-1",
        IterationNo = 1,
        Scope = GoalVerificationScopes.WorkUnit,
        SessionId = "session-1",
        WorkingDirectory = "E:\\github\\AgentNetworkPlan\\PuddingAgent",
        TimeoutSeconds = timeoutSeconds,
    };

    private static readonly string[] GreenSummary =
        ["Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 1 s"];

    [TestMethod]
    public async Task Run_GreenTestRun_ProducesPassedReportWithRealCounts()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var stub = new StubProcessManager { ExitCode = 0, Output = GreenSummary };
        var runner = new GoalCheckRunner(store, stub, new AllowAllAdmission());

        var reports = await runner.RunAsync([Spec()], Context());

        Assert.AreEqual(1, reports.Count);
        Assert.AreEqual(GoalCriterionResultStatuses.Passed, reports[0].Status);
        Assert.AreEqual(5, reports[0].ExecutedTestCount);
        Assert.AreEqual(0, reports[0].FailedTestCount);
        Assert.AreEqual(GoalCheckRunner.RunnerId, reports[0].RunnerId);
        Assert.AreEqual("job-1", reports[0].InvocationId);
        Assert.AreEqual("terminal-job:job-1", reports[0].ReportRef);
        Assert.AreEqual(
            "dotnet test Source/PuddingCoreTests/PuddingCoreTests.csproj --no-restore --nologo",
            stub.Commands[0]);

        // 报告必须已持久化，capsule 读取链能读到同一份证据。
        var persisted = GoalVerificationPersistence.ReadReports(await store.ReadForEpochAsync("goal-1", 1));
        Assert.AreEqual(1, persisted.Count);
        Assert.AreEqual(5, persisted[0].ExecutedTestCount);
    }

    [TestMethod]
    public async Task Run_UnparsableTestOutput_IsFailedNotPassed()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var stub = new StubProcessManager { ExitCode = 0, Output = ["Build succeeded.", "no summary"] };
        var runner = new GoalCheckRunner(store, stub, new AllowAllAdmission());

        var reports = await runner.RunAsync([Spec()], Context());

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, reports[0].Status);
        Assert.AreEqual(GoalCheckFailureCodes.TestCountUnknown, reports[0].FailureCode);
        Assert.IsNull(reports[0].ExecutedTestCount);
    }

    [TestMethod]
    public async Task Run_NonZeroExitCode_IsFailed()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var stub = new StubProcessManager { ExitCode = 1, Output = ["Failed: 2, Passed: 3, Skipped: 0, Total: 5"] };
        var runner = new GoalCheckRunner(store, stub, new AllowAllAdmission());

        var reports = await runner.RunAsync([Spec()], Context());

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, reports[0].Status);
        Assert.AreEqual(GoalCheckFailureCodes.NonZeroExitCode, reports[0].FailureCode);
        Assert.AreEqual(1, reports[0].ExitCode);

        // R1 回归锁：非零退出（生产侧 Status=Failed）⇒ 必须落 finished(failed) 终态记录，
        // 不得被误分类成等待而退回 pending。
        var records = await store.ReadForEpochAsync("goal-1", 1);
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(GoalCheckRecordStatuses.Finished, records[0].Status);
        Assert.AreEqual(GoalCheckFailureCodes.NonZeroExitCode, records[0].FailureCode);
        var persisted = GoalVerificationPersistence.ReadReports(records);
        Assert.AreEqual(1, persisted.Count);
        Assert.AreEqual(GoalCriterionResultStatuses.Failed, persisted[0].Status);
    }

    [TestMethod]
    public async Task Run_FewerTestsThanDeclared_IsFailed()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var stub = new StubProcessManager
        {
            ExitCode = 0,
            Output = ["Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3"],
        };
        var runner = new GoalCheckRunner(store, stub, new AllowAllAdmission());

        var reports = await runner.RunAsync([Spec(expectedTests: 5)], Context());

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, reports[0].Status);
        Assert.AreEqual(GoalCheckFailureCodes.TestCountUnknown, reports[0].FailureCode);
        Assert.AreEqual(3, reports[0].ExecutedTestCount);
    }

    [TestMethod]
    public async Task Run_UnregisteredDefinition_ExecutesNoProcess()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var stub = new StubProcessManager { ExitCode = 0, Output = GreenSummary };
        var runner = new GoalCheckRunner(store, stub, new AllowAllAdmission());

        var reports = await runner.RunAsync([Spec(definitionRef: "checks/pretend.md#rm-rf")], Context());

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, reports[0].Status);
        Assert.AreEqual(GoalCheckFailureCodes.UnsupportedCheckKind, reports[0].FailureCode);
        Assert.AreEqual(0, stub.Commands.Count);
    }

    [TestMethod]
    public async Task Run_CheckThatNeverExits_WaitsAndKillsTheProcess()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var stub = new StubProcessManager { NeverExits = true, Output = GreenSummary };
        var runner = new GoalCheckRunner(store, stub, new AllowAllAdmission());

        var reports = await runner.RunAsync([Spec()], Context(timeoutSeconds: 1));

        Assert.AreEqual(GoalCriterionResultStatuses.Waiting, reports[0].Status);
        Assert.AreEqual(GoalCheckFailureCodes.CheckTimeout, reports[0].FailureCode);
        CollectionAssert.Contains(stub.Killed, "job-1");

        // R6 回归锁（存储侧区分性）：真超时 ⇒ 记录退回 pending、不落报告 —— 与 failed 终态可区分。
        var records = await store.ReadForEpochAsync("goal-1", 1);
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(GoalCheckRecordStatuses.Pending, records[0].Status);
        Assert.IsNull(records[0].ReportJson);
    }

    [TestMethod]
    public async Task Run_TrulyKilledProcess_StillWaitsInsteadOfJudging()
    {
        // R2 回归锁：真被杀（Status=Killed，即使携带退出码）= 证据不完整 ⇒ 仍走等待语义，
        // 不得被「Failed 终态化」改动带跑成 failed/passed。
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var stub = new StubProcessManager
        {
            StatusOverride = TerminalProcessStatus.Killed,
            ExitCode = 1,
            Output = GreenSummary,
        };
        var runner = new GoalCheckRunner(store, stub, new AllowAllAdmission());

        var reports = await runner.RunAsync([Spec()], Context());

        Assert.AreEqual(GoalCriterionResultStatuses.Waiting, reports[0].Status);
        Assert.AreEqual(GoalCheckFailureCodes.CheckTimeout, reports[0].FailureCode);
        Assert.IsTrue(reports[0].HasUnfinishedBackgroundProcess);

        var records = await store.ReadForEpochAsync("goal-1", 1);
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(GoalCheckRecordStatuses.Pending, records[0].Status);
        Assert.IsNull(records[0].ReportJson);
    }

    [TestMethod]
    public async Task Run_MissingExitCode_FailsClosedWithExplicitReason()
    {
        // R4 回归锁：进程已终态但退出码不可得 ⇒ fail-closed 记 failed（绝不记 passed，也不当等待）。
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var stub = new StubProcessManager
        {
            StatusOverride = TerminalProcessStatus.Exited,
            ExitCode = null,
            Output = GreenSummary,
        };
        var runner = new GoalCheckRunner(store, stub, new AllowAllAdmission());

        var reports = await runner.RunAsync([Spec()], Context());

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, reports[0].Status);
        Assert.AreEqual(GoalCheckFailureCodes.ExitCodeUnknown, reports[0].FailureCode);
        Assert.IsNull(reports[0].ExitCode);

        var records = await store.ReadForEpochAsync("goal-1", 1);
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(GoalCheckRecordStatuses.Finished, records[0].Status);
        Assert.AreEqual(GoalCheckFailureCodes.ExitCodeUnknown, records[0].FailureCode);
    }

    [TestMethod]
    public async Task Run_WhenAdmissionDenies_ExecutesNoProcessAndRecordsAdmissionDenied()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var stub = new StubProcessManager { ExitCode = 0, Output = GreenSummary };
        var runner = new GoalCheckRunner(store, stub, new DenyAllAdmission());

        var reports = await runner.RunAsync([Spec()], Context());

        // ADR-092 §13.4：受控检查必须与 terminal 工具共用同一准入面。
        // 准入拒绝时 fail-closed：不启动任何进程、不记通过，如实记 failed + 准入拒绝码。
        Assert.AreEqual(GoalCriterionResultStatuses.Failed, reports[0].Status);
        Assert.AreEqual(GoalCheckFailureCodes.AdmissionDenied, reports[0].FailureCode);
        Assert.AreEqual(0, stub.Commands.Count);
    }

    /// <summary>准入桩：本文件验证执行与解析语义，不验证准入策略（准入拒绝用例自带拒绝桩）。</summary>
    private sealed class AllowAllAdmission : ITerminalCommandAdmission
    {
        public void EnsureAllowed(string command, bool isYoloMode)
        {
        }
    }

    /// <summary>拒绝一切命令的准入桩：验证准入拒绝时 fail-closed（不启动进程、不记通过）。</summary>
    private sealed class DenyAllAdmission : ITerminalCommandAdmission
    {
        public void EnsureAllowed(string command, bool isYoloMode)
            => throw new UnauthorizedAccessException("denied by test admission");
    }

    [TestMethod]
    public async Task FileEvidence_ExistingNonEmptyFile_PassesWithoutStartingAnyProcess()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var stub = new StubProcessManager { ExitCode = 0, Output = GreenSummary };
        var runner = new GoalCheckRunner(store, stub, new AllowAllAdmission());

        var root = Directory.CreateTempSubdirectory("goal-file-evidence").FullName;
        try
        {
            var evidencePath = Path.Combine(root, "evidence");
            Directory.CreateDirectory(evidencePath);
            await File.WriteAllTextAsync(Path.Combine(evidencePath, "report.md"), "# done\n");

            var reports = await runner.RunAsync(
                [FileSpec("evidence/report.md")],
                Context() with { WorkingDirectory = root });

            // 目标级验收（T3）：目标级 criterion 能被真实评估 —— passed 两态之一，且零进程启动。
            Assert.AreEqual(1, reports.Count);
            Assert.AreEqual(GoalCriterionResultStatuses.Passed, reports[0].Status);
            Assert.IsNull(reports[0].FailureCode);
            Assert.AreEqual(GoalCheckRunner.RunnerId, reports[0].RunnerId);
            Assert.AreEqual("file:evidence/report.md", reports[0].EvidenceRefs[0]);
            Assert.AreEqual(0, stub.Commands.Count, "file-evidence must never start a process");

            // 报告已持久化，下游 capsule 读取链能读到同一份证据。
            var persisted = GoalVerificationPersistence.ReadReports(await store.ReadForEpochAsync("goal-1", 1));
            Assert.AreEqual(1, persisted.Count);
            Assert.AreEqual(GoalCriterionResultStatuses.Passed, persisted[0].Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FileEvidence_MissingFile_FailsInsteadOfPending()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var stub = new StubProcessManager { ExitCode = 0, Output = GreenSummary };
        var runner = new GoalCheckRunner(store, stub, new AllowAllAdmission());

        var root = Directory.CreateTempSubdirectory("goal-file-evidence").FullName;
        try
        {
            var reports = await runner.RunAsync(
                [FileSpec("evidence/absent.md")],
                Context() with { WorkingDirectory = root });

            Assert.AreEqual(GoalCriterionResultStatuses.Failed, reports[0].Status);
            Assert.AreNotEqual(GoalCriterionResultStatuses.Pending, reports[0].Status);
            Assert.AreEqual(GoalCheckFailureCodes.FileEvidenceMissing, reports[0].FailureCode);
            Assert.AreEqual(0, stub.Commands.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FileEvidence_EmptyFile_Fails()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var stub = new StubProcessManager { ExitCode = 0, Output = GreenSummary };
        var runner = new GoalCheckRunner(store, stub, new AllowAllAdmission());

        var root = Directory.CreateTempSubdirectory("goal-file-evidence").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "evidence"));
            await File.WriteAllTextAsync(Path.Combine(root, "evidence", "empty.md"), string.Empty);

            var reports = await runner.RunAsync(
                [FileSpec("evidence/empty.md")],
                Context() with { WorkingDirectory = root });

            Assert.AreEqual(GoalCriterionResultStatuses.Failed, reports[0].Status);
            Assert.AreEqual(GoalCheckFailureCodes.FileEvidenceMissing, reports[0].FailureCode);
            Assert.AreEqual(0, stub.Commands.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FileEvidence_UnsafePath_FailsWithReadableReason()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);
        var stub = new StubProcessManager { ExitCode = 0, Output = GreenSummary };
        var runner = new GoalCheckRunner(store, stub, new AllowAllAdmission());

        string[] unsafePaths = ["../escape.md", "C:/abs/abs.md", "wild*card.md", "question?.md"];
        foreach (var unsafePath in unsafePaths)
        {
            var reports = await runner.RunAsync(
                [FileSpec(unsafePath)],
                Context() with { WorkingDirectory = Path.GetTempPath() });

            Assert.AreEqual(GoalCriterionResultStatuses.Failed, reports[0].Status, unsafePath);
            Assert.AreEqual(GoalCheckFailureCodes.UnsupportedCheckKind, reports[0].FailureCode, unsafePath);
            Assert.IsFalse(string.IsNullOrWhiteSpace(reports[0].Message), unsafePath);
            Assert.AreEqual(0, stub.Commands.Count, unsafePath);
        }
    }

    /// <summary>file-evidence 检查规格：定义与 hash 都来自注册表真实值（非自造）。</summary>
    private static GoalCheckSpec FileSpec(string relativePath) => new()
    {
        CheckId = $"objective:file-evidence:{relativePath}",
        CriterionId = $"objective-file-evidence:{relativePath}",
        CriterionRevision = 1,
        Kind = GoalVerificationSpecKinds.FileEvidence,
        DefinitionRef = GoalCheckDefinitionRegistry.FileEvidenceRef,
        DefinitionHash = GoalCheckDefinitionRegistry.TryGetDefinitionHash(
            GoalCheckDefinitionRegistry.FileEvidenceRef, out var hash) ? hash : string.Empty,
        InputRefs = [relativePath],
        InputFingerprint = "fp-1",
        ExecutorRole = "core",
        ExpectedEvidence = "只读核验：文件存在且非空",
        ExpectedTestCount = null,
    };
}
