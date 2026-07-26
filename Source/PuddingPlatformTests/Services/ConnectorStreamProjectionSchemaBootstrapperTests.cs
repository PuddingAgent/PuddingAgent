using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class ConnectorStreamProjectionSchemaBootstrapperTests
{
    [TestMethod]
    public async Task EnsureCreatedAsync_AddsProjectionTableToExistingDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE legacy_marker (id INTEGER PRIMARY KEY AUTOINCREMENT);");

        await ConnectorStreamProjectionSchemaBootstrapper.EnsureCreatedAsync(db);
        await ConnectorStreamProjectionSchemaBootstrapper.EnsureCreatedAsync(db);

        Assert.IsTrue(await TableExistsAsync(db, "connector_stream_projections"));
        Assert.IsTrue(await IndexExistsAsync(db, "idx_connector_stream_projection_id"));
        Assert.IsTrue(await IndexExistsAsync(db, "idx_connector_stream_command_connector"));
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_AcceptsFreshPlatformDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        await ConnectorStreamProjectionSchemaBootstrapper.EnsureCreatedAsync(db);

        Assert.IsTrue(await TableExistsAsync(db, "connector_stream_projections"));
    }

    private static async Task<bool> TableExistsAsync(
        DbContext db,
        string tableName)
        => await SqliteObjectExistsAsync(db, "table", tableName);

    private static async Task<bool> IndexExistsAsync(
        DbContext db,
        string indexName)
        => await SqliteObjectExistsAsync(db, "index", indexName);

    private static async Task<bool> SqliteObjectExistsAsync(
        DbContext db,
        string objectType,
        string objectName)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = $type AND name = $name;";
        var typeParameter = command.CreateParameter();
        typeParameter.ParameterName = "$type";
        typeParameter.Value = objectType;
        command.Parameters.Add(typeParameter);
        var nameParameter = command.CreateParameter();
        nameParameter.ParameterName = "$name";
        nameParameter.Value = objectName;
        command.Parameters.Add(nameParameter);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }
}
