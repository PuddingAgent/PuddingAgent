using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Eventing;
using PuddingPlatform.Services.ExecutionBudgets;
using PuddingPlatform.Services.Plugins;
using PuddingPlatform.Services.Reviews;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-01/03/07A S5 schema bootstrap composition: all new bootstrappers run sequentially against one SQLite database,
/// are idempotent on a second pass, and create the expected tables/indexes without colliding.
/// </summary>
[TestClass]
public sealed class SchemaBootstrappersCompositionTests
{
    [TestMethod]
    public async Task AllNewBootstrappers_AreIdempotentAndCreateExpectedTables()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        for (var pass = 0; pass < 2; pass++)
        {
            await ExecutionBudgetSchemaBootstrapper.EnsureCreatedAsync(db, null);
            await ReviewJobSchemaBootstrapper.EnsureCreatedAsync(db, null);
            await PluginJobSchemaBootstrapper.EnsureCreatedAsync(db, null);
            await EventSubscriptionSchemaBootstrapper.EnsureCreatedAsync(db, null);
        }

        foreach (var table in new[]
                 {
                     "execution_budget_accounts", "execution_budget_reservations", "review_jobs",
                     "review_job_operations", "review_events", "plugin_jobs", "plugin_job_operations",
                     "event_subscriptions", "event_deliveries",
                 })
        {
            var found = await db.Database.SqlQueryRaw<string>(
                "SELECT name AS Value FROM sqlite_master WHERE type='table' AND name = '" + table + "'").SingleAsync();
            Assert.AreEqual(table, found, table);
        }

        var indexes = await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type='index' AND name LIKE 'UX_%'").ToListAsync();
        CollectionAssert.Contains(indexes, "UX_execution_budget_reservations_attempt");
        CollectionAssert.Contains(indexes, "UX_review_jobs_review");
        CollectionAssert.Contains(indexes, "UX_plugin_jobs_operation");
        CollectionAssert.Contains(indexes, "UX_event_deliveries_sub_event");
    }
}
