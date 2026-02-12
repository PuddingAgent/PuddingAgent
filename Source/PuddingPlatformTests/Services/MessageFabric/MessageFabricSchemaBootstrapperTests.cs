using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Models;
using PuddingPlatform.Data;
using PuddingPlatform.Services.MessageFabric;

namespace PuddingPlatformTests.Services.MessageFabric;

[TestClass]
public sealed class MessageFabricSchemaBootstrapperTests
{
    [TestMethod]
    public async Task EnsureCreatedAsync_Creates_MessageFabricTables_ForExistingSqliteDatabase()
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

            await MessageFabricSchemaBootstrapper.EnsureCreatedAsync(db);
        }

        await using (var db = new PlatformDbContext(options))
        {
            Assert.IsTrue(await TableExistsAsync(db, "room_messages"));
            Assert.IsTrue(await TableExistsAsync(db, "message_deliveries"));
            Assert.IsTrue(await TableExistsAsync(db, "room_participants"));

            var store = new MessageFabricStore(db);
            await store.PersistRouteAsync("default", new MessageRoutePlan
            {
                MessageId = "m-schema",
                RoomMessage = new RoomMessageDraft
                {
                    RoomId = "room-default",
                    MessageId = "m-schema",
                    From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner" },
                    Audience = MessageAudiences.Direct,
                    Visibility = MessageVisibilities.Private,
                    Content = "schema works",
                    CreatedAt = 100,
                },
                Deliveries =
                [
                    new MessageDeliveryDraft
                    {
                        DeliveryId = "d-schema",
                        MessageId = "m-schema",
                        Target = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
                        Priority = 5,
                    },
                ],
            });

            Assert.AreEqual(1, await db.RoomMessages.CountAsync());
            Assert.AreEqual(1, await db.MessageDeliveries.CountAsync());
        }
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_UpgradesExistingDeliveriesTable_WithClaimColumnsBeforeIndexes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE message_deliveries (
                    "Id"                INTEGER PRIMARY KEY AUTOINCREMENT,
                    delivery_id         TEXT    NOT NULL,
                    message_id          TEXT    NOT NULL,
                    workspace_id        TEXT    NOT NULL DEFAULT 'default',
                    room_id             TEXT,
                    target_kind         TEXT    NOT NULL,
                    target_id           TEXT    NOT NULL,
                    target_display_name TEXT,
                    status              TEXT    NOT NULL DEFAULT 'queued',
                    priority            INTEGER NOT NULL DEFAULT 0,
                    created_at          INTEGER NOT NULL,
                    updated_at          INTEGER NOT NULL,
                    read_at             INTEGER,
                    ack_at              INTEGER
                );
                """);

            await MessageFabricSchemaBootstrapper.EnsureCreatedAsync(db);
        }

        await using (var db = new PlatformDbContext(options))
        {
            Assert.IsTrue(await ColumnExistsAsync(db, "message_deliveries", "attempt_count"));
            Assert.IsTrue(await ColumnExistsAsync(db, "message_deliveries", "available_at"));
            Assert.IsTrue(await ColumnExistsAsync(db, "message_deliveries", "lease_until"));
            Assert.IsTrue(await ColumnExistsAsync(db, "message_deliveries", "claimed_by_execution_id"));
            Assert.IsTrue(await ColumnExistsAsync(db, "message_deliveries", "last_error"));

            var store = new MessageFabricStore(db);
            await store.PersistRouteAsync("default", new MessageRoutePlan
            {
                MessageId = "m-upgrade",
                RoomMessage = new RoomMessageDraft
                {
                    RoomId = "room-default",
                    MessageId = "m-upgrade",
                    From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner" },
                    Audience = MessageAudiences.Direct,
                    Visibility = MessageVisibilities.Public,
                    Content = "upgrade works",
                    CreatedAt = 100,
                },
                Deliveries =
                [
                    new MessageDeliveryDraft
                    {
                        DeliveryId = "d-upgrade",
                        MessageId = "m-upgrade",
                        Target = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
                        Priority = 5,
                    },
                ],
            });

            var claimed = await store.ClaimNextAsync(new MessageClaimRequest
            {
                Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
                WorkspaceId = "default",
                ExecutionId = "exec-upgrade",
            }, CancellationToken.None);

            Assert.IsNotNull(claimed);
            Assert.AreEqual("d-upgrade", claimed!.DeliveryId);
        }
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

    private static async Task<bool> ColumnExistsAsync(DbContext db, string tableName, string columnName)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
