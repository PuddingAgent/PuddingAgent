using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatformTests.Controllers;

[TestClass]
public sealed class TelemetryMetricsControllerTests
{
    [TestMethod]
    public async Task GetSummary_Groups_Filtered_Metric_Events()
    {
        await using var scope = await CreateScopeAsync();
        scope.Db.TelemetryMetricEvents.AddRange(
            Metric(
                workspaceId: "workspace-1",
                sessionId: "session-1",
                category: "tool",
                name: "tool.execution",
                status: "succeeded",
                occurredAtUtc: "2026-01-01T00:00:00.0000000Z",
                durationMs: 10,
                severity: "info"),
            Metric(
                workspaceId: "workspace-1",
                sessionId: "session-1",
                category: "tool",
                name: "tool.execution",
                status: "failed",
                occurredAtUtc: "2026-01-01T01:00:00.0000000Z",
                durationMs: 20,
                severity: "error"),
            Metric(
                workspaceId: "workspace-1",
                sessionId: "session-1",
                category: "llm",
                name: "llm.request",
                status: "succeeded",
                occurredAtUtc: "2026-01-01T02:00:00.0000000Z",
                durationMs: 30,
                severity: "info"));
        await scope.Db.SaveChangesAsync();

        var controller = new TelemetryMetricsController(scope.Db);

        var result = await controller.GetSummary(
            workspaceId: "workspace-1",
            sessionId: "session-1",
            category: "tool",
            name: "tool.execution",
            sinceUtc: "2026-01-01T00:30:00.0000000Z",
            limit: 10,
            ct: CancellationToken.None);

        var ok = Assert.IsInstanceOfType<OkObjectResult>(result.Result);
        var summary = Assert.IsInstanceOfType<TelemetryMetricsSummaryDto>(ok.Value);
        Assert.AreEqual(1, summary.TotalGroups);
        var group = summary.Groups.Single();
        Assert.AreEqual("tool", group.Category);
        Assert.AreEqual("tool.execution", group.Name);
        Assert.AreEqual("failed", group.Status);
        Assert.AreEqual(1, group.EventCount);
        Assert.AreEqual(1, group.CountValueSum);
        Assert.AreEqual(20, group.AverageDurationMs);
        Assert.AreEqual(20, group.MaxDurationMs);
        Assert.AreEqual(1, group.ErrorCount);
        Assert.AreEqual("2026-01-01T01:00:00.0000000Z", group.LastOccurredAtUtc);
    }

    private static TelemetryMetricEventEntity Metric(
        string workspaceId,
        string sessionId,
        string category,
        string name,
        string status,
        string occurredAtUtc,
        long durationMs,
        string severity)
    {
        return new TelemetryMetricEventEntity
        {
            MetricId = Guid.NewGuid().ToString("N"),
            TraceId = Guid.NewGuid().ToString("N"),
            CorrelationId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            SessionId = sessionId,
            Source = "runtime",
            Category = category,
            Name = name,
            Status = status,
            OccurredAtUtc = occurredAtUtc,
            DurationMs = durationMs,
            CountValue = 1,
            Severity = severity,
        };
    }

    private static async Task<TestScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return new TestScope(connection, db);
    }

    private sealed class TestScope(
        SqliteConnection connection,
        PlatformDbContext db) : IAsyncDisposable
    {
        public PlatformDbContext Db { get; } = db;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
