using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Reviews;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class ReviewJobSchemaUpgradeTests
{
    [TestMethod]
    public async Task ExistingDatabaseWithoutReviewTables_PreservesRowsAndRoundTripsActorOnRestart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE existing_data (id INTEGER PRIMARY KEY)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO existing_data VALUES (42)");
            Assert.IsFalse(await db.Database.EnsureCreatedAsync());
            await ReviewJobSchemaBootstrapper.EnsureCreatedAsync(db, null);
            db.ReviewJobs.Add(new ReviewJobEntity { JobId = "job", ReviewId = "review", ActorId = "actor" });
            await db.SaveChangesAsync();
        }

        await using var reopened = new PlatformDbContext(options);
        await ReviewJobSchemaBootstrapper.EnsureCreatedAsync(reopened, null);
        Assert.AreEqual("actor", (await reopened.ReviewJobs.AsNoTracking().SingleAsync()).ActorId);
        Assert.AreEqual(42, await reopened.Database.SqlQueryRaw<int>("SELECT id AS Value FROM existing_data").SingleAsync());
    }
}
