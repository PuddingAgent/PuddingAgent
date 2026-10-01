using System.Globalization;
using System.Text.Json;
using PuddingCode.Tools;
using PuddingCodeIndex.Contracts;
using PuddingCodeIntelligence.Contracts;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// D1 的**一半**（2026-10-01）：<c>code_index_status</c> 必须 fail-closed ——
/// 未在**注册表**登记的项目不得再返回索引视图里的陈旧 <c>Completed</c>；
/// 已登记项目必须**逐字不变**（回归线，见 <see cref="Registered_Project_Output_Is_Frozen_Byte_For_Byte"/>）。
/// 另一半（<c>code_symbol_search</c> 的陈旧死路径，D2）是独立切片，本文件刻意不涉及。
/// <para>
/// 缺陷登记：<c>Docs/Features/Index-Retrieval-Known-Defects-2026-10-01.md</c> D1。
/// </para>
/// <para>
/// 本文件全部为**假实现**（不碰 SQLite、不碰索引库），因此可在宿主运行时并行跑，不触发 database is locked。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeIndexStatusRegistryGateTests
{
    private const string WorkspaceId = "ws-1";

    /// <summary>已登记项目的 id 取自 D1 实测现场（根项目）。</summary>
    private const string RegisteredProjectId = "8a48458b30150fdbed4baaced35d24cf";

    /// <summary>D1 实测中「已从注册表移除、却仍返回 Completed」的两个 scope 之一。</summary>
    private const string UnregisteredProjectId = "scope-6526fb344e33";

    private static readonly DateTimeOffset CompletedAtUtc =
        DateTimeOffset.Parse("2026-10-01T03:30:50+00:00", CultureInfo.InvariantCulture);

    /// <summary>
    /// 「已登记项目」返回体的**冻结原文**：由**改动前**的实现实跑捕获（见报告 EVIDENCE）。
    /// 这是 requirement 3 的回归线 —— 任何字段增删、改名、取值或顺序变化都会让它变红。
    /// </summary>
    private const string FrozenRegisteredOutput = """
{
  "workspace_id": "ws-1",
  "project_id": "8a48458b30150fdbed4baaced35d24cf",
  "status": "Completed",
  "message": "Index is complete.",
  "started_at_utc": null,
  "completed_at_utc": "2026-10-01T03:30:50+00:00"
}
""";

    // ──────────────────────────────────────────────────────────────
    // 未登记 ⇒ fail-closed
    // ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Unregistered_Project_Is_Fail_Closed_And_Never_Reads_Index_Status()
    {
        var service = new RecordingQueryService();
        var registry = new RecordingProjectRegistry(RegisteredProjectId);
        var tool = new CodeIndexStatusTool(service, resolver: null, registry);

        var result = await ExecuteAsync(tool, $$"""{"project_id":"{{UnregisteredProjectId}}"}""");

        Assert.IsTrue(result.Success, result.Error);

        using var document = JsonDocument.Parse(result.Output);
        var status = document.RootElement.GetProperty("status").GetString();
        Assert.AreEqual("not_registered", status,
            "未登记项目不得再返回索引视图里的陈旧 Completed —— 这正是 D1 的漏洞");
        Assert.AreNotEqual("Completed", status, "显式可区分：不得与真实完成态撞词");

        StringAssert.Contains(
            document.RootElement.GetProperty("message").GetString() ?? string.Empty,
            "code_index_list_projects",
            "必须把调用方指向注册表的真源");

        Assert.IsNull(
            document.RootElement.GetProperty("completed_at_utc").GetString(),
            "fail-closed 结果不得泄露陈旧的完成时间（否则面板照旧能把它渲染成已索引）");

        Assert.AreEqual(0, service.StatusCallCount,
            "未登记 ⇒ 根本不该去读索引视图；读了就说明门槛没落在最前面");
    }

    // ──────────────────────────────────────────────────────────────
    // 已登记 ⇒ 逐字不变（回归线）
    // ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Registered_Project_Output_Is_Frozen_Byte_For_Byte()
    {
        var service = new RecordingQueryService();
        var registry = new RecordingProjectRegistry(RegisteredProjectId);
        var tool = new CodeIndexStatusTool(service, resolver: null, registry);

        var result = await ExecuteAsync(tool, $$"""{"project_id":"{{RegisteredProjectId}}"}""");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(
            NormalizeLineEndings(FrozenRegisteredOutput),
            NormalizeLineEndings(result.Output),
            "已登记项目的返回体必须逐字不变（只允许行尾差异）");

        Assert.AreEqual(1, service.StatusCallCount, "已登记 ⇒ 仍然恰好读一次索引状态");
        Assert.AreEqual(WorkspaceId, service.LastWorkspaceId);
        Assert.AreEqual(RegisteredProjectId, service.LastProjectId);
    }

    // ──────────────────────────────────────────────────────────────
    // 注册表缺席 ⇒ 与同目录既有工具同一句 Fail 文案
    // ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Missing_Registry_Fails_Closed_With_The_Sibling_Tools_Message()
    {
        var service = new RecordingQueryService();
        var tool = new CodeIndexStatusTool(service, resolver: null);

        var result = await ExecuteAsync(tool, $$"""{"project_id":"{{RegisteredProjectId}}"}""");

        Assert.IsFalse(result.Success, "注册表不可用必须显式失败，不得静默当成『已登记』放行");
        Assert.AreEqual(
            "Code project tools are not available: ICodeProjectRegistry is not registered.",
            result.Error,
            "必须与 CodeProjectManagementTools 的同一句文案逐字一致");
        Assert.AreEqual(0, service.StatusCallCount);
    }

    // ──────────────────────────────────────────────────────────────
    // 自动探测出来的 project_id 同样过门槛
    // ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Auto_Detected_Project_Is_Also_Gated_By_The_Registry()
    {
        const string autoDetectedScopeId = "scope-autodetected";
        var service = new RecordingQueryService();
        var registry = new RecordingProjectRegistry(RegisteredProjectId);
        var resolver = new FixedScopeResolver(autoDetectedScopeId);
        var tool = new CodeIndexStatusTool(service, resolver, registry);

        var result = await ExecuteAsync(tool, """{"file_path":"D:\\repo\\src\\Anything.cs"}""");

        Assert.IsTrue(result.Success, result.Error);
        using var document = JsonDocument.Parse(result.Output);
        Assert.AreEqual(autoDetectedScopeId,
            document.RootElement.GetProperty("project_id").GetString());
        Assert.AreEqual("not_registered",
            document.RootElement.GetProperty("status").GetString(),
            "门槛必须落在『最终使用的 project_id』上，而不只是显式入参上");
        Assert.AreEqual(0, service.StatusCallCount);
    }

    // ──────────────────────────────────────────────────────────────
    // helpers / fakes
    // ──────────────────────────────────────────────────────────────

    private static string NormalizeLineEndings(string value) => value.Replace("\r\n", "\n");

    private static Task<ToolExecutionResult> ExecuteAsync(CodeIndexStatusTool tool, string argumentsJson) =>
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

    /// <summary>记录调用次数的查询服务：状态恒为 <c>Completed</c>（即 D1 里那个"陈旧的乐观答案"）。</summary>
    private sealed class RecordingQueryService : ICodeQueryService
    {
        public int StatusCallCount { get; private set; }
        public string? LastWorkspaceId { get; private set; }
        public string? LastProjectId { get; private set; }

        public Task<CodeIndexResult> GetProjectIndexStatusAsync(
            string workspaceId, string projectId, CancellationToken cancellationToken = default)
        {
            StatusCallCount++;
            LastWorkspaceId = workspaceId;
            LastProjectId = projectId;
            return Task.FromResult(new CodeIndexResult(
                true,
                CodeIndexStatus.Completed,
                "Index is complete.",
                StartedAtUtc: null,
                CompletedAtUtc: CompletedAtUtc,
                WorkspaceId: workspaceId,
                ProjectId: projectId));
        }

        public Task<IReadOnlyList<CodeSymbolDetail>> SearchSymbolsAsync(
            CodeSymbolSearchRequest request, CancellationToken cancellationToken = default) =>
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

    /// <summary>
    /// 注册表假实现：真源只有 <see cref="ListProjectsAsync"/> —— 与 <c>code_index_list_projects</c> 同一个 API。
    /// <para>
    /// <see cref="GetProjectAsync"/> 刻意 <c>throw</c>：注册表契约里它**不过滤</b> 已 <c>Removed</c> 的行
    /// （见 <c>SqliteCodeIndexStore.ListProjectsAsync</c> 的 <c>Status &lt;&gt; Removed</c> 与
    /// <c>GetProjectAsync</c> 的无过滤 SELECT），因此它**不是** list 工具的口径。
    /// 这里的抛异常把「必须走 ListProjectsAsync」变成一条**会失败的断言**，而不是一句注释。
    /// </para>
    /// </summary>
    private sealed class RecordingProjectRegistry : ICodeProjectRegistry
    {
        private readonly string[] _registeredProjectIds;

        public RecordingProjectRegistry(params string[] registeredProjectIds) =>
            _registeredProjectIds = registeredProjectIds;

        public List<string> ListedWorkspaceIds { get; } = [];

        public Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(
            string workspaceId, CancellationToken cancellationToken = default)
        {
            ListedWorkspaceIds.Add(workspaceId);
            IReadOnlyList<CodeProjectRecord> records = _registeredProjectIds
                .Select(id => new CodeProjectRecord(
                    workspaceId, id, @"D:\repo", CodeProjectStatus.Active))
                .ToArray();
            return Task.FromResult(records);
        }

        public Task<CodeIndexResult> AddProjectAsync(
            CodeProjectAddRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CodeIndexResult> RemoveProjectAsync(
            CodeProjectRemoveRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CodeProjectRecord?> GetProjectAsync(
            string workspaceId, string projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "注册表判定必须走 ListProjectsAsync（code_index_list_projects 的真源），不能走无过滤的 GetProjectAsync");
    }

    /// <summary>固定返回一个 scope 的解析器，用于验证自动探测路径同样过门槛。</summary>
    private sealed class FixedScopeResolver : ICodeIndexScopeResolver
    {
        private readonly string _scopeId;

        public FixedScopeResolver(string scopeId) => _scopeId = scopeId;

        public Task<ScopeResolution?> ResolveAsync(
            string workspaceId, string? filePath = null, string? scopePath = null,
            string? scopeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<ScopeResolution?>(Build(workspaceId));

        public Task<ScopeResolution> ResolveAndEnsureAsync(
            string workspaceId, string? filePath = null, string? scopePath = null,
            string? scopeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Build(workspaceId));

        private ScopeResolution Build(string workspaceId) => new(
            new CodeIndexScope(workspaceId, _scopeId, @"D:\repo", ScopeState.Active, ScopeSource.Auto),
            IsAutoDetected: true,
            IsNewlyCreated: false);
    }
}
