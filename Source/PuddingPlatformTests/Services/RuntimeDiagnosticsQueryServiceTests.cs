using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Diagnostics;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Diagnostics;

namespace PuddingPlatformTests.Services;

/// <summary>
/// DS-14 diagnostics slice: the shared timeline query applies redaction, so a native surface cannot read
/// unredacted diagnostics. What redaction actually does is pinned precisely rather than assumed.
/// </summary>
[TestClass]
public sealed class RuntimeDiagnosticsQueryServiceTests
{
    [TestMethod]
    public async Task TimelineQueryMasksSensitiveMetadataKeysAndKeepsOtherKeys()
    {
        await using var scope = await Scope.CreateAsync();
        var result = await scope.Service.QueryTimelineAsync(new RuntimeTimelineQueryDto(), CancellationToken.None);

        Assert.AreEqual(1, result.Total);
        var item = result.Items.Single();
        // 敏感 key（token）的值被替换；非敏感 key 原样保留。
        Assert.AreEqual("***REDACTED***", item.Metadata["token"]);
        Assert.AreEqual("visible summary sk-live-not-masked", item.Summary);
        Assert.AreEqual("composition", item.Metadata["component"]);
    }

    [TestMethod]
    public async Task RedactionTruncatesLongTextButDoesNotScrubFreeTextSecrets()
    {
        await using var scope = await Scope.CreateAsync();
        var item = (await scope.Service.QueryTimelineAsync(new RuntimeTimelineQueryDto(), CancellationToken.None))
            .Items.Single();

        // Error 超过 500 字符 → 截断。
        Assert.IsNotNull(item.Error);
        Assert.EndsWith("…[truncated]", item.Error);
        Assert.AreEqual(500 + "…[truncated]".Length, item.Error.Length);

        // 登记而不是掩盖：RedactText 只截断，**不会**清洗自由文本里长得像密钥的字符串。
        Assert.Contains("sk-live-not-masked", item.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("***REDACTED***", item.Summary, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RedactIsTheSharedPolicyEntryPointForEveryTimelinePath()
    {
        await using var scope = await Scope.CreateAsync();
        // 会话时间线与 E2E 证据都复用同一个 Redact，而不是各自实现一份策略。
        var masked = scope.Service.Redact(new RuntimeTimelineItemDto
        {
            Id = "x", Kind = "activity", Component = "c", Operation = "o", Status = "failed",
            Metadata = new Dictionary<string, string>
            {
                ["api_key"] = "secret-value", ["authorization"] = "Bearer abc", ["note"] = "keep",
            },
            Summary = "s",
        });
        Assert.AreEqual("***REDACTED***", masked.Metadata["api_key"]);
        Assert.AreEqual("***REDACTED***", masked.Metadata["authorization"]);
        Assert.AreEqual("keep", masked.Metadata["note"]);
        Assert.AreEqual("s", masked.Summary);

        var empty = scope.Service.Redact(new RuntimeTimelineItemDto
        {
            Id = "y", Kind = "activity", Component = "c", Operation = "o", Status = "succeeded",
        });
        Assert.AreEqual(string.Empty, empty.Summary);
        Assert.AreEqual(string.Empty, empty.Error);
    }

    private sealed class Scope(PlatformDbContext db, RuntimeDiagnosticsQueryService service) : IAsyncDisposable
    {
        public RuntimeDiagnosticsQueryService Service => service;

        public static async Task<Scope> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();

            db.RuntimeActivities.Add(new RuntimeActivityEntity
            {
                ActivityId = "activity-fixture",
                TraceId = "trace-fixture",
                CorrelationId = "correlation-fixture",
                SessionId = "session-fixture",
                WorkspaceId = "default",
                Component = "composition",
                Operation = "composition.fixture",
                Status = "failed",
                StartedAtUtc = "2026-09-27T00:00:00.000Z",
                EndedAtUtc = "2026-09-27T00:00:01.000Z",
                DurationMs = 1000,
                Severity = "error",
                // 自由文本里带一个密钥形态的字符串（不会被 RedactText 清洗），Summary 不长所以不截断。
                Summary = "visible summary sk-live-not-masked",
                ErrorMessage = new string('e', 600),
                // token 是敏感 key；component 不是。
                MetadataJson = "{\"token\":\"tok-live-secret\",\"component\":\"composition\"}",
            });
            await db.SaveChangesAsync();

            var timeline = new RuntimeTimelineQueryService(
                new SingleContextFactory(db), new ConversationDiagnosticEventProjector());
            return new Scope(db, new RuntimeDiagnosticsQueryService(timeline, new DiagnosticRedactor()));
        }

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }

        /// <summary>Minimal factory shape over the test connection (the host uses a pooled factory).</summary>
        private sealed class SingleContextFactory(PlatformDbContext db) : IDbContextFactory<PlatformDbContext>
        {
            public PlatformDbContext CreateDbContext() => db;
            public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(db);
        }
    }
}
