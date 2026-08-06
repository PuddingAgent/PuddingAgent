using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Diagnostics;

namespace PuddingPlatformTests.Services;

/// <summary>
/// DiagnosticRetentionService 行为验证：
/// 1) 只删超过保留期的行（时间戳为 "O" 格式字符串，字典序比较安全）
/// 2) Enabled=false 时完全不动数据
/// 3) session_event_log 无投影水位时一律跳过（ADR-056 权威事实源保护）
/// 4) 白名单外的表名被忽略（防注入设计的副作用验证）
/// </summary>
[TestClass]
public sealed class DiagnosticRetentionServiceTests
{
    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(connection)
                .Options;
            return new PlatformDbContext(options);
        }
    }

    private static async Task<(SqliteConnection Connection, TestDbContextFactory Factory)> CreateDbAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }
        return (connection, new TestDbContextFactory(connection));
    }

    private static DiagnosticRetentionService CreateService(
        TestDbContextFactory factory, DiagnosticRetentionOptions options)
        => new(factory, Options.Create(options), NullLogger<DiagnosticRetentionService>.Instance);

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

    private static SessionEventLogEntity MakeSessionEvent(string sessionId, long sequence, DateTimeOffset recordedAt) => new()
    {
        SessionId = sessionId,
        WorkspaceId = "default",
        SequenceNum = sequence,
        EventType = "test",
        Data = "{}",
        RecordedAt = recordedAt.ToString("O"),
    };

    [TestMethod]
    public async Task Trim_Removes_Only_Rows_Older_Than_Retention()
    {
        var (connection, factory) = await CreateDbAsync();
        await using (connection)
        {
            await using (var db = factory.CreateDbContext())
            {
                db.Set<TelemetryMetricEventEntity>().AddRange(
                    MakeTelemetry("old-20d", DateTimeOffset.UtcNow.AddDays(-20)),
                    MakeTelemetry("old-15d", DateTimeOffset.UtcNow.AddDays(-15)),
                    MakeTelemetry("fresh-1d", DateTimeOffset.UtcNow.AddDays(-1)));
                await db.SaveChangesAsync();
            }

            var service = CreateService(factory, new DiagnosticRetentionOptions
            {
                Enabled = true,
                StartupDelaySeconds = 0,
                BatchSize = 2, // 故意小于待删行数，覆盖分批循环
                BatchDelayMs = 0,
                Tables =
                {
                    ["telemetry_metric_events"] = new DiagnosticRetentionTableOptions { RetentionDays = 14 },
                },
            });

            await service.RunOnceAsync();

            await using (var db = factory.CreateDbContext())
            {
                var remaining = await db.Set<TelemetryMetricEventEntity>()
                    .Select(t => t.MetricId)
                    .ToListAsync();
                CollectionAssert.AreEquivalent(new[] { "fresh-1d" }, remaining);
            }
        }
    }

    [TestMethod]
    public async Task Disabled_Service_Does_Not_Touch_Data()
    {
        var (connection, factory) = await CreateDbAsync();
        await using (connection)
        {
            await using (var db = factory.CreateDbContext())
            {
                db.Set<TelemetryMetricEventEntity>().Add(
                    MakeTelemetry("old-30d", DateTimeOffset.UtcNow.AddDays(-30)));
                await db.SaveChangesAsync();
            }

            var service = CreateService(factory, new DiagnosticRetentionOptions
            {
                Enabled = false,
                Tables =
                {
                    ["telemetry_metric_events"] = new DiagnosticRetentionTableOptions { RetentionDays = 14 },
                },
            });

            await service.RunOnceAsync();

            await using (var db = factory.CreateDbContext())
            {
                Assert.AreEqual(1, await db.Set<TelemetryMetricEventEntity>().CountAsync());
            }
        }
    }

    [TestMethod]
    public async Task SessionEventLog_Skipped_Without_Projection_Watermark()
    {
        var (connection, factory) = await CreateDbAsync();
        await using (connection)
        {
            await using (var db = factory.CreateDbContext())
            {
                db.Set<SessionEventLogEntity>().Add(
                    MakeSessionEvent("s1", 1, DateTimeOffset.UtcNow.AddDays(-30)));
                await db.SaveChangesAsync();
            }

            var service = CreateService(factory, new DiagnosticRetentionOptions
            {
                Enabled = true,
                StartupDelaySeconds = 0,
                BatchDelayMs = 0,
                Tables =
                {
                    ["session_event_log"] = new DiagnosticRetentionTableOptions { RetentionDays = 14 },
                },
            });

            await service.RunOnceAsync();

            await using (var db = factory.CreateDbContext())
            {
                Assert.AreEqual(
                    1,
                    await db.Set<SessionEventLogEntity>().CountAsync(),
                    "session_event_log 无投影水位时必须整表跳过（ADR-056 权威事实源）");
            }
        }
    }

    [TestMethod]
    public async Task Unknown_Table_Name_Is_Ignored_Safely()
    {
        var (connection, factory) = await CreateDbAsync();
        await using (connection)
        {
            var service = CreateService(factory, new DiagnosticRetentionOptions
            {
                Enabled = true,
                StartupDelaySeconds = 0,
                Tables =
                {
                    ["not_a_real_table"] = new DiagnosticRetentionTableOptions { RetentionDays = 14 },
                },
            });

            await service.RunOnceAsync(); // 不应抛异常
        }
    }
}
