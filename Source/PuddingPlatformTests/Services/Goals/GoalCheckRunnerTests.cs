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
        public string ProcessId { get; set; } = "job-1";

        private TerminalProcessInfo Info() => new()
        {
            ProcessId = ProcessId,
            SessionId = "session-1",
            Command = Commands.Count > 0 ? Commands[^1] : string.Empty,
            WorkingDir = ".",
            StartedAt = DateTimeOffset.UtcNow,
            ExitCode = NeverExits ? null : ExitCode,
            Status = NeverExits ? TerminalProcessStatus.Running : TerminalProcessStatus.Exited,
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
        var runner = new GoalCheckRunner(store, stub);

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
        var runner = new GoalCheckRunner(store, stub);

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
        var runner = new GoalCheckRunner(store, stub);

        var reports = await runner.RunAsync([Spec()], Context());

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, reports[0].Status);
        Assert.AreEqual(GoalCheckFailureCodes.NonZeroExitCode, reports[0].FailureCode);
        Assert.AreEqual(1, reports[0].ExitCode);
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
        var runner = new GoalCheckRunner(store, stub);

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
        var runner = new GoalCheckRunner(store, stub);

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
        var runner = new GoalCheckRunner(store, stub);

        var reports = await runner.RunAsync([Spec()], Context(timeoutSeconds: 1));

        Assert.AreEqual(GoalCriterionResultStatuses.Waiting, reports[0].Status);
        Assert.AreEqual(GoalCheckFailureCodes.CheckTimeout, reports[0].FailureCode);
        CollectionAssert.Contains(stub.Killed, "job-1");
    }
}
