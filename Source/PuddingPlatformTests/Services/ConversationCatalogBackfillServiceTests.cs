using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// P0-4f C3: ConversationCatalogBackfillService 历史回填测试。
/// 覆盖：状态机映射（active/idle/failed/cancelled/frozen+successor）、title pick A 反查截断、
/// created_at/last_active_at 语义，以及幂等性（重跑收敛、无重复行、created_at 不被覆盖）。
/// </summary>
[TestClass]
public sealed class ConversationCatalogBackfillServiceTests
{
    [TestMethod]
    public async Task BackfillAsync_BuildsCatalogRowsAndIsIdempotent()
    {
        var dbPath = NewTempDbPath();

        try
        {
            await using var harness = await CreateHarnessAsync(dbPath);

            // 确保 EF 建表（含 ChatMessages，title 反查依赖），再让 EventStore 补齐裸 SQL 索引。
            await using (var db = await harness.Factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }
            await harness.EventStore.EnsureTablesAsync(CancellationToken.None);

            var t1 = "2026-06-03T08:00:00+00:00";
            var t2 = "2026-06-03T08:01:00+00:00";
            var content1 = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJ"; // > 30 chars

            await using (var db = await harness.Factory.CreateDbContextAsync())
            {
                db.ConversationEvents.AddRange(
                    Evt("conv-1", 1, ConversationEventTypes.TurnAccepted, "agent-1", """{"userMessageId":"msg-1"}""", t1),
                    Evt("conv-1", 2, ConversationEventTypes.TurnCompleted, "agent-1", """{"reply":"ok"}""", t2),
                    Evt("conv-2", 1, ConversationEventTypes.TurnAccepted, "agent-2", """{"userMessageId":"msg-2"}""", t1),
                    Evt("conv-2", 2, ConversationEventTypes.TurnFailed, "agent-2", """{"error":"boom"}""", t2),
                    Evt("conv-3", 1, ConversationEventTypes.TurnAccepted, "agent-3", "{}", t1),
                    Evt("conv-3", 2, ConversationEventTypes.TurnCancelled, "agent-3", "{}", t2),
                    Evt("conv-4", 1, ConversationEventTypes.TurnAccepted, "agent-4", "{}", t1),
                    Evt("conv-4", 2, ConversationEventTypes.ContextCompactionCompleted, "agent-4", """{"newConversationId":"conv-5"}""", t2));
                db.ChatMessages.AddRange(
                    Msg("msg-1", "conv-1", content1),
                    Msg("msg-2", "conv-2", "short second message"));
                await db.SaveChangesAsync();
            }

            var first = await harness.Service.BackfillAsync();
            var second = await harness.Service.BackfillAsync();

            Assert.AreEqual(4, first.ConversationsScanned);
            Assert.AreEqual(8, first.EventsProcessed);
            Assert.AreEqual(0, first.Errors, string.Join("; ", first.ErrorDetails));
            Assert.AreEqual(4, second.ConversationsScanned);
            Assert.AreEqual(8, second.EventsProcessed);

            await using var verify = await harness.Factory.CreateDbContextAsync();
            var rows = await verify.ConversationCatalogs
                .OrderBy(c => c.ConversationId)
                .ToListAsync();

            // 幂等：重跑不产生重复行。
            Assert.AreEqual(4, rows.Count);

            var created1 = DateTimeOffset.Parse(t1).ToString("O");
            var active1 = DateTimeOffset.Parse(t2).ToString("O");

            var conv1 = rows.Single(c => c.ConversationId == "conv-1");
            Assert.AreEqual("idle", conv1.Status);
            Assert.AreEqual("agent-1", conv1.PrincipalId);
            Assert.AreEqual(content1.Substring(0, 30), conv1.Title);
            Assert.AreEqual(created1, conv1.CreatedAt);
            Assert.AreEqual(active1, conv1.LastActiveAt);
            Assert.IsNull(conv1.ParentConversationId);
            Assert.IsNull(conv1.SuccessorConversationId);

            var conv2 = rows.Single(c => c.ConversationId == "conv-2");
            Assert.AreEqual("failed", conv2.Status);
            Assert.AreEqual("short second message", conv2.Title);

            var conv3 = rows.Single(c => c.ConversationId == "conv-3");
            Assert.AreEqual("cancelled", conv3.Status);

            var conv4 = rows.Single(c => c.ConversationId == "conv-4");
            Assert.AreEqual("frozen", conv4.Status);
            Assert.AreEqual("conv-5", conv4.SuccessorConversationId);
        }
        finally
        {
            DeleteDbFile(dbPath);
        }
    }

    [TestMethod]
    public async Task BackfillAsync_EmptyEventLog_ProducesNoRows()
    {
        var dbPath = NewTempDbPath();

        try
        {
            await using var harness = await CreateHarnessAsync(dbPath);
            await using (var db = await harness.Factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }
            await harness.EventStore.EnsureTablesAsync(CancellationToken.None);

            var result = await harness.Service.BackfillAsync();

            Assert.AreEqual(0, result.ConversationsScanned);
            Assert.AreEqual(0, result.EventsProcessed);
            Assert.AreEqual(0, result.Errors);
        }
        finally
        {
            DeleteDbFile(dbPath);
        }
    }

    private static string NewTempDbPath()
        => Path.Combine(
            Path.GetTempPath(),
            $"pudding_catalog_backfill_{Guid.NewGuid():N}.db");

    private static void DeleteDbFile(string dbPath)
    {
        // 释放 SQLite 连接池对临时 db 文件的占用（文件型 SQLite + 连接池默认开启）。
        SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = dbPath + suffix;
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // 清理失败不影响测试结果判定。
            }
        }
    }

    private static async Task<Harness> CreateHarnessAsync(string dbPath)
    {
        // Pooling=False：避免连接池持有文件句柄，保证测试结束可删除临时 db。
        var connectionString = $"Data Source={dbPath};Pooling=False";
        var services = new ServiceCollection();
        services.AddDbContextFactory<PlatformDbContext>(
            options => options.UseSqlite(connectionString));
        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<PlatformDbContext>>().CreateDbContext());
        var provider = services.BuildServiceProvider();

        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();

        var eventStore = new ConversationEventStore(
            scopeFactory,
            new CommittedEventSignal(),
            NullLogger<ConversationEventStore>.Instance);
        var writer = new ConversationCatalogWriter(
            scopeFactory,
            NullLogger<ConversationCatalogWriter>.Instance);
        var service = new ConversationCatalogBackfillService(
            factory,
            eventStore,
            writer,
            NullLogger<ConversationCatalogBackfillService>.Instance);

        return new Harness(provider, factory, eventStore, service);
    }

    private static ConversationEventEntity Evt(
        string conversationId,
        long sequence,
        string type,
        string? agentId,
        string payload,
        string occurredAt)
        => new()
        {
            ConversationId = conversationId,
            Sequence = sequence,
            EventId = $"{conversationId}-evt-{sequence}",
            WorkspaceId = "ws-1",
            TurnId = "turn-1",
            Type = type,
            SchemaVersion = 2,
            Payload = payload,
            OccurredAt = occurredAt,
            CommittedAt = occurredAt,
            AgentId = agentId,
        };

    private static ChatMessageEntity Msg(string messageId, string sessionId, string content)
        => new()
        {
            MessageId = messageId,
            SessionId = sessionId,
            WorkspaceId = "ws-1",
            Role = "user",
            Content = content,
        };

    private sealed class Harness(
        ServiceProvider provider,
        IDbContextFactory<PlatformDbContext> factory,
        IConversationEventStore eventStore,
        ConversationCatalogBackfillService service) : IAsyncDisposable
    {
        public IDbContextFactory<PlatformDbContext> Factory { get; } = factory;
        public IConversationEventStore EventStore { get; } = eventStore;
        public ConversationCatalogBackfillService Service { get; } = service;

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
        }
    }
}
