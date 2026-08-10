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
                     "IX_orchestration_node_runs_ready",
                     "IX_orchestration_events_run_sequence"
                 })
        {
            Assert.IsTrue(await ObjectExistsAsync(db, "index", index), $"Missing index {index}");
        }
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
}
