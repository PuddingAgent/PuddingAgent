using Microsoft.Extensions.Logging.Abstractions;
using PuddingAgent.Services;
using PuddingCode.Configuration;
using PuddingCodeIndex.Contracts;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// S-A2 的测试替身（codeIndex 块）。
/// <para>
/// <b>替身纪律</b>：只有**查询型**成员返回数据；生命周期与写成员（<c>StartAsync</c> / <c>StopAsync</c> /
/// <c>EnsureScope</c> / <c>AddProjectAsync</c> / <c>ForgetScopeAsync</c> …）一律**直接抛** ——
/// 于是「状态查询顺手启动驱动 / 顺手登记项目」会被立刻抓住，而不用去数调用次数。
/// </para>
/// <para>
/// 全部数据来自内存，**零磁盘、零数据库**：既保证确定性，也保证不会碰到运行中的
/// <c>D:\Data\databases\code-index\code_index.db</c>。
/// </para>
/// </summary>
internal sealed class Sa2ScopeRegistryStub(params CodeIndexScope[] scopes) : ICodeIndexScopeRegistry
{
    public Task<IReadOnlyList<CodeIndexScope>> ListScopesAsync(
        string workspaceId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CodeIndexScope>>(
            scopes.Where(s => string.Equals(s.WorkspaceId, workspaceId, StringComparison.Ordinal)).ToArray());

    public Task<CodeIndexScope> EnsureScopeAsync(
        string workspaceId,
        string rootPath,
        ScopeSource source,
        string? displayName = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("状态查询不得登记 scope（写操作）。");

    public Task<CodeIndexResult> ForgetScopeAsync(
        string workspaceId,
        string scopeId,
        bool removeIndexData = true,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("状态查询不得注销 scope（写操作）。");

    public Task<CodeIndexScope?> GetScopeAsync(
        string workspaceId,
        string scopeId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("S-A2 只用 ListScopesAsync；本条若被调用，说明探针绕过了唯一的枚举真源。");

    public Task<CodeIndexScope?> FindCoveringScopeAsync(
        string workspaceId,
        string directoryPath,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("S-A2 只用 ListScopesAsync；本条若被调用，说明探针绕过了唯一的枚举真源。");
}

/// <summary>项目记录替身：只暴露 <c>ListProjectsAsync</c>（用于取**原始**注册态）。</summary>
internal sealed class Sa2ProjectRegistryStub(params CodeProjectRecord[] records) : ICodeProjectRegistry
{
    public Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(
        string workspaceId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CodeProjectRecord>>(
            records.Where(r => string.Equals(r.WorkspaceId, workspaceId, StringComparison.Ordinal)).ToArray());

    public Task<CodeIndexResult> AddProjectAsync(CodeProjectAddRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("状态查询不得登记项目（写操作）。");

    public Task<CodeIndexResult> RemoveProjectAsync(CodeProjectRemoveRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("状态查询不得移除项目（写操作）。");

    public Task<CodeProjectRecord?> GetProjectAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("S-A2 只用 ListProjectsAsync；本条若被调用，说明探针多打了一次存储。");
}

/// <summary>
/// 维护驱动替身：<c>GetScopeStatuses</c> 返回注入的挂载 scope；计数属性给**非零固定值**
/// （透明传参才可能通过断言 —— 全给 0 会让「有没有透传」无法区分）。
/// </summary>
internal sealed class Sa2MaintenanceStub(params CodeIndexMaintenanceScopeStatus[] statuses) : ICodeIndexMaintenance
{
    /// <summary>驱动是否在跑（真源：生产上由 <c>StartAsync</c> 翻转）。</summary>
    internal bool Running { get; set; } = true;

    internal const long BatchesProcessedValue = 42;
    internal const long ReconcileRequestsValue = 3;
    internal const long RemovalObservationsValue = 5;
    internal const int PendingReconcileScopeCountValue = 2;

    public bool IsRunning => Running;

    public long BatchesProcessed => BatchesProcessedValue;

    public long ReconcileRequests => ReconcileRequestsValue;

    public long RemovalObservations => RemovalObservationsValue;

    public int PendingReconcileScopeCount => PendingReconcileScopeCountValue;

    public IReadOnlyList<CodeIndexMaintenanceScopeStatus> GetScopeStatuses() => statuses;

    public CodeIndexMaintenanceScopeStatus? GetScopeStatus(string workspaceId, string scopeId) =>
        statuses.FirstOrDefault(s =>
            string.Equals(s.WorkspaceId, workspaceId, StringComparison.Ordinal)
            && string.Equals(s.ScopeId, scopeId, StringComparison.Ordinal));

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("状态查询不得启动维护驱动。");

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("状态查询不得停止维护驱动。");

    public bool EnsureScope(string workspaceId, string scopeId, string rootPath) =>
        throw new InvalidOperationException("状态查询不得挂载 scope。");
}

/// <summary>S-A2 样例数据与探针工厂。</summary>
internal static class Sa2Samples
{
    /// <summary>现行注册表快照里的 workspace（workspaces 目录名即 workspace id）。</summary>
    internal const string WorkspaceId = "default";

    /// <summary>现行注册表的 4 个项目（2026-10-01 实测；id 与根路径原样抄自实测结果）。</summary>
    internal static readonly (string ProjectId, string RootPath, string DisplayName)[] RegisteredProjects =
    [
        ("8a48458b30150fdbed4baaced35d24cf", @"D:\CodeProject\PuddingAgent\PuddingAgent", "PuddingAgent"),
        ("8fdcca4279da2b84e07578d6a2fa07c1", @"D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCore", "PuddingCore"),
        ("64d573efb2bd471064ca30df1c720867", @"D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingPlatform", "PuddingPlatform"),
        ("c402018755330b95325caf8481e2b8dc", @"D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingRuntime", "PuddingRuntime"),
    ];

    /// <summary>
    /// 23 字段维护态记录（**逐字段显式赋值**：字段若被改名/删项，本文件与断言矩阵会一起报警）。
    /// </summary>
    internal static CodeIndexMaintenanceScopeStatus MaintenanceStatus(
        string scopeId,
        string rootPath,
        bool indexPending = true) =>
        new(
            WorkspaceId: WorkspaceId,
            ScopeId: scopeId,
            RootPath: rootPath,
            ObservedVersion: 11,
            DesiredVersion: 12,
            CommittedVersion: 10,
            MarkedWhileInFlightCount: 1,
            IndexPending: indexPending,
            IndexInFlight: false,
            NeedsReconcile: true,
            ReconcileReason: "removal-observed",
            ReconcileRequestCount: 2,
            RemovalObservationCount: 3,
            LastRemovalPaths: [@"D:\code\a.cs"],
            RemovedFileCount: 4,
            IncrementallyIndexedFileCount: 5,
            ScopeEscalationCount: 6,
            SweptFileCount: 7,
            CalibrationRunCount: 8,
            RejectedCalibrationRunCount: 9,
            LastCalibrationAtUtc: new DateTimeOffset(2026, 10, 1, 4, 52, 56, TimeSpan.Zero),
            RecentObservationCount: 13,
            WatcherAttached: true);

    /// <summary>注册表条目（<see cref="CodeIndexScope"/>）。</summary>
    internal static CodeIndexScope Scope(
        string projectId,
        string rootPath,
        ScopeState state = ScopeState.Active,
        string? displayName = null) =>
        new(WorkspaceId, projectId, rootPath, state, ScopeSource.Manual, displayName, null, null);

    /// <summary>
    /// 「不关心 codeIndex 块」的既有测试用：0 个项目、驱动未运行，且 dataPaths 指向一个**不存在**的数据根
    /// （于是 workspace 枚举为空 ⇒ 探针不会读任何真实数据库）。
    /// </summary>
    internal static CodeIndexStatusProbe EmptyProbeWithoutDisk() =>
        new(
            new Sa2MaintenanceStub { Running = false },
            new Sa2ScopeRegistryStub(),
            new Sa2ProjectRegistryStub(),
            PuddingDataPaths.FromRoot(Path.Combine(Path.GetTempPath(), "pudding-sa2-absent-data-root")),
            NullLogger<CodeIndexStatusProbe>.Instance);
}
