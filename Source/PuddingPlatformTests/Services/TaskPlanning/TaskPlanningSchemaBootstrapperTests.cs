using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services.TaskPlanning;

namespace PuddingPlatformTests.Services.TaskPlanning;

[TestClass]
public sealed class TaskPlanningSchemaBootstrapperTests
{
    [TestMethod]
    public async Task EnsureCreatedAsync_Creates_TaskPlanningTables_ForExistingSqliteDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TABLE IF NOT EXISTS legacy_marker (id INTEGER PRIMARY KEY AUTOINCREMENT);");

            await TaskPlanningSchemaBootstrapper.EnsureCreatedAsync(db);
        }

        await using (var db = new PlatformDbContext(options))
        {
            Assert.IsTrue(await TableExistsAsync(db, "task_plan_runs"));
            Assert.IsTrue(await TableExistsAsync(db, "task_nodes"));
            Assert.IsTrue(await IndexExistsAsync(db, "IX_task_plan_runs_plan_id"));
            Assert.IsTrue(await IndexExistsAsync(db, "IX_task_plan_runs_workspace_id_status_updated_at"));
            Assert.IsTrue(await IndexExistsAsync(db, "IX_task_nodes_task_node_id"));
            Assert.IsTrue(await IndexExistsAsync(db, "IX_task_nodes_plan_id_parent_task_node_id_status"));
            Assert.IsTrue(await IndexExistsAsync(db, "IX_task_nodes_plan_id_depth_status"));

            var taskPlanRunColumns = await GetColumnsAsync(db, "task_plan_runs");
            AssertColumn(taskPlanRunColumns, "task_plan_runs", "plan_id", "TEXT", checkNotNull: true);
            AssertColumn(taskPlanRunColumns, "task_plan_runs", "workspace_id", "TEXT", checkNotNull: true);
            AssertColumn(taskPlanRunColumns, "task_plan_runs", "status", "TEXT", checkNotNull: true, expectedDefault: "Draft");
            AssertColumn(taskPlanRunColumns, "task_plan_runs", "max_delegation_depth", "INTEGER", checkNotNull: true, expectedDefault: "2");
            AssertColumn(taskPlanRunColumns, "task_plan_runs", "updated_at", "INTEGER", checkNotNull: true);
            AssertColumn(taskPlanRunColumns, "task_plan_runs", "result_summary", "TEXT", checkNotNull: false);
            AssertColumn(taskPlanRunColumns, "task_plan_runs", "error_message", "TEXT", checkNotNull: false);

            var taskNodeColumns = await GetColumnsAsync(db, "task_nodes");
            AssertColumn(taskNodeColumns, "task_nodes", "task_node_id", "TEXT", checkNotNull: true);
            AssertColumn(taskNodeColumns, "task_nodes", "plan_id", "TEXT", checkNotNull: true);
            AssertColumn(taskNodeColumns, "task_nodes", "status", "TEXT", checkNotNull: true, expectedDefault: "Draft");
            AssertColumn(taskNodeColumns, "task_nodes", "depth", "INTEGER", checkNotNull: true);
            AssertColumn(taskNodeColumns, "task_nodes", "allow_sub_delegation", "INTEGER", checkNotNull: true, expectedDefault: "1");
            AssertColumn(taskNodeColumns, "task_nodes", "allow_agent_creation", "INTEGER", checkNotNull: true, expectedDefault: "1");
            AssertColumn(taskNodeColumns, "task_nodes", "result_summary", "TEXT", checkNotNull: false);
            AssertColumn(taskNodeColumns, "task_nodes", "error_message", "TEXT", checkNotNull: false);
        }
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_CanUpgradeAndWorkWithStore_CreatePlanAndNode()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new PlatformDbContext(options);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS legacy_task_marker (id INTEGER PRIMARY KEY AUTOINCREMENT);");

        await TaskPlanningSchemaBootstrapper.EnsureCreatedAsync(db);

        var store = new PuddingPlatform.Services.TaskPlanning.TaskPlanStore(db);
        var plan = await store.CreatePlanAsync(new PuddingCode.Models.TaskPlanCreateRequest
        {
            WorkspaceId = "default",
            RootSessionId = "session-root",
            LeaderAgentId = "leader",
        });

        Assert.IsNotNull(plan.PlanId);
        Assert.AreEqual(1, await db.TaskPlanRuns.CountAsync());
        Assert.AreEqual(1, await db.TaskNodes.CountAsync());
    }

    private static async Task<bool> TableExistsAsync(DbContext db, string tableName)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result) > 0;
    }

    private static async Task<bool> IndexExistsAsync(DbContext db, string indexName)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = indexName;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result) > 0;
    }

    private static async Task<Dictionary<string, TableColumnInfo>> GetColumnsAsync(DbContext db, string tableName)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        await using var reader = await command.ExecuteReaderAsync();
        var columns = new Dictionary<string, TableColumnInfo>(StringComparer.OrdinalIgnoreCase);

        while (await reader.ReadAsync())
        {
            var name = Convert.ToString(reader["name"]) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            columns[name] = new TableColumnInfo
            {
                Name = name,
                Type = Convert.ToString(reader["type"])?.Trim() ?? string.Empty,
                NotNull = Convert.ToInt32(reader["notnull"]) == 1,
                DefaultValue = reader["dflt_value"] == DBNull.Value ? null : Convert.ToString(reader["dflt_value"]),
                IsPrimaryKey = Convert.ToInt32(reader["pk"]) == 1,
            };
        }

        return columns;
    }

    private static void AssertColumn(
        Dictionary<string, TableColumnInfo> columns,
        string tableName,
        string columnName,
        string expectedType,
        string? expectedDefault = null,
        bool checkNotNull = false)
    {
        Assert.IsTrue(columns.ContainsKey(columnName), $"[{tableName}] missing required column: {columnName}");

        var column = columns[columnName];
        Assert.AreEqual(expectedType, column.Type?.ToUpperInvariant(), $"[{tableName}] {columnName} type mismatch");

        if (checkNotNull)
        {
            Assert.IsTrue(column.NotNull, $"[{tableName}] {columnName} expected to be NOT NULL");
        }

        if (expectedDefault is not null)
        {
            var normalizedDefault = NormalizeDefault(column.DefaultValue);
            var normalizedExpected = NormalizeDefault(expectedDefault);
            Assert.AreEqual(
                normalizedExpected,
                normalizedDefault,
                $"[{tableName}] {columnName} default mismatch: expected {normalizedExpected}, actual {normalizedDefault}");
        }
    }

    private static string? NormalizeDefault(string? value)
    {
        if (value is null)
            return null;

        return value.Trim().Trim('\'', '"');
    }

    private sealed record TableColumnInfo
    {
        public required string Name { get; init; }
        public string? Type { get; init; }
        public bool NotNull { get; init; }
        public string? DefaultValue { get; init; }
        public bool IsPrimaryKey { get; init; }
    }
}
