using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingAgent.Services;
using PuddingCode.Configuration;
using PuddingCodeIndex.Contracts;
using PuddingHost.Controllers;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// 切片 S-A2（2026-10-01）：<c>GET /api/admin/index/status</c> 上的第二块 <c>codeIndex</c>（符号 / 代码索引）。
/// <para>
/// <b>断言矩阵 A1~A7</b>：
/// A1 契约形状（根对象 3 个属性 + fullText 字段名冻结 + 不合并状态字段）；
/// A2 注册表 4 个项目全部出现且登记态正确（同时是 A3/A4 的**活对照**：登记且路径存在 ⇒ 不陈旧）；
/// A3 未登记 scope（D1）⇒ 出现在列表里且 <c>stale = true</c>；
/// A4 根路径不存在（D2）⇒ <c>rootPathExists = false</c> 且 <c>stale = true</c>；
/// A5 维护态 23 字段**原样透传**（同一引用 + 23 个 camelCase 字段名逐个点名）；
/// A6 D3：注册态与维护态是两个字段、两个真源，不合并；
/// A7 驱动计数 + 降级（读不出来时如实报 <c>null</c> 而不是 0）。
/// </para>
/// <para>
/// 全部数据来自内存替身（<see cref="Sa2ScopeRegistryStub"/> 等），**零数据库、零索引库写入**：
/// 本文件任何断言都不会触碰运行中的 <c>D:\Data\databases\code-index\code_index.db</c>。
/// </para>
/// </summary>
public sealed class SA2CodeIndexStatusTests
{
    // ── A1：契约形状 ────────────────────────────────────────────────────

    /// <summary>
    /// A1：根对象**只有**三个属性（<c>GeneratedAtUtc</c> / <c>FullText</c> / <c>CodeIndex</c>），
    /// fullText 的 14 个字段名**逐字未变**，且维护态里**没有**被合并进来的单一 <c>status</c> 字段。
    /// </summary>
    [Fact]
    public async Task A1_Root_Contract_Adds_CodeIndex_Without_Touching_FullText_Field_Names()
    {
        var rootProperties = typeof(IndexAdminStatusSnapshot)
            .GetProperties()
            .Select(static p => p.Name)
            .ToArray();

        Assert.Equal(new[] { "GeneratedAtUtc", "FullText", "CodeIndex" }, rootProperties);
        Assert.Equal(
            typeof(FullTextIndexStatusDetailSnapshot),
            typeof(IndexAdminStatusSnapshot).GetProperty("FullText")!.PropertyType);

        // 冻结契约：这 14 个名字就是前端已上线的字段名（改名 = 破前端）。
        Assert.Equal(
            new[]
            {
                "Configured", "Enabled", "IndexRoot", "IndexRootExists", "WorkspaceRoot",
                "MaxIndexBytes", "MinRebuildInterval", "AcceptedScopes", "RejectedReasons",
                "CompositionCreated", "Maintenance", "Scopes", "Jobs", "JobsReason",
            },
            typeof(FullTextIndexStatusDetailSnapshot).GetProperties().Select(static p => p.Name));

        var codeIndex = await BuildProbe(
                AbsentDataRoot(),
                new Sa2ScopeRegistryStub(),
                new Sa2ProjectRegistryStub(),
                new Sa2MaintenanceStub())
            .BuildAsync();

        using var document = JsonDocument.Parse(Serialize(codeIndex));
        var projects = document.RootElement.GetProperty("projects");
        Assert.Equal(JsonValueKind.Array, projects.ValueKind);
        Assert.Empty(projects.EnumerateArray());
    }

    // ── A2：注册表 4 个项目全部出现 ─────────────────────────────────────

    /// <summary>
    /// A2：<see cref="Sa2Samples.RegisteredProjects"/> 的 4 个项目一个不落，登记态/来源/展示名如实透出；
    /// 且它们登记在册 **且** 根路径存在 ⇒ <c>stale = false</c>（这就是 A3/A4 的活对照：
    /// 若 <c>stale</c> 恒为 true，本断言先红）。
    /// </summary>
    [Fact]
    public async Task A2_Every_Registered_Project_Is_Listed_And_Healthy_Ones_Are_Not_Stale()
    {
        using var fixture = new Sa2DataRootFixture();

        var scopes = Sa2Samples.RegisteredProjects
            .Select(static p => Sa2Samples.Scope(p.ProjectId, p.RootPath, displayName: p.DisplayName))
            .ToArray();

        var block = await BuildBlockAsync(
            fixture,
            new Sa2ScopeRegistryStub(scopes),
            new Sa2ProjectRegistryStub(
                Sa2Samples.RegisteredProjects
                    .Select(static p => new CodeProjectRecord(
                        Sa2Samples.WorkspaceId, p.ProjectId, p.RootPath, CodeProjectStatus.Active, p.DisplayName))
                    .ToArray()),
            new Sa2MaintenanceStub());

        // 契约：项目按 (workspaceId, projectId) 序数升序稳定排序（同磁盘状态下两次快照顺序一致）。
        Assert.Equal(
            Sa2Samples.RegisteredProjects
                .Select(static p => p.ProjectId)
                .OrderBy(static id => id, StringComparer.Ordinal),
            block.Projects.Select(static p => p.ProjectId));

        foreach (var (projectId, rootPath, displayName) in Sa2Samples.RegisteredProjects)
        {
            var entry = Assert.Single(block.Projects, p => p.ProjectId == projectId);
            Assert.Equal(Sa2Samples.WorkspaceId, entry.WorkspaceId);
            Assert.Equal(rootPath, entry.RootPath);
            Assert.Equal(displayName, entry.DisplayName);
            Assert.True(entry.Registered);
            Assert.Equal("Active", entry.RegistrationState);
            Assert.Equal("Active", entry.RegistrationStatus);
            Assert.Equal("Manual", entry.RegistrationSource);
            Assert.True(entry.RootPathExists);
            Assert.False(entry.Stale, $"{projectId} 已登记且根路径存在，不得报成陈旧");
        }
    }

    // ── A3：D1 —— 未登记 scope 必须标陈旧 ───────────────────────────────

    /// <summary>
    /// A3（D1）：只出现在维护驱动里、**不在注册表**的 scope 必须进列表且 <c>stale = true</c>。
    /// <para>
    /// 关键设计：该 scope 的根路径**是真实存在的目录**（<c>stale</c> 的唯一来源就只能是「未登记」），
    /// 否则这条断言会与 A4 混在一起、无法区分是哪条规则在起作用。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A3_D1_Unregistered_Scope_Is_Listed_And_Marked_Stale()
    {
        using var fixture = new Sa2DataRootFixture();

        // 已登记且在册的项目（活对照）。
        var registered = Sa2Samples.RegisteredProjects[0];
        var registry = new Sa2ScopeRegistryStub(
            Sa2Samples.Scope(registered.ProjectId, registered.RootPath, displayName: registered.DisplayName));

        // 维护驱动额外持有 D1 里那个已从注册表移除、却仍被查询面服务的 scope。
        const string orphanScopeId = "scope-6526fb344e33";
        var maintenance = new Sa2MaintenanceStub(
            Sa2Samples.MaintenanceStatus(registered.ProjectId, registered.RootPath),
            Sa2Samples.MaintenanceStatus(orphanScopeId, fixture.ExistingDirectory));

        var block = await BuildBlockAsync(
            fixture,
            registry,
            new Sa2ProjectRegistryStub(),
            maintenance);

        Assert.Equal(2, block.Projects.Count);

        var orphan = Assert.Single(block.Projects, p => p.ProjectId == orphanScopeId);
        Assert.False(orphan.Registered);
        Assert.Null(orphan.RegistrationState);
        Assert.Null(orphan.RegistrationStatus);
        Assert.True(orphan.RootPathExists, "该 scope 的根路径故意用真实存在的目录：陈旧只能来自「未登记」");
        Assert.True(orphan.Stale, "D1：未在注册表登记的 scope 一律标陈旧（不得报成健康）");
        Assert.NotNull(orphan.Maintenance);

        // 活对照：同一份 fixture 下，登记在册的那条不是陈旧的。
        var healthy = Assert.Single(block.Projects, p => p.ProjectId == registered.ProjectId);
        Assert.True(healthy.Registered);
        Assert.False(healthy.Stale);
    }

    // ── A4：D2 —— 根路径不存在必须标陈旧 ────────────────────────────────

    /// <summary>
    /// A4（D2）：登记在册但 <c>RootPath</c> **必然不存在** ⇒ <c>rootPathExists = false</c> 且
    /// <c>stale = true</c>；同一份快照里路径存在的那条仍是 <c>stale = false</c>（活对照）。
    /// </summary>
    [Fact]
    public async Task A4_D2_Missing_Root_Path_Is_Not_Stale()
    {
        using var fixture = new Sa2DataRootFixture();

        var missingPath = Path.Combine(
            fixture.Root,
            "definitely-missing-" + Guid.NewGuid().ToString("N"),
            "project");
        Assert.False(Directory.Exists(missingPath), "测试前提：该路径必须不存在");

        var existing = Sa2Samples.RegisteredProjects[0];

        var block = await BuildBlockAsync(
            fixture,
            new Sa2ScopeRegistryStub(
                Sa2Samples.Scope("missing-root-project", missingPath, displayName: "missing"),
                Sa2Samples.Scope(existing.ProjectId, existing.RootPath, displayName: existing.DisplayName)),
            new Sa2ProjectRegistryStub(),
            new Sa2MaintenanceStub());

        var missing = Assert.Single(block.Projects, p => p.ProjectId == "missing-root-project");
        Assert.True(missing.Registered, "它在注册表里 —— 陈旧只能来自根路径");
        Assert.False(missing.RootPathExists);
        // D2 红线：根路径不存在 ⇒ 陈旧（不得把死路径渲染成已索引）。用 Equal 形式，变异取红时能拿到 Expected/Received 原文。
        Assert.Equal(expected: true, actual: missing.Stale);

        var healthy = Assert.Single(block.Projects, p => p.ProjectId == existing.ProjectId);
        Assert.True(healthy.RootPathExists);
        Assert.False(healthy.Stale);
    }

    // ── A5：维护态 23 字段原样透传 ──────────────────────────────────────

    /// <summary>
    /// A5：<c>Maintenance</c> 是组件记录的**同一引用**（没有挑拣、没有重建），
    /// 序列化后恰好是那 23 个 camelCase 字段（逐个点名，少一个/多一个都红），且值逐项一致。
    /// </summary>
    [Fact]
    public async Task A5_Maintenance_Status_Is_Passed_Through_Verbatim_With_All_23_Fields()
    {
        using var fixture = new Sa2DataRootFixture();

        var project = Sa2Samples.RegisteredProjects[0];
        var status = Sa2Samples.MaintenanceStatus(project.ProjectId, project.RootPath);

        var block = await BuildBlockAsync(
            fixture,
            new Sa2ScopeRegistryStub(Sa2Samples.Scope(project.ProjectId, project.RootPath)),
            new Sa2ProjectRegistryStub(),
            new Sa2MaintenanceStub(status));

        var entry = Assert.Single(block.Projects);
        Assert.Same(status, entry.Maintenance);
        Assert.Null(entry.MaintenanceReason);

        using var document = JsonDocument.Parse(Serialize(block));
        var maintenance = document.RootElement
            .GetProperty("projects")[0]
            .GetProperty("maintenance");

        Assert.Equal(
            new[]
            {
                "workspaceId", "scopeId", "rootPath", "observedVersion", "desiredVersion",
                "committedVersion", "markedWhileInFlightCount", "indexPending", "indexInFlight",
                "needsReconcile", "reconcileReason", "reconcileRequestCount", "removalObservationCount",
                "lastRemovalPaths", "removedFileCount", "incrementallyIndexedFileCount",
                "scopeEscalationCount", "sweptFileCount", "calibrationRunCount",
                "rejectedCalibrationRunCount", "lastCalibrationAtUtc", "recentObservationCount",
                "watcherAttached",
            },
            maintenance.EnumerateObject().Select(static p => p.Name));

        Assert.True(maintenance.GetProperty("indexPending").GetBoolean());
        Assert.Equal(10, maintenance.GetProperty("committedVersion").GetInt64());
        Assert.Equal(13, maintenance.GetProperty("recentObservationCount").GetInt64());
        Assert.True(maintenance.GetProperty("watcherAttached").GetBoolean());

        // 维护态里**没有**被合并进来的单一「状态」字段（D3 的反例断言）。
        Assert.False(maintenance.TryGetProperty("status", out _));
    }

    // ── A6：D3 —— 注册态与维护态分别是两个字段 ───────────────────────────

    /// <summary>
    /// A6（D3）：同一条目上「注册态」与「维护态」分别呈现 ——
    /// 注册记录原始状态 <c>Registering</c>（真源：索引注册表 / SQLite 项目记录表）与
    /// 维护的 <c>indexPending</c>（真源：维护驱动进程内状态）**同时可见且互不覆盖**；
    /// 注册表投影另给一个 <c>registrationState</c>（<c>Registering</c> 被投影成 <c>Active</c>）。
    /// </summary>
    [Fact]
    public async Task A6_D3_Registration_State_And_Maintenance_State_Are_Separate_Fields()
    {
        using var fixture = new Sa2DataRootFixture();

        var project = Sa2Samples.RegisteredProjects[0];
        var status = Sa2Samples.MaintenanceStatus(project.ProjectId, project.RootPath, indexPending: true);

        var block = await BuildBlockAsync(
            fixture,
            new Sa2ScopeRegistryStub(Sa2Samples.Scope(project.ProjectId, project.RootPath)),
            new Sa2ProjectRegistryStub(
                new CodeProjectRecord(
                    Sa2Samples.WorkspaceId, project.ProjectId, project.RootPath, CodeProjectStatus.Registering)),
            new Sa2MaintenanceStub(status));

        var entry = Assert.Single(block.Projects);

        Assert.Equal("Active", entry.RegistrationState);      // 注册表投影（Registering ⇒ Active）
        Assert.Equal("Registering", entry.RegistrationStatus); // 项目记录原始真值
        Assert.Equal("Manual", entry.RegistrationSource);
        Assert.True(entry.Maintenance!.IndexPending);          // 维护态真值（D3 里被误当注册态的那个 Pending）
        Assert.Null(entry.MaintenanceReason);

        using var document = JsonDocument.Parse(Serialize(block));
        var project0 = document.RootElement.GetProperty("projects")[0];
        Assert.Equal("Registering", project0.GetProperty("registrationStatus").GetString());
        Assert.Equal("Active", project0.GetProperty("registrationState").GetString());
        Assert.Equal(JsonValueKind.Object, project0.GetProperty("maintenance").ValueKind);
    }

    /// <summary>
    /// A6b：登记在册但**未挂到维护驱动** ⇒ 维护态为 <c>null</c> 并给出如实原因；
    /// 驱动没运行时原因是另一个取值。两种情形都**不**导致 <c>stale</c>（欠运行 ≠ 陈旧）。
    /// </summary>
    [Fact]
    public async Task A6b_Missing_Maintenance_Reports_The_Real_Reason_Without_Touching_Staleness()
    {
        using var fixture = new Sa2DataRootFixture();
        var project = Sa2Samples.RegisteredProjects[0];

        var attachedDriver = new Sa2MaintenanceStub();
        var detached = await BuildBlockAsync(
            fixture,
            new Sa2ScopeRegistryStub(Sa2Samples.Scope(project.ProjectId, project.RootPath)),
            new Sa2ProjectRegistryStub(),
            attachedDriver);

        var detachedEntry = Assert.Single(detached.Projects);
        Assert.Null(detachedEntry.Maintenance);
        Assert.Equal("scope-not-attached", detachedEntry.MaintenanceReason);
        Assert.False(detachedEntry.Stale);

        var stopped = await BuildBlockAsync(
            fixture,
            new Sa2ScopeRegistryStub(Sa2Samples.Scope(project.ProjectId, project.RootPath)),
            new Sa2ProjectRegistryStub(),
            new Sa2MaintenanceStub { Running = false });

        var stoppedEntry = Assert.Single(stopped.Projects);
        Assert.Null(stoppedEntry.Maintenance);
        Assert.Equal("maintenance-driver-not-running", stoppedEntry.MaintenanceReason);
        Assert.False(stopped.MaintenanceRunning);
    }

    // ── A7：驱动计数 + 降级 ─────────────────────────────────────────────

    /// <summary>
    /// A7：驱动计数逐项透传；读不出来时**如实降级**（计数报 <c>null</c>，不是 0；不抛异常、不 500）。
    /// </summary>
    [Fact]
    public async Task A7_Driver_Counters_Are_Passed_Through_And_Failures_Degrade_To_Null()
    {
        using var fixture = new Sa2DataRootFixture();

        var block = await BuildBlockAsync(
            fixture,
            new Sa2ScopeRegistryStub(),
            new Sa2ProjectRegistryStub(),
            new Sa2MaintenanceStub());

        Assert.True(block.MaintenanceRunning);
        Assert.Equal(Sa2MaintenanceStub.BatchesProcessedValue, block.BatchesProcessed!.Value);
        Assert.Equal(Sa2MaintenanceStub.ReconcileRequestsValue, block.ReconcileRequests!.Value);
        Assert.Equal(Sa2MaintenanceStub.RemovalObservationsValue, block.RemovalObservations!.Value);
        Assert.Equal(Sa2MaintenanceStub.PendingReconcileScopeCountValue, block.PendingReconcileScopeCount!.Value);
        Assert.Null(block.Note);
        Assert.Equal(new[] { Sa2Samples.WorkspaceId }, block.WorkspaceIds);

        // 降级：注册表读取失败 ⇒ 空列表 + 如实原因，计数类报 null（未知 ≠ 0）。
        var degraded = await new CodeIndexStatusProbe(
                new Sa2MaintenanceStub(),
                new ThrowingScopeRegistry(),
                new Sa2ProjectRegistryStub(),
                fixture.Paths,
                NullLogger<CodeIndexStatusProbe>.Instance)
            .BuildAsync();

        Assert.Empty(degraded.Projects);
        Assert.Empty(degraded.WorkspaceIds);
        Assert.Null(degraded.MaintenanceRunning);
        Assert.Null(degraded.BatchesProcessed);
        Assert.NotNull(degraded.Note);
        Assert.Contains("code-index-status-unavailable", degraded.Note!, StringComparison.Ordinal);
    }

    // ── 局部夹具 ────────────────────────────────────────────────────────

    private static Task<CodeIndexStatusDetailSnapshot> BuildBlockAsync(
        Sa2DataRootFixture fixture,
        ICodeIndexScopeRegistry registry,
        ICodeProjectRegistry projectRegistry,
        ICodeIndexMaintenance maintenance) =>
        BuildProbe(fixture.Paths, registry, projectRegistry, maintenance).BuildAsync();

    /// <summary>一个**不存在**的数据根（workspace 枚举为空 ⇒ 探针不读任何真实存储）。</summary>
    private static PuddingDataPaths AbsentDataRoot() =>
        PuddingDataPaths.FromRoot(Path.Combine(Path.GetTempPath(), "pudding-sa2-absent-data-root"));

    private static CodeIndexStatusProbe BuildProbe(
        PuddingDataPaths paths,
        ICodeIndexScopeRegistry registry,
        ICodeProjectRegistry projectRegistry,
        ICodeIndexMaintenance maintenance) =>
        new(maintenance, registry, projectRegistry, paths, NullLogger<CodeIndexStatusProbe>.Instance);

    /// <summary>与生产默认序列化策略同口径（<c>JsonSerializerDefaults.Web</c> ⇒ camelCase）。</summary>
    private static string Serialize(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>
    /// 临时数据根（只在系统临时目录；自带「必须在临时目录下」的断言）——
    /// workspace 枚举的真源是 <c>workspaces</c> 下的目录名，因此这里造一个 <c>workspaces/default</c>。
    /// </summary>
    private sealed class Sa2DataRootFixture : IDisposable
    {
        internal Sa2DataRootFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-sa2-host", Guid.NewGuid().ToString("N"));
            if (!Root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"S-A2 测试只允许使用系统临时目录，实际为 {Root}");

            Paths = PuddingDataPaths.FromRoot(Root);
            ExistingDirectory = Path.Combine(Root, "corpus", "exists");
            Directory.CreateDirectory(ExistingDirectory);
            Directory.CreateDirectory(Paths.WorkspaceRoot(Sa2Samples.WorkspaceId));
        }

        internal string Root { get; }

        internal PuddingDataPaths Paths { get; }

        /// <summary>一个**真实存在**的目录（用于把「未登记」与「路径不存在」两条规则分开验证）。</summary>
        internal string ExistingDirectory { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不应把测试判红。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>注册表替身：任何调用都抛（用于验证降级路径）。</summary>
    private sealed class ThrowingScopeRegistry : ICodeIndexScopeRegistry
    {
        public Task<IReadOnlyList<CodeIndexScope>> ListScopesAsync(
            string workspaceId,
            CancellationToken cancellationToken = default) =>
            throw new IOException("registry unavailable (test double)");

        public Task<CodeIndexScope> EnsureScopeAsync(
            string workspaceId, string rootPath, ScopeSource source, string? displayName = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CodeIndexResult> ForgetScopeAsync(
            string workspaceId, string scopeId, bool removeIndexData = true,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CodeIndexScope?> GetScopeAsync(
            string workspaceId, string scopeId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CodeIndexScope?> FindCoveringScopeAsync(
            string workspaceId, string directoryPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
