using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Observability;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class TelemetryMetricSinkTests
{
    [TestMethod]
    public async Task RecordAsync_WritesMetricEvent()
    {
        await using var scope = await CreateScopeAsync();
        var sink = new TelemetryMetricSink(
            scope.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TelemetryMetricSink>.Instance);

        await sink.RecordAsync(new TelemetryMetric
        {
            Trace = RuntimeTraceContext.CreateNew(sessionId: "s1", workspaceId: "w1"),
            Source = "backend",
            Category = TelemetryMetricCategories.Tool,
            Name = "tool.call",
            Status = TelemetryMetricStatuses.Succeeded,
            DurationMs = 123,
            CountValue = 1,
            Unit = "call",
            Summary = "Tool completed.",
            Dimensions = new Dictionary<string, string>
            {
                ["tool_name"] = "example",
            },
        });

        var row = await scope.Db.TelemetryMetricEvents.SingleAsync();
        Assert.AreEqual("w1", row.WorkspaceId);
        Assert.AreEqual("s1", row.SessionId);
        Assert.AreEqual("tool", row.Category);
        Assert.AreEqual("tool.call", row.Name);
        Assert.AreEqual("succeeded", row.Status);
        Assert.AreEqual(123, row.DurationMs);
        StringAssert.Contains(row.DimensionsJson, "tool_name");
        StringAssert.Contains(row.DimensionsJson, "stage");
        StringAssert.Contains(row.DimensionsJson, "tool");
        StringAssert.Contains(row.DimensionsJson, "stage_order");
    }

    [TestMethod]
    public async Task RecordAsync_Throws_When_CustomStage_Is_Unclassified()
    {
        await using var scope = await CreateScopeAsync();
        var sink = new TelemetryMetricSink(
            scope.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TelemetryMetricSink>.Instance);

        var metric = new TelemetryMetric
        {
            Trace = RuntimeTraceContext.CreateNew(sessionId: "s1", workspaceId: "w1"),
            Source = "backend",
            Category = TelemetryMetricCategories.Tool,
            Name = "tool.call",
            Status = TelemetryMetricStatuses.Succeeded,
            Dimensions = new Dictionary<string, string>
            {
                ["stage"] = "custom.phase",
            },
        };

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => sink.RecordAsync(metric));

        StringAssert.Contains(ex.Message, "custom.phase");
        Assert.AreEqual(0, await scope.Db.TelemetryMetricEvents.CountAsync());
    }

    private static async Task<TestScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<PlatformDbContext>(options => options.UseSqlite(connection));
        var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<PlatformDbContext>();
        await db.Database.EnsureCreatedAsync();
        return new TestScope(connection, provider, db);
    }

    private sealed class TestScope(
        SqliteConnection connection,
        ServiceProvider services,
        PlatformDbContext db) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;
        public PlatformDbContext Db { get; } = db;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
