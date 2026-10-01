using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCodeIndex.Contracts;

namespace PuddingAgent.Services;

// ────────────────────────────────────────────────────────────────────────
// 切片 S-A2（2026-10-01）：GET /api/admin/index/status 的**第二块** —— 符号 / 代码索引（codeIndex）。
//
// 与 fullText 块的分工：fullText 那块的字段名与结构**冻结**（前端已按它上线），本切片只做**新增块**。
//
// D1/D2 的正确性要求**做进数据里**（不是写在注释里）：
//   · 未在注册表登记的项目（D1：注册表与查询视图口径不一致）⇒ stale = true；
//   · 根路径不存在的项目（D2：陈旧 E:\ 路径仍被服务）⇒ rootPathExists = false 且 stale = true。
//   于是面板**不可能**把「已注销 / 指向死路径」的条目渲染成健康。
//
// D3 的口径：**注册态**（registry / 项目记录）与**维护态**（维护驱动，含 indexPending）是**两个独立字段**，
// 不合并成一个「状态」；各自真源见 <see cref="CodeIndexProjectStatusSnapshot"/> 的属性注释。
//
// 只读：全部走查询型 API（ListScopesAsync / ListProjectsAsync / GetScopeStatuses），
// 不打开写事务、不调用任何会修改索引库的接口。
// ────────────────────────────────────────────────────────────────────────

/// <summary>codeIndex 块：符号 / 代码索引的只读快照。</summary>
/// <param name="WorkspaceIds">
/// 本次快照遍历的 workspace（真源：<see cref="PuddingDataPaths.WorkspacesRoot"/> 下的目录名，
/// 与 <c>CodeIndexMaintenanceHostedService</c> 的挂载循环同一口径 —— 平台的约定是「workspace id = workspaces 下的目录名」）。
/// </param>
/// <param name="MaintenanceRunning">维护驱动 <see cref="ICodeIndexMaintenance.IsRunning"/>；整块降级时为 <c>null</c>。</param>
/// <param name="BatchesProcessed">驱动自构造以来处理的合并批次总数（组件原值）。</param>
/// <param name="ReconcileRequests">需要重建索引的批次总数（组件原值）。</param>
/// <param name="RemovalObservations">观测到的删除路径总数（组件原值）。</param>
/// <param name="PendingReconcileScopeCount">当前待重建的 scope 数（组件原值）。</param>
/// <param name="Projects">逐项目条目（注册表 ∪ 维护驱动，按 workspace/projectId 稳定排序）。</param>
/// <param name="Note">
/// 整块读不出来时的**如实原因**（R5：未知 ≠ 健康，也 ≠ 0）；正常为 <c>null</c>。
/// </param>
public sealed record CodeIndexStatusDetailSnapshot(
    IReadOnlyList<string> WorkspaceIds,
    bool? MaintenanceRunning,
    long? BatchesProcessed,
    long? ReconcileRequests,
    long? RemovalObservations,
    int? PendingReconcileScopeCount,
    IReadOnlyList<CodeIndexProjectStatusSnapshot> Projects,
    string? Note);

/// <summary>
/// 单个项目的只读条目。
/// <para>
/// <b>D3：两个状态字段，两个真源，绝不合并</b>
/// </para>
/// <list type="bullet">
/// <item><see cref="RegistrationState"/> / <see cref="RegistrationStatus"/> / <see cref="RegistrationSource"/>
/// —— <b>注册态</b>。真源是索引注册表（SQLite 项目记录表）：
/// <see cref="RegistrationState"/> 是 <see cref="ICodeIndexScopeRegistry"/> 的投影
/// （<see cref="ScopeState"/>：Active/Covered/Removed/Failed），<see cref="RegistrationStatus"/> 是同一行的
/// <b>原始生命周期状态</b>（<see cref="CodeProjectStatus"/>：含 <c>Registering</c> —— 注册表契约把这个字面量
/// 投影掉了，只有原始记录还留着它）。两者同源不同投影，因此都给出，而不是二选一。</item>
/// <item><see cref="Maintenance"/> —— <b>维护态</b>。真源是维护驱动进程内状态
/// （<see cref="ICodeIndexMaintenance.GetScopeStatuses"/> 的 23 字段记录，**原样透传**，
/// 其中 <c>indexPending</c> 就是 D3 里那个与 <c>Registering</c> 混为一谈的 <c>Pending</c>），
/// 未挂到驱动上时为 <c>null</c> 并由 <see cref="MaintenanceReason"/> 如实说明原因。</item>
/// </list>
/// <para>
/// <b>陈旧判定</b>：<see cref="Stale"/> = 未在注册表中 <b>或</b> <see cref="RootPathExists"/> == false。
/// 这是 D1（未登记仍作答）与 D2（死路径仍被服务）在本端点上的 fail-closed 落点。
/// 「未挂到维护驱动」（<see cref="Maintenance"/> == null）**不**等于陈旧：那是「欠一次运行」，
/// 属运行事实，不属「注册/路径」事实。
/// </para>
/// </summary>
/// <param name="WorkspaceId">拥有该项目的 workspace。</param>
/// <param name="ProjectId">项目 / scope 标识（注册表与维护驱动用同一 id 空间）。</param>
/// <param name="DisplayName">展示名；仅在注册表条目上有值，仅来自维护驱动的条目为 <c>null</c>。</param>
/// <param name="RootPath">项目根路径（注册表优先；仅来自维护驱动的条目取驱动观测到的路径）。</param>
/// <param name="Registered">是否在 <see cref="ICodeIndexScopeRegistry"/> 中登记（D1 的判定输入）。</param>
/// <param name="RegistrationState">注册态（注册表投影，<see cref="ScopeState"/> 名）；未登记为 <c>null</c>。</param>
/// <param name="RegistrationStatus">注册态（项目记录原始 <see cref="CodeProjectStatus"/> 名，含 <c>Registering</c>）。</param>
/// <param name="RegistrationSource">scope 来源（<see cref="ScopeSource"/> 名：Manual/Auto/Pinned）。</param>
/// <param name="Maintenance">维护态 23 字段（原样透传）；未挂到驱动上为 <c>null</c>。</param>
/// <param name="MaintenanceReason">维护态缺席时的如实原因；正常为 <c>null</c>。</param>
/// <param name="RootPathExists">对 <paramref name="RootPath"/> 做的**目录存在性**判定（D2 的判定输入）。</param>
/// <param name="Stale">陈旧：未登记 <b>或</b> 根路径不存在。</param>
public sealed record CodeIndexProjectStatusSnapshot(
    string WorkspaceId,
    string ProjectId,
    string? DisplayName,
    string RootPath,
    bool Registered,
    string? RegistrationState,
    string? RegistrationStatus,
    string? RegistrationSource,
    CodeIndexMaintenanceScopeStatus? Maintenance,
    string? MaintenanceReason,
    bool RootPathExists,
    bool Stale);

/// <summary>
/// codeIndex 块的只读探针（S-A2）。
/// <para>
/// <b>为什么单独一个类</b>：全文索引块的探针（<see cref="FullTextIndexStatusProbe"/>）已经承担了供给组合、
/// Lucene 引擎、台账等一整套只读观测；把符号索引的注册表/驱动观测塞进去，会让两块的失败面互相污染，
/// 也会让「fullText 字段冻结」这条红线难以守住。控制器把两块**并列**组合。
/// </para>
/// <para>
/// <b>降级不崩</b>（与 S-A 同口径）：任何单点失败都被局部捕获并如实降级（计数类报 <c>null</c>，
/// 绝不拿 0 冒充「没有」），端点保持 200。
/// </para>
/// </summary>
public sealed class CodeIndexStatusProbe
{
    /// <summary>维护态缺席：驱动在跑，但这个 scope 没挂上去（欠一次 attach，不等于陈旧）。</summary>
    private const string ScopeNotAttachedReason = "scope-not-attached";

    /// <summary>维护态缺席：维护驱动根本没在跑（因此进程内不存在任何已挂 scope）。</summary>
    private const string DriverNotRunningReason = "maintenance-driver-not-running";

    private readonly ICodeIndexMaintenance _maintenance;
    private readonly ICodeIndexScopeRegistry _scopeRegistry;
    private readonly ICodeProjectRegistry _projectRegistry;
    private readonly PuddingDataPaths _dataPaths;
    private readonly ILogger<CodeIndexStatusProbe> _logger;

    /// <summary>Creates the probe.</summary>
    /// <param name="maintenance">维护驱动（只读：<c>GetScopeStatuses</c> / 计数属性 / <c>IsRunning</c>）。</param>
    /// <param name="scopeRegistry">索引注册表（只读：<c>ListScopesAsync</c>）—— 本块的**枚举真源**。</param>
    /// <param name="projectRegistry">项目记录（只读：<c>ListProjectsAsync</c>）—— 只用于取原始注册态。</param>
    /// <param name="dataPaths">数据根（workspace 目录枚举的唯一真源）。</param>
    /// <param name="logger">Logger.</param>
    public CodeIndexStatusProbe(
        ICodeIndexMaintenance maintenance,
        ICodeIndexScopeRegistry scopeRegistry,
        ICodeProjectRegistry projectRegistry,
        PuddingDataPaths dataPaths,
        ILogger<CodeIndexStatusProbe> logger)
    {
        _maintenance = maintenance;
        _scopeRegistry = scopeRegistry;
        _projectRegistry = projectRegistry;
        _dataPaths = dataPaths;
        _logger = logger;
    }

    /// <summary>构建一次 codeIndex 快照（全程只读；失败降级为「空列表 + 如实原因」，绝不 500）。</summary>
    /// <param name="ct">取消令牌。</param>
    public async Task<CodeIndexStatusDetailSnapshot> BuildAsync(CancellationToken ct = default)
    {
        try
        {
            return await ObserveAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 请求被取消是调用方的事实，不是「索引不可用」，不走降级。
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[CodeIndexStatus] The code index snapshot could not be built; reporting unknown counters.");

            return new CodeIndexStatusDetailSnapshot(
                WorkspaceIds: [],
                MaintenanceRunning: null,
                BatchesProcessed: null,
                ReconcileRequests: null,
                RemovalObservations: null,
                PendingReconcileScopeCount: null,
                Projects: [],
                Note: $"code-index-status-unavailable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<CodeIndexStatusDetailSnapshot> ObserveAsync(CancellationToken ct)
    {
        var workspaceIds = EnumerateWorkspaceIds();

        // 维护驱动持有的 scope（进程内状态）：既用于「未登记 scope ⇒ 陈旧」的**发现**，
        // 也用于给注册表条目补上维护态。
        var attachedByKey = new Dictionary<string, CodeIndexMaintenanceScopeStatus>(StringComparer.Ordinal);
        foreach (var status in _maintenance.GetScopeStatuses())
            attachedByKey[ProjectKey(status.WorkspaceId, status.ScopeId)] = status;

        var projects = new List<CodeIndexProjectStatusSnapshot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // ① 注册表条目：登记态的真源。
        foreach (var workspaceId in workspaceIds)
        {
            var scopes = await _scopeRegistry.ListScopesAsync(workspaceId, ct).ConfigureAwait(false);
            var records = await _projectRegistry.ListProjectsAsync(workspaceId, ct).ConfigureAwait(false);

            foreach (var scope in scopes)
            {
                var key = ProjectKey(workspaceId, scope.ScopeId);
                seen.Add(key);
                attachedByKey.TryGetValue(key, out var maintenance);

                var rootPathExists = DirectoryExists(scope.RootPath);

                projects.Add(new CodeIndexProjectStatusSnapshot(
                    WorkspaceId: workspaceId,
                    ProjectId: scope.ScopeId,
                    DisplayName: scope.DisplayName,
                    RootPath: scope.RootPath,
                    Registered: true,
                    RegistrationState: scope.State.ToString(),
                    RegistrationStatus: FindProjectStatus(records, scope.ScopeId),
                    RegistrationSource: scope.Source.ToString(),
                    Maintenance: maintenance,
                    MaintenanceReason: maintenance is null ? DescribeMissingMaintenance() : null,
                    RootPathExists: rootPathExists,
                    Stale: IsStale(registered: true, rootPathExists)));
            }
        }

        // ② 只在维护驱动里出现的 scope：**未在注册表登记** ⇒ 必须进列表且标陈旧（D1：不得报成健康）。
        foreach (var maintenance in attachedByKey.Values)
        {
            if (!seen.Add(ProjectKey(maintenance.WorkspaceId, maintenance.ScopeId)))
                continue;

            var rootPathExists = DirectoryExists(maintenance.RootPath);

            projects.Add(new CodeIndexProjectStatusSnapshot(
                WorkspaceId: maintenance.WorkspaceId,
                ProjectId: maintenance.ScopeId,
                DisplayName: null,
                RootPath: maintenance.RootPath,
                Registered: false,
                RegistrationState: null,
                RegistrationStatus: null,
                RegistrationSource: null,
                Maintenance: maintenance,
                MaintenanceReason: null,
                RootPathExists: rootPathExists,
                Stale: IsStale(registered: false, rootPathExists)));
        }

        // 稳定输出：同一份磁盘状态下，两次快照的顺序必须一致（否则前端 diff 会抖）。
        projects.Sort(static (left, right) =>
        {
            var byWorkspace = string.CompareOrdinal(left.WorkspaceId, right.WorkspaceId);
            return byWorkspace != 0 ? byWorkspace : string.CompareOrdinal(left.ProjectId, right.ProjectId);
        });

        return new CodeIndexStatusDetailSnapshot(
            WorkspaceIds: workspaceIds,
            MaintenanceRunning: _maintenance.IsRunning,
            BatchesProcessed: _maintenance.BatchesProcessed,
            ReconcileRequests: _maintenance.ReconcileRequests,
            RemovalObservations: _maintenance.RemovalObservations,
            PendingReconcileScopeCount: _maintenance.PendingReconcileScopeCount,
            Projects: projects,
            Note: null);
    }

    /// <summary>
    /// D1/D2 的**唯一定义**（两个调用点都走这里，所以「改错一处」就是改错全部）：
    /// 未登记 <b>或</b> 根路径不存在 ⇒ 陈旧。
    /// </summary>
    private static bool IsStale(bool registered, bool rootPathExists) =>
        !registered || !rootPathExists;

    /// <summary>
    /// 目录存在性判定：直接用 <see cref="RootPath"/> 的字面值，**不猜工作目录、不猜盘符**
    /// （相对路径按进程 CWD 解析；解析不到目录即为「不存在」—— fail-closed，宁可报陈旧）。
    /// 空白路径视为不存在（<see cref="Directory.Exists(string?)"/> 本身不抛异常）。
    /// </summary>
    private static bool DirectoryExists(string rootPath) =>
        !string.IsNullOrWhiteSpace(rootPath) && Directory.Exists(rootPath);

    /// <summary>取该行的**原始**生命周期状态（注册态里唯一能表达 <c>Registering</c> 的字面量）。</summary>
    private static string? FindProjectStatus(IReadOnlyList<CodeProjectRecord> records, string projectId)
    {
        foreach (var record in records)
        {
            if (string.Equals(record.ProjectId, projectId, StringComparison.Ordinal))
                return record.Status.ToString();
        }

        return null;
    }

    private string DescribeMissingMaintenance() =>
        _maintenance.IsRunning ? ScopeNotAttachedReason : DriverNotRunningReason;

    /// <summary>scope 键：workspace + scopeId（分隔符取不可打印字符，避免与 id 内容相撞）。</summary>
    private static string ProjectKey(string workspaceId, string scopeId) => $"{workspaceId}\u001F{scopeId}";

    /// <summary>
    /// workspace id 列表：<c>workspaces</c> 下的目录名（与 <c>CodeIndexMaintenanceHostedService</c> 同一约定）。
    /// 读不出来 ⇒ 空列表（本块随之降级为「无项目」，并保持 200）。
    /// </summary>
    private IReadOnlyList<string> EnumerateWorkspaceIds()
    {
        try
        {
            if (!Directory.Exists(_dataPaths.WorkspacesRoot))
                return [];

            return Directory.GetDirectories(_dataPaths.WorkspacesRoot)
                .Select(Path.GetFileName)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name!)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "[CodeIndexStatus] The workspace list could not be read from {Root}.",
                _dataPaths.WorkspacesRoot);
            return [];
        }
    }
}
