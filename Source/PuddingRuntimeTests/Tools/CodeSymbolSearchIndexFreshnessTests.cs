using System.Text.Json;
using PuddingCode.Tools;
using PuddingCodeIndex.Contracts;
using PuddingCodeIntelligence.Contracts;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// T1 的接入面：<c>code_symbol_search</c> 的结果必须能区分「索引重建中（空结果只是暂时的）」
/// 与「代码真的不存在」。
/// <para>
/// 断言两件事：① 返回体新增 <c>index_freshness.state</c>，且当 maintenance 缺失或 scope 未附着时
/// 如实为 <c>unknown</c>（绝不猜成 idle）；② 仅当 0 命中且索引非 idle 时，文本才追加一行诚实说明，
/// 有结果或已 idle 时保持寂静（不误报、不喊狼来了）。既有字段名/语义保持不变。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeSymbolSearchIndexFreshnessTests
{
    private const string WorkspaceId = "workspace-1";
    private const string ProjectId = "project-live";

    private static Task<ToolExecutionResult> ExecuteAsync(CodeSymbolSearchTool tool, string argumentsJson) =>
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

    [TestMethod]
    public async Task Zero_Hits_While_Index_In_Flight_Reports_Rebuilding_And_Says_May_Be_Temporary()
    {
        var tool = new CodeSymbolSearchTool(
            new SearchQueryService([]),
            resolver: null,
            new RegistryStub([Project(ProjectId, "C:\\repo")]),
            new MaintenanceStub(Status(ProjectId, indexInFlight: true)));

        var result = await ExecuteAsync(tool, """{"query":"Widget","project_id":"project-live"}""");

        using var json = JsonDocument.Parse(StripNotes(result.Output!));
        Assert.AreEqual(
            "rebuilding",
            json.RootElement.GetProperty("index_freshness").GetProperty("state").GetString());
        StringAssert.Contains(result.Output!, "may be temporary");
    }

    [TestMethod]
    public async Task Zero_Hits_Needs_Reconcile_Reports_State_And_Surfaces_Reconcile_Reason()
    {
        var tool = new CodeSymbolSearchTool(
            new SearchQueryService([]),
            resolver: null,
            new RegistryStub([Project(ProjectId, "C:\\repo")]),
            new MaintenanceStub(Status(
                ProjectId,
                needsReconcile: true,
                reconcileReason: "scan-truncated")));

        var result = await ExecuteAsync(tool, """{"query":"Widget","project_id":"project-live"}""");

        using var json = JsonDocument.Parse(StripNotes(result.Output!));
        var freshness = json.RootElement.GetProperty("index_freshness");
        Assert.AreEqual("needs-reconcile", freshness.GetProperty("state").GetString());
        Assert.AreEqual("scan-truncated", freshness.GetProperty("reconcile_reason").GetString());
        StringAssert.Contains(result.Output!, "may be temporary");
        StringAssert.Contains(result.Output!, "last reconcile reason:");
        StringAssert.Contains(result.Output!, "scan-truncated");
    }

    [TestMethod]
    public async Task Zero_Hits_With_Attached_Watcher_Reports_Idle_And_Stays_Silent()
    {
        var tool = new CodeSymbolSearchTool(
            new SearchQueryService([]),
            resolver: null,
            new RegistryStub([Project(ProjectId, "C:\\repo")]),
            new MaintenanceStub(Status(ProjectId, watcherAttached: true)));

        var result = await ExecuteAsync(tool, """{"query":"Widget","project_id":"project-live"}""");

        using var json = JsonDocument.Parse(StripNotes(result.Output!));
        var freshness = json.RootElement.GetProperty("index_freshness");
        Assert.AreEqual("idle", freshness.GetProperty("state").GetString());
        Assert.IsTrue(freshness.GetProperty("watcher_attached").GetBoolean());
        Assert.IsFalse(
            result.Output!.Contains("may be temporary", StringComparison.Ordinal),
            "索引已 idle 时不得无中生有地喊狼来了");
    }

    [TestMethod]
    public async Task Zero_Hits_Without_Maintenance_Reports_Unknown_And_Honest_Reason()
    {
        var tool = new CodeSymbolSearchTool(
            new SearchQueryService([]),
            resolver: null,
            new RegistryStub([Project(ProjectId, "C:\\repo")]));

        var result = await ExecuteAsync(tool, """{"query":"Widget","project_id":"project-live"}""");

        using var json = JsonDocument.Parse(StripNotes(result.Output!));
        var freshness = json.RootElement.GetProperty("index_freshness");
        Assert.AreEqual("unknown", freshness.GetProperty("state").GetString());
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(freshness.GetProperty("reason").GetString()),
            "unknown 必须给出原因，不得留空");
        StringAssert.Contains(result.Output!, "not proof that the symbol does not exist");
    }

    [TestMethod]
    public async Task With_Hits_While_Index_In_Flight_Reports_Rebuilding_But_Stays_Silent()
    {
        var temp = Directory.CreateTempSubdirectory("pudding-t1-hits");
        try
        {
            var file = Path.Combine(temp.FullName, "Widget.cs");
            await File.WriteAllTextAsync(file, "class Widget {}");
            var tool = new CodeSymbolSearchTool(
                new SearchQueryService([Detail(file, ProjectId)]),
                resolver: null,
                new RegistryStub([Project(ProjectId, temp.FullName)]),
                new MaintenanceStub(Status(ProjectId, indexInFlight: true)));

            var result = await ExecuteAsync(tool, """{"query":"Widget","project_id":"project-live"}""");

            using var json = JsonDocument.Parse(StripNotes(result.Output!));
            Assert.IsTrue(json.RootElement.GetProperty("count").GetInt32() > 0);
            Assert.AreEqual(
                "rebuilding",
                json.RootElement.GetProperty("index_freshness").GetProperty("state").GetString());
            Assert.IsFalse(
                result.Output!.Contains("may be temporary", StringComparison.Ordinal),
                "有命中时不得追加空结果说明");
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task Existing_Coverage_Fields_Are_Still_Present_And_Unchanged()
    {
        var tool = new CodeSymbolSearchTool(
            new SearchQueryService([]),
            resolver: null,
            new RegistryStub([Project(ProjectId, "C:\\repo")]),
            new MaintenanceStub(Status(ProjectId, watcherAttached: true)));

        var result = await ExecuteAsync(tool, """{"query":"Widget","project_id":"project-live"}""");

        using var json = JsonDocument.Parse(StripNotes(result.Output!));
        Assert.AreEqual(
            "project-live",
            json.RootElement.GetProperty("searched_scope").GetString());
        Assert.IsTrue(json.RootElement.GetProperty("complete").GetBoolean());
        Assert.AreEqual(0, json.RootElement.GetProperty("stale_skipped").GetInt32());
    }

    [TestMethod]
    public async Task Aggregate_Without_Project_Reports_Pending_When_Any_Scope_Pends()
    {
        var tool = new CodeSymbolSearchTool(
            new SearchQueryService([]),
            resolver: null,
            new RegistryStub([]),
            new MaintenanceStub(
                Status("scope-a", watcherAttached: true),
                Status("scope-b", indexPending: true, watcherAttached: true)));

        var result = await ExecuteAsync(tool, """{"query":"Widget"}""");

        using var json = JsonDocument.Parse(StripNotes(result.Output!));
        var freshness = json.RootElement.GetProperty("index_freshness");
        Assert.AreEqual("pending", freshness.GetProperty("state").GetString());
        Assert.IsTrue(freshness.GetProperty("index_pending").GetBoolean());
        StringAssert.Contains(result.Output!, "may be temporary");
    }

    [TestMethod]
    public async Task Empty_Scope_List_Reports_Unknown_Without_Guessing_Idle()
    {
        var tool = new CodeSymbolSearchTool(
            new SearchQueryService([]),
            resolver: null,
            new RegistryStub([]),
            new MaintenanceStub());

        var result = await ExecuteAsync(tool, """{"query":"Widget"}""");

        using var json = JsonDocument.Parse(StripNotes(result.Output!));
        var freshness = json.RootElement.GetProperty("index_freshness");
        Assert.AreEqual("unknown", freshness.GetProperty("state").GetString());
        Assert.AreEqual(
            "no scope attached to the maintenance driver",
            freshness.GetProperty("reason").GetString());
        StringAssert.Contains(result.Output!, "not proof that the symbol does not exist");
    }

    /// <summary>输出 = JSON 主体 + 人类可读提示行；测试只解析 JSON 主体。</summary>
    private static string StripNotes(string output)
    {
        var index = output.IndexOf("\n\n", StringComparison.Ordinal);
        return index < 0 ? output : output[..index];
    }

    private static CodeSymbolDetail Detail(string filePath, string projectId)
        => new(new CodeSymbolRecord(
            WorkspaceId,
            projectId,
            filePath,
            SymbolId: "sym-1",
            Name: "Widget",
            Kind: CodeSymbolKind.Class,
            StartLine: 1,
            EndLine: 1));

    private static CodeProjectRecord Project(string projectId, string rootPath)
        => new(WorkspaceId, projectId, rootPath, CodeProjectStatus.Active);

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

    private sealed class SearchQueryService(IReadOnlyList<CodeSymbolDetail> hits) : ICodeQueryService
    {
        public int SearchCallCount { get; private set; }

        public Task<IReadOnlyList<CodeSymbolDetail>> SearchSymbolsAsync(
            CodeSymbolSearchRequest request, CancellationToken cancellationToken = default)
        {
            SearchCallCount++;
            return Task.FromResult(hits);
        }

        public Task<CodeIndexResult> GetProjectIndexStatusAsync(
            string workspaceId, string projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CodeSymbolRecord>> ExploreAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CodeRelationRecord>> GetCallersAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CodeRelationRecord>> GetCalleesAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CodeSymbolRecord>> GetImpactAsync(
            string workspaceId, string projectId, string symbolId, int maxDepth = 3,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RegistryStub(IReadOnlyList<CodeProjectRecord> projects) : ICodeProjectRegistry
    {
        public Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(
            string workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(projects);

        public Task<CodeProjectRecord?> GetProjectAsync(
            string workspaceId, string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(projects.FirstOrDefault(p =>
                string.Equals(p.ProjectId, projectId, StringComparison.Ordinal)));

        public Task<CodeIndexResult> AddProjectAsync(
            CodeProjectAddRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CodeIndexResult> RemoveProjectAsync(
            CodeProjectRemoveRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
