using PuddingCode.Tools;
using PuddingCodeIndex.Contracts;
using PuddingCodeIntelligence.Contracts;
using PuddingRuntime.Services.Tools;
using System.Text.Json;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// T2a 的接入面：<c>code_callers</c> 与 <c>code_impact</c> 的空结果同样必须自证「索引是否可信」——
/// 它们的空结果会直接驱动「可以删代码」「没有下游影响」这类破坏性 / 高风险判断，
/// 误读「重建中的空结果」比误读搜索空结果更危险。
/// <para>
/// 断言四类行为 × 两个工具：① 非 idle + 空结果 ⇒ <c>index_freshness.state</c> 如实 + 文本附诚实说明（含 ℹ）；
/// ② idle + 空结果 ⇒ state 为 idle 但文本保持寂静；③ 取不到事实（maintenance 缺失）⇒ state 为 unknown
/// 且 reason 非空、仍附说明；④ 有命中 ⇒ 不追加说明（不喊狼来了）。
/// 另加 1 条「共享 helper 行为锁定」用例，逐字冻结 <c>BuildEmptyResultFreshnessNote</c> 的 3 个分支。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeCallersImpactFreshnessTests
{
    private const string WorkspaceId = "workspace-1";
    private const string ProjectId = "project-live";

    /// <summary>诚实说明的标记（ℹ）。用 \u 转义写死，避免源码文件编码漂移影响断言。</summary>
    private const string InfoMarker = "\u2139";

    private const string CallersArgs = """{"symbol_id":"sym-1","project_id":"project-live"}""";
    private const string ImpactArgs = """{"symbol_id":"sym-1","project_id":"project-live","max_depth":3}""";

    // ───────────────────────────── code_callers ─────────────────────────────

    [TestMethod]
    public async Task Callers_Zero_Hits_While_Index_In_Flight_Reports_Rebuilding_And_Says_May_Be_Temporary()
    {
        var tool = new CodeCallersTool(
            new QueryServiceStub(callers: [], impacted: []),
            maintenance: new MaintenanceStub(Status(ProjectId, indexInFlight: true)));

        var result = await ExecuteCallersAsync(tool, CallersArgs);

        using var json = JsonDocument.Parse(StripNotes(result.Output!));
        var freshness = json.RootElement.GetProperty("index_freshness");
        Assert.AreEqual("rebuilding", freshness.GetProperty("state").GetString());
        Assert.IsTrue(freshness.GetProperty("index_in_flight").GetBoolean());
        Assert.AreEqual(0, json.RootElement.GetProperty("count").GetInt32());
        StringAssert.Contains(result.Output!, InfoMarker);
        StringAssert.Contains(result.Output!, "may be temporary");
    }

    [TestMethod]
    public async Task Callers_Zero_Hits_With_Attached_Watcher_Reports_Idle_And_Stays_Silent()
    {
        var tool = new CodeCallersTool(
            new QueryServiceStub(callers: [], impacted: []),
            maintenance: new MaintenanceStub(Status(ProjectId, watcherAttached: true)));

        var result = await ExecuteCallersAsync(tool, CallersArgs);

        using var json = JsonDocument.Parse(StripNotes(result.Output!));
        var freshness = json.RootElement.GetProperty("index_freshness");
        Assert.AreEqual("idle", freshness.GetProperty("state").GetString());
        Assert.IsTrue(freshness.GetProperty("watcher_attached").GetBoolean());
        Assert.IsFalse(
            result.Output!.Contains(InfoMarker, StringComparison.Ordinal),
            "callers：索引已 idle 的空结果不得附说明（保持寂静）");
    }

    [TestMethod]
    public async Task Callers_Zero_Hits_Without_Maintenance_Reports_Unknown_And_Honest_Reason()
    {
        var tool = new CodeCallersTool(new QueryServiceStub(callers: [], impacted: []));

        var result = await ExecuteCallersAsync(tool, CallersArgs);

        using var json = JsonDocument.Parse(StripNotes(result.Output!));
        var freshness = json.RootElement.GetProperty("index_freshness");
        Assert.AreEqual("unknown", freshness.GetProperty("state").GetString());
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(freshness.GetProperty("reason").GetString()),
            "callers：unknown 必须给出原因，不得留空");
        StringAssert.Contains(result.Output!, InfoMarker);
        StringAssert.Contains(result.Output!, "not proof that the symbol does not exist");
    }

    [TestMethod]
    public async Task Callers_With_Hits_While_Index_In_Flight_Reports_Rebuilding_But_Stays_Silent()
    {
        var tool = new CodeCallersTool(
            new QueryServiceStub(
                callers:
                [
                    new CodeRelationRecord(
                        WorkspaceId, ProjectId, "caller-1", "sym-1", CodeRelationKind.Calls,
                        SourceLine: 7, SourceFilePath: "C:\\repo\\A.cs"),
                ],
                impacted: []),
            maintenance: new MaintenanceStub(Status(ProjectId, indexInFlight: true)));

        var result = await ExecuteCallersAsync(tool, CallersArgs);

        using var json = JsonDocument.Parse(StripNotes(result.Output!));
        Assert.AreEqual(1, json.RootElement.GetProperty("count").GetInt32());
        Assert.AreEqual(1, json.RootElement.GetProperty("callers").GetArrayLength());
        Assert.AreEqual(
            "rebuilding",
            json.RootElement.GetProperty("index_freshness").GetProperty("state").GetString());
        Assert.IsFalse(
            result.Output!.Contains(InfoMarker, StringComparison.Ordinal),
            "callers：有命中时不得追加空结果说明");
    }

    // ───────────────────────────── code_impact ─────────────────────────────

    [TestMethod]
    public async Task Impact_Zero_Hits_While_Index_In_Flight_Reports_Rebuilding_And_Says_May_Be_Temporary()
    {
        var tool = new CodeImpactTool(
            new QueryServiceStub(callers: [], impacted: []),
            maintenance: new MaintenanceStub(Status(ProjectId, indexInFlight: true)));

        var result = await ExecuteImpactAsync(tool, ImpactArgs);

        using var json = JsonDocument.Parse(StripNotes(result.Output!));
        var freshness = json.RootElement.GetProperty("index_freshness");
        Assert.AreEqual("rebuilding", freshness.GetProperty("state").GetString());
        Assert.AreEqual(3, json.RootElement.GetProperty("max_depth").GetInt32());
        Assert.AreEqual(0, json.RootElement.GetProperty("count").GetInt32());
        StringAssert.Contains(result.Output!, InfoMarker);
        StringAssert.Contains(result.Output!, "may be temporary");
    }

    [TestMethod]
    public async Task Impact_Zero_Hits_With_Attached_Watcher_Reports_Idle_And_Stays_Silent()
    {
        var tool = new CodeImpactTool(
            new QueryServiceStub(callers: [], impacted: []),
            maintenance: new MaintenanceStub(Status(ProjectId, watcherAttached: true)));

        var result = await ExecuteImpactAsync(tool, ImpactArgs);

        using var json = JsonDocument.Parse(StripNotes(result.Output!));
        var freshness = json.RootElement.GetProperty("index_freshness");
        Assert.AreEqual("idle", freshness.GetProperty("state").GetString());
        Assert.IsFalse(
            result.Output!.Contains(InfoMarker, StringComparison.Ordinal),
            "impact：索引已 idle 的空结果不得附说明（保持寂静）");
    }

    [TestMethod]
    public async Task Impact_Zero_Hits_Without_Maintenance_Reports_Unknown_And_Honest_Reason()
    {
        var tool = new CodeImpactTool(new QueryServiceStub(callers: [], impacted: []));

        var result = await ExecuteImpactAsync(tool, ImpactArgs);

        using var json = JsonDocument.Parse(StripNotes(result.Output!));
        var freshness = json.RootElement.GetProperty("index_freshness");
        Assert.AreEqual("unknown", freshness.GetProperty("state").GetString());
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(freshness.GetProperty("reason").GetString()),
            "impact：unknown 必须给出原因，不得留空");
        StringAssert.Contains(result.Output!, InfoMarker);
        StringAssert.Contains(result.Output!, "not proof that the symbol does not exist");
    }

    [TestMethod]
    public async Task Impact_With_Hits_While_Index_In_Flight_Reports_Rebuilding_But_Stays_Silent()
    {
        var tool = new CodeImpactTool(
            new QueryServiceStub(
                callers: [],
                impacted:
                [
                    new CodeSymbolRecord(
                        WorkspaceId, ProjectId, "C:\\repo\\B.cs",
                        SymbolId: "sym-9", Name: "Downstream", Kind: CodeSymbolKind.Method,
                        StartLine: 12, EndLine: 13),
                ]),
            maintenance: new MaintenanceStub(Status(ProjectId, indexInFlight: true)));

        var result = await ExecuteImpactAsync(tool, ImpactArgs);

        using var json = JsonDocument.Parse(StripNotes(result.Output!));
        Assert.AreEqual(1, json.RootElement.GetProperty("count").GetInt32());
        Assert.AreEqual(1, json.RootElement.GetProperty("impacted").GetArrayLength());
        Assert.AreEqual(
            "rebuilding",
            json.RootElement.GetProperty("index_freshness").GetProperty("state").GetString());
        Assert.IsFalse(
            result.Output!.Contains(InfoMarker, StringComparison.Ordinal),
            "impact：有命中时不得追加空结果说明");
    }

    [TestMethod]
    public async Task Callers_Zero_Hits_With_Needs_Reconcile_Surfaces_Reconcile_Reason()
    {
        var tool = new CodeCallersTool(
            new QueryServiceStub(callers: [], impacted: []),
            maintenance: new MaintenanceStub(Status(
                ProjectId,
                needsReconcile: true,
                reconcileReason: "scan-truncated")));

        var result = await ExecuteCallersAsync(tool, CallersArgs);

        using var json = JsonDocument.Parse(StripNotes(result.Output!));
        var freshness = json.RootElement.GetProperty("index_freshness");
        Assert.AreEqual("needs-reconcile", freshness.GetProperty("state").GetString());
        Assert.AreEqual("scan-truncated", freshness.GetProperty("reconcile_reason").GetString());
        StringAssert.Contains(result.Output!, "last reconcile reason:");
        StringAssert.Contains(result.Output!, "scan-truncated");
    }

    // ───────────────────────── 共享 helper 的行为锁定 ─────────────────────────

    [TestMethod]
    public void Shared_Empty_Result_Note_Helper_Is_Silent_For_Idle_And_Verbatim_Otherwise()
    {
        Assert.AreEqual(
            string.Empty,
            CodeQueryToolHelper.BuildEmptyResultFreshnessNote("idle", null, null, ProjectId),
            "idle 必须完全寂静（返回空串）");

        Assert.AreEqual(
            string.Empty,
            CodeQueryToolHelper.BuildEmptyResultFreshnessNote("pending-unknown", "r", "u", ProjectId),
            "未知状态同样不得发声（只有三个非 idle 状态才说话）");

        var rebuilding = CodeQueryToolHelper.BuildEmptyResultFreshnessNote(
            "rebuilding", "scan-truncated", null, ProjectId);
        Assert.AreEqual(
            "\n\n\u2139\uFE0F 0 hit(s), but the index for \"project-live\" is rebuilding (last reconcile reason: scan-truncated)"
            + " \u21D2 the empty result may be temporary and does NOT mean the symbol is absent; "
            + "retry later, or use search_grep for a live scan.",
            rebuilding);

        var unknown = CodeQueryToolHelper.BuildEmptyResultFreshnessNote(
            "unknown", null, "maintenance service not registered", ProjectId);
        Assert.AreEqual(
            "\n\n\u2139\uFE0F 0 hit(s); index freshness is unknown (maintenance service not registered)"
            + " \u21D2 the empty result is not proof that the symbol does not exist.",
            unknown);
    }

    // ─────────────────────────────── 测试基础设施 ───────────────────────────────

    /// <summary>输出 = JSON 主体 + 人类可读提示行；测试只解析 JSON 主体。</summary>
    private static string StripNotes(string output)
    {
        var index = output.IndexOf("\n\n", StringComparison.Ordinal);
        return index < 0 ? output : output[..index];
    }

    private static Task<ToolExecutionResult> ExecuteCallersAsync(CodeCallersTool tool, string argumentsJson) =>
        tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = argumentsJson,
            Context = new ToolExecutionContext
            {
                WorkspaceId = WorkspaceId,
                SessionId = "session-1",
                AgentInstanceId = "agent-1",
            },
        });

    private static Task<ToolExecutionResult> ExecuteImpactAsync(CodeImpactTool tool, string argumentsJson) =>
        tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = argumentsJson,
            Context = new ToolExecutionContext
            {
                WorkspaceId = WorkspaceId,
                SessionId = "session-1",
                AgentInstanceId = "agent-1",
            },
        });

    private static CodeIndexMaintenanceScopeStatus Status(
        string scopeId,
        bool indexPending = false,
        bool indexInFlight = false,
        bool needsReconcile = false,
        string? reconcileReason = null,
        bool watcherAttached = false,
        DateTimeOffset? lastCalibrationAtUtc = null,
        long unresolvedSourcePathCount = 0)
        => new(
            WorkspaceId,
            scopeId,
            RootPath: "C:\\repo",
            ObservedVersion: 0,
            DesiredVersion: 0,
            CommittedVersion: 0,
            MarkedWhileInFlightCount: 0,
            IndexPending: indexPending,
            IndexInFlight: indexInFlight,
            NeedsReconcile: needsReconcile,
            ReconcileReason: reconcileReason,
            ReconcileRequestCount: 0,
            RemovalObservationCount: 0,
            LastRemovalPaths: Array.Empty<string>(),
            RemovedFileCount: 0,
            IncrementallyIndexedFileCount: 0,
            ScopeEscalationCount: 0,
            SweptFileCount: 0,
            CalibrationRunCount: 0,
            RejectedCalibrationRunCount: 0,
            LastCalibrationAtUtc: lastCalibrationAtUtc,
            RecentObservationCount: 0,
            WatcherAttached: watcherAttached,
            SourceMaintenanceUnresolvedPathCount: unresolvedSourcePathCount);

    private sealed class MaintenanceStub(params CodeIndexMaintenanceScopeStatus[] statuses) : ICodeIndexMaintenance
    {
        private readonly IReadOnlyList<CodeIndexMaintenanceScopeStatus> _statuses = statuses;

        public bool IsRunning => true;

        public long BatchesProcessed => 0;

        public long ReconcileRequests => 0;

        public long RemovalObservations => 0;

        public int PendingReconcileScopeCount => _statuses.Count(s => s.NeedsReconcile);

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public bool EnsureScope(string workspaceId, string scopeId, string rootPath) => true;

        public IReadOnlyList<CodeIndexMaintenanceScopeStatus> GetScopeStatuses() => _statuses;

        public CodeIndexMaintenanceScopeStatus? GetScopeStatus(string workspaceId, string scopeId) =>
            _statuses.FirstOrDefault(s =>
                string.Equals(s.WorkspaceId, workspaceId, StringComparison.Ordinal)
                && string.Equals(s.ScopeId, scopeId, StringComparison.Ordinal));
    }

    private sealed class QueryServiceStub(
        IReadOnlyList<CodeRelationRecord> callers,
        IReadOnlyList<CodeSymbolRecord> impacted) : ICodeQueryService
    {
        public Task<IReadOnlyList<CodeSymbolDetail>> SearchSymbolsAsync(
            CodeSymbolSearchRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CodeSymbolDetail>>([]);

        public Task<CodeIndexResult> GetProjectIndexStatusAsync(
            string workspaceId, string projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CodeSymbolRecord>> ExploreAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CodeRelationRecord>> GetCallersAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) =>
            Task.FromResult(callers);

        public Task<IReadOnlyList<CodeRelationRecord>> GetCalleesAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CodeSymbolRecord>> GetImpactAsync(
            string workspaceId, string projectId, string symbolId, int maxDepth = 3,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(impacted);
    }
}
