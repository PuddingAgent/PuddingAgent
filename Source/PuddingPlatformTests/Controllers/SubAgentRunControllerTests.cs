using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.SubAgents;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;

namespace PuddingPlatformTests.Controllers;

[TestClass]
public sealed class SubAgentRunControllerTests
{
    [TestMethod]
    public async Task Get_Uses_Archived_Terminal_Metrics_Instead_Of_Zeroes()
    {
        await using var db = await CreateDatabaseAsync();
        var archive = CreateArchive();
        var controller = new SubAgentRunController(
            new TestDbContextFactory(db.Options),
            new StubRunStore(archive));

        var result = await controller.Get("run-archive", CancellationToken.None);

        var detail = Assert.IsInstanceOfType<OkObjectResult>(result.Result).Value;
        var dto = Assert.IsInstanceOfType<SubAgentRunDetailDto>(detail);
        Assert.AreEqual(37, dto.Summary.TotalRounds);
        Assert.AreEqual(85, dto.Summary.TotalToolCalls);
        Assert.AreEqual(360_000, dto.Summary.TotalDurationMs);
    }

    [TestMethod]
    public async Task Events_Returns_Full_Archived_Payload_For_Authenticated_Inspector()
    {
        await using var db = await CreateDatabaseAsync();
        var archive = CreateArchive();
        var controller = new SubAgentRunController(
            new TestDbContextFactory(db.Options),
            new StubRunStore(archive));

        var result = await controller.Events("run-archive", 100, 0, CancellationToken.None);

        var page = Assert.IsInstanceOfType<OkObjectResult>(result.Result).Value;
        var dto = Assert.IsInstanceOfType<PagedResultDto<SubAgentRunEventDto>>(page);
        var payload = dto.Items.Single().Payload;
        Assert.IsNotNull(payload);
        Assert.AreEqual("shell", payload.Value.GetProperty("tool_name").GetString());
        Assert.AreEqual("git status", payload.Value.GetProperty("arguments_preview").GetString());
    }

    private static SubAgentRunArchive CreateArchive()
    {
        var rawEvent = JsonSerializer.Deserialize<object>("""
            {
              "eventId": "event-tool",
              "eventType": "subagent.tool.started",
              "timestamp": "2026-07-19T00:05:01.000Z",
              "payload": {
                "tool_name": "shell",
                "arguments_preview": "git status"
              }
            }
            """)!;
        return new SubAgentRunArchive
        {
            Manifest = new SubAgentRunManifest
            {
                RunId = "run-archive",
                ParentSessionId = "parent",
                SubSessionId = "sub",
                WorkspaceId = "default",
                AgentInstanceId = "agent",
                TemplateId = "template",
                Task = "inspect archived events",
                Status = "completed",
                StartedAt = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero),
                CompletedAt = new DateTimeOffset(2026, 7, 19, 0, 6, 0, TimeSpan.Zero),
                TotalRounds = 37,
                TotalToolCalls = 85,
                TotalDurationMs = 360_000,
            },
            Events = [rawEvent],
            Tools =
            [
                new SubAgentToolAuditEntry
                {
                    ToolCallId = "call-tool",
                    ToolName = "shell",
                    ArgsHash = "hash",
                    Success = true,
                    DurationMs = 10,
                },
            ],
        };
    }

    private static async Task<TestDatabase> CreateDatabaseAsync()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "SubAgentRunControllerTests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return new TestDatabase(dbPath, options);
    }

    private sealed class TestDatabase(
        string dbPath,
        DbContextOptions<PlatformDbContext> options) : IAsyncDisposable
    {
        public DbContextOptions<PlatformDbContext> Options { get; } = options;

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class StubRunStore(SubAgentRunArchive archive) : ISubAgentRunStore
    {
        public Task<SubAgentRunArchive?> GetRunArchiveAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult<SubAgentRunArchive?>(runId == archive.Manifest.RunId ? archive : null);

        public Task<SubAgentRunHandle> CreateRunAsync(SubAgentRunCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AppendEventAsync(string runId, string eventType, object payload, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AppendToolAuditAsync(string runId, SubAgentToolAuditEntry entry, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SubAgentRunTerminalWriteResult> CompleteRunAsync(string runId, SubAgentRunCompletion completion, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> RecoverInterruptedRunsAsync(DateTimeOffset startedBeforeUtc, int maxRuns, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> ReplayPendingConversationEventsAsync(int maxRuns, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteRunAsync(string runId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
