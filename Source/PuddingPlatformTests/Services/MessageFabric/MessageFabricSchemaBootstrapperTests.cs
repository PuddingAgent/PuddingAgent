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
            Assert.IsTrue(await ColumnExistsAsync(db, "room_messages", "conversation_id"));
            Assert.IsTrue(await ColumnExistsAsync(db, "room_messages", "reply_to_message_id"));
            Assert.IsTrue(await ColumnExistsAsync(db, "room_messages", "correlation_id"));
            Assert.IsTrue(await ColumnExistsAsync(db, "room_messages", "causation_id"));
            Assert.IsTrue(await ColumnExistsAsync(db, "room_messages", "metadata_json"));

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
            Assert.IsTrue(await ColumnExistsAsync(db, "message_deliveries", "defer_count"));
            Assert.IsTrue(await ColumnExistsAsync(db, "message_deliveries", "execution_state"));

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

    [TestMethod]
    public async Task EnsureCreatedAsync_NormalizesDirtyQueueData()
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
                    attempt_count       INTEGER NOT NULL DEFAULT 0,
                    available_at        INTEGER,
                    lease_until         INTEGER,
                    claimed_by_execution_id TEXT,
                    last_error          TEXT,
                    created_at          INTEGER NOT NULL,
                    updated_at          INTEGER NOT NULL,
                    read_at             INTEGER,
                    ack_at              INTEGER
                );
                """);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO message_deliveries (delivery_id, message_id, workspace_id, target_kind, target_id, status, attempt_count, available_at, last_error, created_at, updated_at)
                VALUES
                    ('d-inflated', 'm1', 'default', 'agent', 'assistant', 'retrying', 248, 1000, 'agent busy spin', 1, 1),
                    ('d-stuck',    'm2', 'default', 'agent', 'assistant', 'retrying', 3, 1000, 'upstream 503', 1, 1),
                    ('d-future',   'm3', 'default', 'agent', 'assistant', 'retrying', 3, 99999999999999, 'upstream 503', 1, 1),
                    ('d-clean',    'm4', 'default', 'agent', 'assistant', 'queued', 1, NULL, NULL, 1, 1);
                """);

            await MessageFabricSchemaBootstrapper.EnsureCreatedAsync(db);
        }

        await using (var db = new PlatformDbContext(options))
        {
            // attempt_count > 3 截断为 3；retrying + attempt>=3 + availableAt 已过期 -> dead_letter；
            // execution_state 从 lastError 解析 busy（忽略大小写）。
            var inflated = await db.MessageDeliveries.AsNoTracking()
                .SingleAsync(d => d.DeliveryId == "d-inflated");
            Assert.AreEqual(3, inflated.AttemptCount);
            Assert.AreEqual("dead_letter", inflated.Status);
            Assert.AreEqual("Busy", inflated.ExecutionState);

            var stuck = await db.MessageDeliveries.AsNoTracking()
                .SingleAsync(d => d.DeliveryId == "d-stuck");
            Assert.AreEqual("dead_letter", stuck.Status);
            Assert.IsNull(stuck.ExecutionState);

            // availableAt 未过期 -> 不重判为 dead_letter。
            var future = await db.MessageDeliveries.AsNoTracking()
                .SingleAsync(d => d.DeliveryId == "d-future");
            Assert.AreEqual("retrying", future.Status);

            // defer_count 存量默认 0；无 busy 的 lastError -> execution_state null。
            var clean = await db.MessageDeliveries.AsNoTracking()
                .SingleAsync(d => d.DeliveryId == "d-clean");
            Assert.AreEqual(0, clean.DeferCount);
            Assert.IsNull(clean.ExecutionState);
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
