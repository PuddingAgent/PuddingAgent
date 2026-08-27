using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingWebApiTests;

[TestClass]
public sealed class SessionSteeringSchemaBootstrapperTests
{
    [TestMethod]
    public async Task EnsureCreatedAsync_AddsTargetTurn_AndExpiresLegacyPendingRows()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE session_steering_messages (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    steering_id TEXT NOT NULL,
                    workspace_id TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    agent_id TEXT NULL,
                    source_queue_item_id TEXT NULL,
                    message_text TEXT NOT NULL,
                    priority INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    created_by TEXT NULL,
                    created_at_utc TEXT NOT NULL,
                    consumed_at_utc TEXT NULL,
                    consumed_round INTEGER NULL,
                    expired_at_utc TEXT NULL
                );
                INSERT INTO session_steering_messages (
                    steering_id, workspace_id, session_id, message_text,
                    priority, status, created_at_utc)
                VALUES ('legacy-1', 'ws-1', 'session-1', 'legacy', 100, 'pending',
                    '2026-08-26T00:00:00+00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);

        await SessionSteeringSchemaBootstrapper.EnsureCreatedAsync(
            db,
            NullLogger.Instance);
        await SessionSteeringSchemaBootstrapper.EnsureCreatedAsync(
            db,
            NullLogger.Instance);

        await using var verify = connection.CreateCommand();
        verify.CommandText =
            """
            SELECT target_turn_id, status, expired_at_utc
            FROM session_steering_messages
            WHERE steering_id = 'legacy-1';
            """;
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(string.Empty, reader.GetString(0));
        Assert.AreEqual("expired", reader.GetString(1));
        Assert.IsFalse(reader.IsDBNull(2));
    }
}
