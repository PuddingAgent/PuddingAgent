using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatformTests.Services.Tasks;

/// <summary>
/// <see cref="WorkspaceTaskSchemaBootstrapper"/> 的「全新库」配对测试。
/// <para>
/// 2026-09-19 压缩决策：旧库一次性 ALTER 补列（task_type / required_capabilities_json /
/// required_provider_id / required_model_id / allow_agent_fallback / auto_dispatch_enabled /
/// sort_order / parent_task_id 共 8 列）已删除——产品未发布不存在需升级的旧库，
/// 8 列全部移入 CREATE TABLE DDL。
/// </para>
/// <para>
/// 本类锁定新库路径三条事实：① 结构化路由列的 DEFAULT 语义生效（插入省略这些列时得到
/// 'general' / '[]' / 0 / 0）；② parent_task_id 可写读往返；③ 重复调用幂等（引用
/// sort_order / parent_task_id 的索引可重复创建，不依赖任何补列步骤）。
/// </para>
/// </summary>
[TestClass]
public sealed class WorkspaceTaskSchemaBootstrapperTests
{
    private const string WorkspaceId = "ws-fresh";
    private const string CreatedAt = "2026-09-19T00:00:00.0000000+00:00";

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
        await WorkspaceTaskSchemaBootstrapper.EnsureCreatedAsync(db);
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

    [TestMethod]
    public async Task EnsureCreated_OnFreshDatabase_StructuredRoutingColumnsHaveDefaults()
    {
        var taskId = "fresh-defaults-1";
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            // 省略全部结构化路由列：task_type / required_capabilities_json /
            // allow_agent_fallback / auto_dispatch_enabled / parent_task_id 由 DDL 默认值接管。
            await db.Database.ExecuteSqlRawAsync(
                $"""
                INSERT INTO workspace_tasks
                  (task_id, workspace_id, title, status, priority, execution_window, sort_order, version,
                   created_at_utc, updated_at_utc)
                VALUES
                  ('{taskId}', '{WorkspaceId}', 'fresh-title', 0, 0, 0, 1, 1,
                   '{CreatedAt}', '{CreatedAt}');
                """);
        }

        var task = await _store.GetTaskAsync(WorkspaceId, taskId, CancellationToken.None);

        Assert.IsNotNull(task, "省略结构化路由列的行必须可读。");
        Assert.AreEqual(1, task.SortOrder);
        Assert.IsNull(task.ParentTaskId, "未指定时 parent_task_id 必须为 NULL。");

        // DEFAULT 语义逐列核对（压缩后这些 DEFAULT 必须保留在 CREATE TABLE 中）。
        await using var verify = await _dbFactory.CreateDbContextAsync();
        await using var cmd = verify.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = """
            SELECT task_type, required_capabilities_json, allow_agent_fallback, auto_dispatch_enabled
            FROM workspace_tasks WHERE task_id = $task_id
            """;
        var p = cmd.CreateParameter();
        p.ParameterName = "$task_id";
        p.Value = taskId;
        cmd.Parameters.Add(p);
        await verify.Database.OpenConnectionAsync();
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync(), "行必须存在。");
        Assert.AreEqual("general", reader.GetString(0), "task_type 缺省必须是 'general'。");
        Assert.AreEqual("[]", reader.GetString(1), "required_capabilities_json 缺省必须是 '[]'。");
        Assert.AreEqual(0L, reader.GetInt64(2), "allow_agent_fallback 缺省必须是 0。");
        Assert.AreEqual(0L, reader.GetInt64(3), "auto_dispatch_enabled 缺省必须是 0。");
    }

    [TestMethod]
    public async Task Store_PersistsParentTaskIdRoundTrip()
    {
        var taskId = "fresh-child-1";
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync(
                $"""
                INSERT INTO workspace_tasks
                  (task_id, workspace_id, title, status, priority, execution_window, sort_order, version,
                   parent_task_id, created_at_utc, updated_at_utc)
                VALUES
                  ('{taskId}', '{WorkspaceId}', 'child-title', 0, 0, 0, 2, 1,
                   'parent-task-0', '{CreatedAt}', '{CreatedAt}');
                """);
        }

        var task = await _store.GetTaskAsync(WorkspaceId, taskId, CancellationToken.None);

        Assert.IsNotNull(task, "含 parent_task_id 的行必须可读。");
        Assert.AreEqual("parent-task-0", task.ParentTaskId, "parent_task_id 必须可往返写读。");
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

            // 幂等：重复 bootstrap 不抛错，列与两个依赖列索引均存在。
            Assert.IsTrue(await ColumnExistsAsync(db, "parent_task_id"));
            Assert.IsTrue(await ColumnExistsAsync(db, "sort_order"));
            Assert.IsTrue(await IndexExistsAsync(db, "IX_workspace_tasks_workspace_sort"));
            Assert.IsTrue(await IndexExistsAsync(db, "IX_workspace_tasks_workspace_parent"));
        }
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

    private static async Task<bool> IndexExistsAsync(PlatformDbContext db, string indexName)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $name LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = indexName;
        command.Parameters.Add(parameter);
        var value = await command.ExecuteScalarAsync();
        return value is not null && value is not DBNull;
    }
}
