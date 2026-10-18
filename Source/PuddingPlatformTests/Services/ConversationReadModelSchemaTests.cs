using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 阶段 C（Plan《会话首屏与渐进加载架构优化方案》§3.2/§6/§11.2）读模型持久化基础：
/// 新库 EF 模型、存量库 bootstrapper、conversation_events 两条显式维护索引与查询计划。
/// </summary>
[TestClass]
public sealed class ConversationReadModelSchemaTests
{
    [TestMethod]
    public async Task ModelDeclaresReadModelEntitiesAndSharedIndexNames()
    {
        await using var temp = TempPlatformDb.Create();
        await using var db = new PlatformDbContext(temp.Options);
        await db.Database.EnsureCreatedAsync();

        var eventEntity = db.Model.FindEntityType(typeof(ConversationEventEntity));
        Assert.IsNotNull(eventEntity);
        var eventIndexNames = eventEntity.GetIndexes()
            .Select(index => index.GetDatabaseName())
            .Where(name => name is not null)
            .ToArray();

        CollectionAssert.Contains(
            eventIndexNames,
            ConversationReadModelSchema.ConversationEventsConversationMessageSequenceIndex);
        CollectionAssert.Contains(
            eventIndexNames,
            ConversationReadModelSchema.ConversationEventsConversationTurnSequenceIndex);

        Assert.AreEqual(
            ConversationReadModelSchema.TurnSummariesTable,
            db.Model.FindEntityType(typeof(ConversationTurnSummaryEntity))?.GetTableName());
        Assert.AreEqual(
            ConversationReadModelSchema.ReadStateTable,
            db.Model.FindEntityType(typeof(ConversationReadStateEntity))?.GetTableName());
    }

    [TestMethod]
    public async Task EnsureCreated_BootstrapsLegacyDatabaseWithReadModelTables()
    {
        await using var temp = TempPlatformDb.Create();
        await CreateRawDatabaseFileAsync(temp.DbPath);

        await using (var db = new PlatformDbContext(temp.Options))
        {
            await ConversationReadModelSchemaBootstrapper.EnsureCreatedAsync(db);
        }

        await using (var db = new PlatformDbContext(temp.Options))
        {
            Assert.IsTrue(await TableExistsAsync(db, ConversationReadModelSchema.TurnSummariesTable));
            Assert.IsTrue(await TableExistsAsync(db, ConversationReadModelSchema.ReadStateTable));
            Assert.IsTrue(await IndexExistsAsync(
                db, ConversationReadModelSchema.TurnSummariesConversationLastSequenceIndex));

            db.ConversationTurnSummaries.Add(NewTurnSummary("conv-1", "turn-1"));
            db.ConversationReadStates.Add(NewReadState("conv-1"));
            await db.SaveChangesAsync();
        }

        await using (var db = new PlatformDbContext(temp.Options))
        {
            var turn = await db.ConversationTurnSummaries.SingleAsync();
            Assert.AreEqual("conv-1", turn.ConversationId);
            Assert.AreEqual(7, turn.TerminalState);
            Assert.AreEqual("preview", turn.ActivePreview);
            Assert.IsTrue(turn.ActivePreviewTruncated);

            var state = await db.ConversationReadStates.SingleAsync();
            Assert.AreEqual(2, state.InitializationState);
            Assert.AreEqual("[]", state.SubAgentRunsJson);
            Assert.AreEqual(3, state.Revision);
        }
    }

    [TestMethod]
    public async Task EnsureCreated_IsIdempotent()
    {
        await using var temp = TempPlatformDb.Create();
        await using var db = new PlatformDbContext(temp.Options);

        await ConversationReadModelSchemaBootstrapper.EnsureCreatedAsync(db);
        await ConversationReadModelSchemaBootstrapper.EnsureCreatedAsync(db);

        Assert.AreEqual(1, await CountObjectsAsync(db, "table", ConversationReadModelSchema.TurnSummariesTable));
        Assert.AreEqual(1, await CountObjectsAsync(db, "index", ConversationReadModelSchema.TurnSummariesConversationLastSequenceIndex));
    }

    [TestMethod]
    public async Task EnsureConversationEventIndexes_CreatesBothIndexesAndUsesThem()
    {
        await using var temp = TempPlatformDb.Create();
        await CreateLegacyConversationEventsAsync(temp.DbPath);

        var created = Array.Empty<string>();
        await using (var db = new PlatformDbContext(temp.Options))
        {
            var result = await ConversationReadModelSchemaBootstrapper.EnsureConversationEventIndexesAsync(db);
            created = result.CreatedIndexes.ToArray();

            Assert.HasCount(2, result.CreatedIndexes);
            CollectionAssert.Contains(
                result.CreatedIndexes.ToArray(),
                ConversationReadModelSchema.ConversationEventsConversationMessageSequenceIndex);
            CollectionAssert.Contains(
                result.CreatedIndexes.ToArray(),
                ConversationReadModelSchema.ConversationEventsConversationTurnSequenceIndex);
            Assert.IsEmpty(result.AlreadyPresentIndexes);

            var second = await ConversationReadModelSchemaBootstrapper.EnsureConversationEventIndexesAsync(db);
            Assert.IsEmpty(second.CreatedIndexes);
            Assert.HasCount(2, second.AlreadyPresentIndexes);
        }

        Assert.HasCount(2, created);
        await using (var db = new PlatformDbContext(temp.Options))
        {
            Assert.IsTrue(await IndexExistsAsync(
                db, ConversationReadModelSchema.ConversationEventsConversationMessageSequenceIndex));
            Assert.IsTrue(await IndexExistsAsync(
                db, ConversationReadModelSchema.ConversationEventsConversationTurnSequenceIndex));

            var messagePlan = await ExplainAsync(
                db,
                """
                SELECT sequence FROM conversation_events
                WHERE conversation_id = 'conv-1' AND message_id = 'msg-1'
                ORDER BY sequence;
                """);
            StringAssert.Contains(
                messagePlan,
                ConversationReadModelSchema.ConversationEventsConversationMessageSequenceIndex,
                "message 维度查询必须按 message_id 等值收敛（不能用 conversation_id 全扫）");

            var turnPlan = await ExplainAsync(
                db,
                """
                SELECT sequence FROM conversation_events
                WHERE conversation_id = 'conv-1' AND turn_id = 'turn-1'
                ORDER BY sequence;
                """);
            StringAssert.Contains(
                turnPlan,
                ConversationReadModelSchema.ConversationEventsConversationTurnSequenceIndex,
                "turn 维度查询必须按 turn_id 等值收敛（不能用 conversation_id 全扫）");
        }
    }

    [TestMethod]
    public async Task EnsureConversationEventIndexes_SkipsWhenConversationEventsMissing()
    {
        await using var temp = TempPlatformDb.Create();
        await CreateRawDatabaseFileAsync(temp.DbPath);

        await using var db = new PlatformDbContext(temp.Options);
        var result = await ConversationReadModelSchemaBootstrapper.EnsureConversationEventIndexesAsync(db);

        Assert.IsEmpty(result.CreatedIndexes);
        Assert.IsEmpty(result.AlreadyPresentIndexes);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static ConversationTurnSummaryEntity NewTurnSummary(string conversationId, string turnId) => new()
    {
        ConversationId = conversationId,
        TurnId = turnId,
        RootRunId = "run-1",
        TerminalState = 7,
        HasAssistantBody = true,
        ContentAppendCount = 2,
        ActivePreview = "preview",
        ActivePreviewTruncated = true,
        FirstSequence = 10,
        LastSequence = 12,
        SummaryRevision = 4,
        ProcessCompleteness = 2,
        ReadModelVersion = 1,
        UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
    };

    private static ConversationReadStateEntity NewReadState(string conversationId) => new()
    {
        ConversationId = conversationId,
        InitializationState = 2,
        CurrentTurnId = "turn-1",
        CurrentRootTerminalState = 1,
        CompactionStartedSequence = 3,
        SubAgentRunsJson = "[]",
        ReadModelVersion = 1,
        Revision = 3,
        UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
    };

    private static async Task CreateRawDatabaseFileAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS legacy_marker (id INTEGER PRIMARY KEY);";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateLegacyConversationEventsAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE IF NOT EXISTS conversation_events (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    conversation_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    event_id TEXT NOT NULL,
                    workspace_id TEXT NOT NULL,
                    turn_id TEXT NOT NULL,
                    message_id TEXT NULL,
                    type TEXT NOT NULL,
                    payload TEXT NOT NULL DEFAULT '{}',
                    occurred_at TEXT NOT NULL DEFAULT '',
                    committed_at TEXT NOT NULL DEFAULT ''
                );
                """;
            await create.ExecuteNonQueryAsync();
        }

        await using var transaction = await connection.BeginTransactionAsync();
        await using var insert = connection.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText =
            """
            INSERT INTO conversation_events
                (conversation_id, sequence, event_id, workspace_id, turn_id, message_id, type)
            VALUES ($conv, $seq, $event, 'ws', $turn, $message, 'message.content.appended');
            """;
        var conv = insert.CreateParameter();
        conv.ParameterName = "$conv";
        var seq = insert.CreateParameter();
        seq.ParameterName = "$seq";
        var eventId = insert.CreateParameter();
        eventId.ParameterName = "$event";
        var turn = insert.CreateParameter();
        turn.ParameterName = "$turn";
        var message = insert.CreateParameter();
        message.ParameterName = "$message";
        foreach (var parameter in new[] { conv, seq, eventId, turn, message })
            insert.Parameters.Add(parameter);

        for (var i = 1; i <= 200; i++)
        {
            conv.Value = $"conv-{(i % 4) + 1}";
            seq.Value = i;
            eventId.Value = $"evt-{i}";
            turn.Value = $"turn-{(i % 20) + 1}";
            message.Value = i % 5 == 0 ? DBNull.Value : $"msg-{(i % 10) + 1}";
            await insert.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task<bool> TableExistsAsync(PlatformDbContext db, string tableName)
    {
        var count = await CountObjectsAsync(db, "table", tableName);
        return count > 0;
    }

    private static async Task<bool> IndexExistsAsync(PlatformDbContext db, string indexName)
    {
        var count = await CountObjectsAsync(db, "index", indexName);
        return count > 0;
    }

    private static async Task<long> CountObjectsAsync(PlatformDbContext db, string type, string name)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = $type AND name = $name;";
        var typeParam = command.CreateParameter();
        typeParam.ParameterName = "$type";
        typeParam.Value = type;
        command.Parameters.Add(typeParam);
        var nameParam = command.CreateParameter();
        nameParam.ParameterName = "$name";
        nameParam.Value = name;
        command.Parameters.Add(nameParam);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ExplainAsync(PlatformDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        await using var reader = await command.ExecuteReaderAsync();
        var details = new List<string>();
        while (await reader.ReadAsync())
        {
            // sqlite: id, parent, notused, detail
            details.Add(reader.IsDBNull(3) ? string.Empty : reader.GetString(3));
        }

        return string.Join(" | ", details);
    }

    private sealed class TempPlatformDb : IAsyncDisposable
    {
        private TempPlatformDb(string rootPath, string dbPath)
        {
            RootPath = rootPath;
            DbPath = dbPath;
            Options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
        }

        public string RootPath { get; }
        public string DbPath { get; }
        public DbContextOptions<PlatformDbContext> Options { get; }

        public static TempPlatformDb Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "pudding-rm-schema-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TempPlatformDb(root, Path.Combine(root, "platform.db"));
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(RootPath))
                    Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup; test temp dirs are outside the repo
            }

            return ValueTask.CompletedTask;
        }
    }
}
