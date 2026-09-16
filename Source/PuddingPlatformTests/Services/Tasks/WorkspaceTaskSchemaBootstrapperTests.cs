using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatformTests.Services.Tasks;

/// <summary>
/// Stage 1（母/子层级，D1）的<b>旧库补列</b>测试。
/// <para>
/// 覆盖缺口：<see cref="WorkspaceTaskSchemaBootstrapper"/> 在本类加入前没有配对测试。
/// 经全量枚举（原首次核对时列表被截断在 40 条，曾据此错认为“唯一缺失”，现更正）：
/// 仓库里共 23 个 *SchemaBootstrapper，本类加入前仅 11 个配有 *SchemaBootstrapperTests，故该约定是
/// “多数但非普遍”，<b>不是</b>“唯一例外”。
/// 有配对测试：AgentOrchestration / AppUser / ConnectorStreamProjection / ConversationCommand / ExecutionRun /
/// ExternalAccessToken / MessageFabric / SessionSteering / SubAgentRun / TaskPlanning / TokenUsage（+ 本类）。
/// 同样缺失：ChatMessage / ExternalTaskApi / Goal / ProviderFileRef / TaskDispatch / TaskSchedulerDecision /
/// TaskSchedulerIntent / TaskSchedulerIntentOutcome / TaskSchedulerScanRun / TaskScheduling / Todo。
/// 既有测试 <see cref="WorkspaceTaskHierarchyPersistenceTests"/> 覆盖的是<b>列位置序</b>风险，
/// 且用 EF <c>EnsureCreated</c> 建<b>全新库</b>——不会走到旧库 <c>ALTER TABLE ADD COLUMN</c> 分支。
/// 而生产库是既有的：<c>CREATE TABLE IF NOT EXISTS</c> 对已存在表是 no-op，补列只能靠
/// <c>EnsureColumnAsync</c>，故该分支必须单独锁定。
/// </para>
/// <para>
/// 本类覆盖三条事实：① 旧表（无 parent_task_id）经 bootstrap 后该列被真实补上；
/// ② 遗留行不被破坏、不被打乱、且 parent_task_id 保持 NULL（D1：不做任何回填）；
/// ③ 重复调用幂等（不抛错、不破坏数据）。
/// </para>
/// </summary>
[TestClass]
public sealed class WorkspaceTaskSchemaBootstrapperTests
{
    private const string LegacyTaskId = "legacy-task-1";
    private const string WorkspaceId = "ws-legacy";
    private const long LegacySortOrder = 9_090_909_091L;
    private const int LegacyVersion = 5;
    private const string LegacyCreatedAt = "2026-01-02T03:04:05.0000000+00:00";

    private string _testRoot = null!;
    private string _databasePath = null!;
    private PlatformDbContextFactory _dbFactory = null!;
    private SqliteWorkspaceTaskStore _store = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "workspace-task-bootstrap-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _databasePath = Path.Combine(_testRoot, "platform.db");
        _dbFactory = new PlatformDbContextFactory(BuildOptions(_databasePath));
        _store = new SqliteWorkspaceTaskStore(_dbFactory);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await CreateLegacySchemaAsync(db);
        await InsertLegacyRowAsync(db);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private static DbContextOptions<PlatformDbContext> BuildOptions(string databasePath)
        => new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={databasePath};Default Timeout=10")
            .Options;

    /// <summary>
    /// 建「本特性改动之前」的 workspace_tasks：35 列，<b>不含</b> parent_task_id。
    /// 列定义逐字对齐 <see cref="WorkspaceTaskSchemaBootstrapper"/> 的 CREATE TABLE，仅去掉该列。
    /// </summary>
    private static async Task CreateLegacySchemaAsync(PlatformDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS workspace_tasks (
                task_id              TEXT    NOT NULL,
                workspace_id         TEXT    NOT NULL,
                title                TEXT    NOT NULL,
                description          TEXT,
                acceptance_criteria  TEXT,
                status               INTEGER NOT NULL,
                priority             INTEGER NOT NULL,
                execution_window     INTEGER NOT NULL,
                preferred_agent_id   TEXT,
                task_type            TEXT    NOT NULL DEFAULT 'general',
                required_capabilities_json TEXT NOT NULL DEFAULT '[]',
                required_provider_id TEXT,
                required_model_id    TEXT,
                allow_agent_fallback INTEGER NOT NULL DEFAULT 0,
                auto_dispatch_enabled INTEGER NOT NULL DEFAULT 0,
                active_assignment_id TEXT,
                not_before_utc       TEXT,
                due_at_utc           TEXT,
                next_eligible_at_utc TEXT,
                sort_order           INTEGER NOT NULL,
                progress_percent     INTEGER,
                progress_summary     TEXT,
                blocker_kind         TEXT,
                blocker_reason       TEXT,
                failure_code         TEXT,
                failure_reason       TEXT,
                origin               INTEGER,
                version              INTEGER NOT NULL DEFAULT 1,
                created_by           TEXT,
                updated_by           TEXT,
                created_at_utc       TEXT    NOT NULL,
                updated_at_utc       TEXT    NOT NULL,
                completed_at_utc     TEXT,
                failed_at_utc        TEXT,
                archived_at_utc      TEXT,
                PRIMARY KEY (task_id)
            );
            """);
    }

    private static async Task InsertLegacyRowAsync(PlatformDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            $"""
            INSERT INTO workspace_tasks
              (task_id, workspace_id, title, status, priority, execution_window, sort_order, version,
               created_at_utc, updated_at_utc)
            VALUES
              ('{LegacyTaskId}', '{WorkspaceId}', 'legacy-title', 0, 0, 0, {LegacySortOrder}, {LegacyVersion},
               '{LegacyCreatedAt}', '{LegacyCreatedAt}');
            """);
    }

    private static async Task<bool> ColumnExistsAsync(PlatformDbContext db, string columnName)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(workspace_tasks)";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [TestMethod]
    public async Task EnsureCreated_OnLegacyTableWithoutParentTaskId_AddsColumn()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // 前置事实：遗留表确实没有该列（否则本测试什么都没验证）。
        Assert.IsFalse(
            await ColumnExistsAsync(db, "parent_task_id"),
            "前置条件失败：遗留表不应已含 parent_task_id。");

        await WorkspaceTaskSchemaBootstrapper.EnsureCreatedAsync(db);

        Assert.IsTrue(
            await ColumnExistsAsync(db, "parent_task_id"),
            "旧库经 bootstrap 后必须补上 parent_task_id 列（CREATE TABLE IF NOT EXISTS 对已存在表是 no-op，只能靠 EnsureColumnAsync）。");
    }

    [TestMethod]
    public async Task EnsureCreated_OnLegacyRow_KeepsRowReadableAndLeavesParentNull()
    {
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            await WorkspaceTaskSchemaBootstrapper.EnsureCreatedAsync(db);
        }

        var task = await _store.GetTaskAsync(WorkspaceId, LegacyTaskId, CancellationToken.None);

        Assert.IsNotNull(task, "遗留行必须仍可读（bootstrap 不得破坏既有数据）。");
        Assert.AreEqual("legacy-title", task!.Title);
        // 哨兵：若补列顺序写错导致列错位，这两个字段会最先变红。
        Assert.AreEqual(LegacySortOrder, task.SortOrder);
        Assert.AreEqual(LegacyVersion, task.Version);
        // D1：不做任何回填，既有行恒为 NULL。
        Assert.IsNull(task.ParentTaskId, "既有行不得被回填 parent_task_id。");
    }

    [TestMethod]
    public async Task EnsureCreated_CalledTwice_IsIdempotent()
    {
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            await WorkspaceTaskSchemaBootstrapper.EnsureCreatedAsync(db);
        }

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            await WorkspaceTaskSchemaBootstrapper.EnsureCreatedAsync(db);

            // 幂等：重复 bootstrap 后列仍存在，且索引可重复创建（IX_..._parent 在补列之后）。
            Assert.IsTrue(await ColumnExistsAsync(db, "parent_task_id"));
        }

        var task = await _store.GetTaskAsync(WorkspaceId, LegacyTaskId, CancellationToken.None);
        Assert.IsNotNull(task);
        Assert.AreEqual(LegacySortOrder, task!.SortOrder);
        Assert.IsNull(task.ParentTaskId);
    }
}
