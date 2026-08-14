using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// RetentionPruningService 行为验证：
/// 1) 从 "Retention" 节读取四张表配置并裁剪过期行（"O" 格式时间戳字典序比较安全）
/// 2) 仅删超过保留期的行，新鲜行保留
/// 3) conversation_events / session_event_log / telemetry / runtime_activity 均按各自保留期裁剪
/// 4) chat_messages 不在白名单 → 永不裁剪
/// 5) 白名单外表名被忽略（防注入设计副作用）
/// </summary>
[TestClass]
public sealed class RetentionPruningServiceTests
{
    private static IConfiguration MakeConfiguration(params (string Key, string Value)[] entries)
    {
        var builder = new ConfigurationBuilder();
        foreach (var (key, value) in entries)
            builder.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value });
        return builder.Build();
    }

    private static (ServiceProvider Provider, SqliteConnection Connection) CreateProvider()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<PlatformDbContext>(o => o.UseSqlite(connection));
        var provider = services.BuildServiceProvider();

        using (var db = provider.GetRequiredService<PlatformDbContext>())
        {
            db.Database.EnsureCreated();
        }

        return (provider, connection);
    }

    private static RetentionPruningService CreateService(
        ServiceProvider provider, IConfiguration configuration)
        => CreateService(provider, configuration, CreateArchiveWriter());

    private static RetentionPruningService CreateService(
        ServiceProvider provider, IConfiguration configuration, RetentionArchiveWriter archiveWriter)
        => new(
            configuration,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RetentionPruningService>.Instance,
            archiveWriter);

    private static RetentionArchiveWriter CreateArchiveWriter()
        => new(
            PuddingDataPaths.FromRoot(CreateArchiveRoot()),
            NullLogger<RetentionArchiveWriter>.Instance);

    private static string CreateArchiveRoot()
        => Path.Combine(Path.GetTempPath(), "pudding-retention-tests", Guid.NewGuid().ToString("N"));

    private static SessionEventLogEntity MakeSessionEvent(string sessionId, long sequence, DateTimeOffset recordedAt) => new()
    {
        SessionId = sessionId,
        WorkspaceId = "default",
        SequenceNum = sequence,
        EventType = "test",
        Data = "{}",
        RecordedAt = recordedAt.ToString("O"),
    };

    private static ConversationEventEntity MakeConversationEvent(string conversationId, DateTimeOffset committedAt) => new()
    {
        ConversationId = conversationId,
        Sequence = 1,
        EventId = "evt-" + conversationId,
        WorkspaceId = "default",
        TurnId = "turn-" + conversationId,
        Type = "test",
        Payload = "{}",
        CommittedAt = committedAt.ToString("O"),
        OccurredAt = committedAt.ToString("O"),
    };

    private static TelemetryMetricEventEntity MakeTelemetry(string id, DateTimeOffset occurredAt) => new()
    {
        MetricId = id,
        TraceId = "trace-" + id,
        CorrelationId = "corr-" + id,
        Source = "test",
        Category = "retention-test",
        Name = "retention.test.metric",
        OccurredAtUtc = occurredAt.ToString("O"),
    };

    private static RuntimeActivityEntity MakeRuntimeActivity(string id, DateTimeOffset startedAt) => new()
    {
        ActivityId = id,
        TraceId = "trace-" + id,
        CorrelationId = "corr-" + id,
        Component = "test",
        Operation = "retention.test",
        Status = "ok",
        StartedAtUtc = startedAt.ToString("O"),
    };

    [TestMethod]
    public async Task Trims_All_Four_Tables_And_Keeps_Fresh_Rows()
    {
        var (provider, connection) = CreateProvider();
        await using (connection)
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                db.Set<SessionEventLogEntity>().AddRange(
                    MakeSessionEvent("old", 1, DateTimeOffset.UtcNow.AddDays(-20)),
                    MakeSessionEvent("fresh", 1, DateTimeOffset.UtcNow.AddDays(-1)));
                db.Set<ConversationEventEntity>().AddRange(
                    MakeConversationEvent("old-c", DateTimeOffset.UtcNow.AddDays(-40)),
                    MakeConversationEvent("fresh-c", DateTimeOffset.UtcNow.AddDays(-1)));
                db.Set<TelemetryMetricEventEntity>().AddRange(
                    MakeTelemetry("old-t", DateTimeOffset.UtcNow.AddDays(-20)),
                    MakeTelemetry("fresh-t", DateTimeOffset.UtcNow.AddDays(-1)));
                db.Set<RuntimeActivityEntity>().AddRange(
                    MakeRuntimeActivity("old-r", DateTimeOffset.UtcNow.AddDays(-20)),
                    MakeRuntimeActivity("fresh-r", DateTimeOffset.UtcNow.AddDays(-1)));
                await db.SaveChangesAsync();
            }

            var config = MakeConfiguration(
                ("Retention:session_event_log:RetentionDays", "14"),
                ("Retention:telemetry_metric_events:RetentionDays", "14"),
                ("Retention:runtime_activity:RetentionDays", "14"),
                ("Retention:conversation_events:RetentionDays", "30"),
                ("Retention:Vacuum:Enabled", "false"));

            var service = CreateService(provider, config);
            await service.RunOnceAsync();

            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                Assert.AreEqual(1, await db.Set<SessionEventLogEntity>().CountAsync(e => e.SessionId == "fresh"));
                Assert.AreEqual(0, await db.Set<SessionEventLogEntity>().CountAsync(e => e.SessionId == "old"));
                Assert.AreEqual(1, await db.Set<ConversationEventEntity>().CountAsync(e => e.ConversationId == "fresh-c"));
                Assert.AreEqual(0, await db.Set<ConversationEventEntity>().CountAsync(e => e.ConversationId == "old-c"));
                Assert.AreEqual(1, await db.Set<TelemetryMetricEventEntity>().CountAsync(e => e.MetricId == "fresh-t"));
                Assert.AreEqual(0, await db.Set<TelemetryMetricEventEntity>().CountAsync(e => e.MetricId == "old-t"));
                Assert.AreEqual(1, await db.Set<RuntimeActivityEntity>().CountAsync(e => e.ActivityId == "fresh-r"));
                Assert.AreEqual(0, await db.Set<RuntimeActivityEntity>().CountAsync(e => e.ActivityId == "old-r"));
            }
        }
    }

    [TestMethod]
    public async Task Conversation_Events_Uses_30_Day_Retention()
    {
        var (provider, connection) = CreateProvider();
        await using (connection)
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                // 20 天前：超过 session/telemetry/runtime 的 14 天，但未超过 conversation_events 的 30 天
                db.Set<ConversationEventEntity>().Add(
                    MakeConversationEvent("twenty-days", DateTimeOffset.UtcNow.AddDays(-20)));
                await db.SaveChangesAsync();
            }

            var config = MakeConfiguration(
                ("Retention:session_event_log:RetentionDays", "14"),
                ("Retention:telemetry_metric_events:RetentionDays", "14"),
                ("Retention:runtime_activity:RetentionDays", "14"),
                ("Retention:conversation_events:RetentionDays", "30"),
                ("Retention:Vacuum:Enabled", "false"));

            var service = CreateService(provider, config);
            await service.RunOnceAsync();

            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                Assert.AreEqual(
                    1,
                    await db.Set<ConversationEventEntity>().CountAsync(),
                    "conversation_events 保留 30 天：20 天前的行必须保留");
            }
        }
    }

    [TestMethod]
    public async Task ChatMessages_Are_Never_Trimmed()
    {
        var (provider, connection) = CreateProvider();
        await using (connection)
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                db.Set<ChatMessageEntity>().Add(new ChatMessageEntity
                {
                    MessageId = "m-old",
                    WorkspaceId = "default",
                    SessionId = "s1",
                    Role = "user",
                    Content = "hello",
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeMilliseconds(),
                });
                await db.SaveChangesAsync();
            }

            var config = MakeConfiguration(
                ("Retention:session_event_log:RetentionDays", "14"),
                ("Retention:telemetry_metric_events:RetentionDays", "14"),
                ("Retention:runtime_activity:RetentionDays", "14"),
                ("Retention:conversation_events:RetentionDays", "30"),
                ("Retention:chat_messages:RetentionDays", "14"),
                ("Retention:Vacuum:Enabled", "false"));

            var service = CreateService(provider, config);
            await service.RunOnceAsync(); // 不应抛异常

            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                Assert.AreEqual(1, await db.Set<ChatMessageEntity>().CountAsync(), "chat_messages 永不裁剪");
            }
        }
    }

    [TestMethod]
    public async Task Unknown_Table_Is_Ignored_Safely()
    {
        var (provider, connection) = CreateProvider();
        await using (connection)
        {
            var config = MakeConfiguration(
                ("Retention:not_a_real_table:RetentionDays", "14"),
                ("Retention:Vacuum:Enabled", "false"));

            var service = CreateService(provider, config);
            await service.RunOnceAsync(); // 不应抛异常
        }
    }

    [TestMethod]
    public async Task Archives_SessionEventLog_Before_Delete()
    {
        var (provider, connection) = CreateProvider();
        var archiveRoot = CreateArchiveRoot();
        var archiveWriter = new RetentionArchiveWriter(
            PuddingDataPaths.FromRoot(archiveRoot),
            NullLogger<RetentionArchiveWriter>.Instance);

        await using (connection)
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                db.Set<SessionEventLogEntity>().Add(
                    MakeSessionEvent("old", 1, DateTimeOffset.UtcNow.AddDays(-20)));
                await db.SaveChangesAsync();
            }

            var config = MakeConfiguration(
                ("Retention:session_event_log:RetentionDays", "14"),
                ("Retention:conversation_events:RetentionDays", "30"),
                ("Retention:telemetry_metric_events:RetentionDays", "14"),
                ("Retention:runtime_activity:RetentionDays", "14"),
                ("Retention:Vacuum:Enabled", "false"));

            var service = CreateService(provider, config, archiveWriter);
            await service.RunOnceAsync();

            // 已删除
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                Assert.AreEqual(0, await db.Set<SessionEventLogEntity>().CountAsync(e => e.SessionId == "old"));
            }

            // 已归档（当天分片，含完整字段与元数据）
            var day = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd");
            var archiveFile = PuddingDataPaths.FromRoot(archiveRoot)
                .PlatformRetentionArchiveFile("session_event_log", day);
            Assert.IsTrue(File.Exists(archiveFile), $"archive file not found: {archiveFile}");

            var lines = await File.ReadAllLinesAsync(archiveFile);
            Assert.AreEqual(1, lines.Length);
            using var doc = JsonDocument.Parse(lines[0]);
            var root = doc.RootElement;
            Assert.AreEqual("old", root.GetProperty("SessionId").GetString());
            Assert.AreEqual("session_event_log", root.GetProperty("table_name").GetString());
            Assert.IsTrue(root.TryGetProperty("archived_at", out _));
            Assert.IsTrue(root.TryGetProperty("retention_cutoff", out _));
        }
    }

    [TestMethod]
    public async Task Archives_ConversationEvents_Before_Delete()
    {
        var (provider, connection) = CreateProvider();
        var archiveRoot = CreateArchiveRoot();
        var archiveWriter = new RetentionArchiveWriter(
            PuddingDataPaths.FromRoot(archiveRoot),
            NullLogger<RetentionArchiveWriter>.Instance);

        await using (connection)
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                db.Set<ConversationEventEntity>().Add(
                    MakeConversationEvent("old-c", DateTimeOffset.UtcNow.AddDays(-40)));
                await db.SaveChangesAsync();
            }

            var config = MakeConfiguration(
                ("Retention:session_event_log:RetentionDays", "14"),
                ("Retention:conversation_events:RetentionDays", "30"),
                ("Retention:telemetry_metric_events:RetentionDays", "14"),
                ("Retention:runtime_activity:RetentionDays", "14"),
                ("Retention:Vacuum:Enabled", "false"));

            var service = CreateService(provider, config, archiveWriter);
            await service.RunOnceAsync();

            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                Assert.AreEqual(0, await db.Set<ConversationEventEntity>().CountAsync(e => e.ConversationId == "old-c"));
            }

            var day = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd");
            var archiveFile = PuddingDataPaths.FromRoot(archiveRoot)
                .PlatformRetentionArchiveFile("conversation_events", day);
            Assert.IsTrue(File.Exists(archiveFile), $"archive file not found: {archiveFile}");

            var lines = await File.ReadAllLinesAsync(archiveFile);
            Assert.AreEqual(1, lines.Length);
            using var doc = JsonDocument.Parse(lines[0]);
            var root = doc.RootElement;
            Assert.AreEqual("old-c", root.GetProperty("ConversationId").GetString());
            Assert.AreEqual("conversation_events", root.GetProperty("table_name").GetString());
        }
    }

    [TestMethod]
    public async Task Archive_Failure_Aborts_Delete()
    {
        var (provider, connection) = CreateProvider();
        await using (connection)
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                db.Set<SessionEventLogEntity>().Add(
                    MakeSessionEvent("old", 1, DateTimeOffset.UtcNow.AddDays(-20)));
                await db.SaveChangesAsync();
            }

            var config = MakeConfiguration(
                ("Retention:session_event_log:RetentionDays", "14"),
                ("Retention:conversation_events:RetentionDays", "30"),
                ("Retention:telemetry_metric_events:RetentionDays", "14"),
                ("Retention:runtime_activity:RetentionDays", "14"),
                ("Retention:Vacuum:Enabled", "false"));

            var failingWriter = new FailingRetentionArchiveWriter(
                PuddingDataPaths.FromRoot(CreateArchiveRoot()));
            var service = CreateService(provider, config, failingWriter);

            // 归档失败 → 直调 RunOnceAsync 抛异常（生产环境由 RunLoopAsync 捕获重试）
            await Assert.ThrowsExactlyAsync<IOException>(() => service.RunOnceAsync());

            // 行未被删除（绝不先删后归档）
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                Assert.AreEqual(1, await db.Set<SessionEventLogEntity>().CountAsync(e => e.SessionId == "old"));
            }
        }
    }

    private sealed class FailingRetentionArchiveWriter : RetentionArchiveWriter
    {
        public FailingRetentionArchiveWriter(PuddingDataPaths paths)
            : base(paths, NullLogger<RetentionArchiveWriter>.Instance)
        {
        }

        public override Task ArchiveBatchAsync<T>(
            string tableName, IReadOnlyList<T> rows, DateTimeOffset cutoff, CancellationToken ct = default)
            => throw new IOException("simulated archive failure");
    }
}
