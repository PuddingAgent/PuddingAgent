using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Orchestration;

namespace PuddingPlatformTests.Services.Orchestration;

[TestClass]
public sealed class AgentOrchestrationSchemaBootstrapperTests
{
    [TestMethod]
    public async Task EnsureCreatedAsync_CreatesDurableGraphRunNodeAndEventTables()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new PlatformDbContext(options);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS legacy_marker (id INTEGER PRIMARY KEY AUTOINCREMENT);");

        await AgentOrchestrationSchemaBootstrapper.EnsureCreatedAsync(db);

        foreach (var table in new[]
                 {
                     "orchestration_graphs",
                     "orchestration_graph_revisions",
                     "orchestration_runs",
                     "orchestration_run_inputs",
                     "orchestration_node_runs",
                     "orchestration_run_events"
                 })
        {
            Assert.IsTrue(await ObjectExistsAsync(db, "table", table), $"Missing table {table}");
        }

        foreach (var index in new[]
                 {
                     "IX_orchestration_revisions_graph_revision",
                     "IX_orchestration_runs_workspace_status_updated",
                     "IX_orchestration_run_inputs_run",
                     "IX_orchestration_node_runs_ready",
                     "IX_orchestration_events_run_sequence"
                 })
        {
            Assert.IsTrue(await ObjectExistsAsync(db, "index", index), $"Missing index {index}");
        }

        Assert.IsTrue(await ColumnExistsAsync(db, "orchestration_node_runs", "outputs_json"));
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_AddsPortOutputsColumnToExistingNodeRunTable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE orchestration_node_runs (
                run_id TEXT NOT NULL, node_id TEXT NOT NULL, node_kind TEXT NOT NULL,
                status TEXT NOT NULL, attempt INTEGER NOT NULL, max_attempts INTEGER NOT NULL,
                claim_id TEXT, lease_owner TEXT, lease_until INTEGER, fencing_token INTEGER NOT NULL,
                execution_run_id TEXT, sub_session_id TEXT, output_summary TEXT,
                artifact_reference TEXT, error_message TEXT, started_at INTEGER,
                completed_at INTEGER, updated_at INTEGER NOT NULL,
                PRIMARY KEY(run_id, node_id)
            );
            """);

        await AgentOrchestrationSchemaBootstrapper.EnsureCreatedAsync(db);

        Assert.IsTrue(await ColumnExistsAsync(db, "orchestration_node_runs", "outputs_json"));
    }

    private static async Task<bool> ObjectExistsAsync(DbContext db, string type, string name)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = $type AND name = $name";
        var typeParameter = command.CreateParameter();
        typeParameter.ParameterName = "$type";
        typeParameter.Value = type;
        command.Parameters.Add(typeParameter);
        var nameParameter = command.CreateParameter();
        nameParameter.ParameterName = "$name";
        nameParameter.Value = name;
        command.Parameters.Add(nameParameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<bool> ColumnExistsAsync(DbContext db, string table, string column)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
