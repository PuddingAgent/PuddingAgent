using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.SubAgents;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// DS-14 subagent-runs slice: the list query sunk out of SubAgentRunController (which read the DbContext
/// directly), plus the archive projections it now owns.
/// </summary>
[TestClass]
public sealed class SubAgentRunQueryServiceTests
{
    [TestMethod]
    public async Task ListFiltersByEachCoreConditionAndPages()
    {
        await using var harness = await Harness.CreateAsync();

        var all = await harness.Service.ListAsync(null, null, null, null, 20, 0);
        Assert.AreEqual(3, all.Total);
        Assert.AreEqual(3, all.Items.Count);
        // StartedAt 是字符串且为 ISO-8601，倒序即最新在前。
        Assert.AreEqual("run-3", all.Items[0].RunId);
        Assert.AreEqual("run-1", all.Items[2].RunId);

        Assert.AreEqual(2, (await harness.Service.ListAsync("parent-a", null, null, null, 20, 0)).Total);
        Assert.AreEqual(1, (await harness.Service.ListAsync(null, "ws-b", null, null, 20, 0)).Total);
        // agent-1 出现在两次运行里（run-1 与 run-3）。
        Assert.AreEqual(2, (await harness.Service.ListAsync(null, null, "agent-1", null, 20, 0)).Total);
        Assert.AreEqual(1, (await harness.Service.ListAsync(null, null, null, "failed", 20, 0)).Total);
        Assert.AreEqual(0, (await harness.Service.ListAsync(null, null, null, "exploded", 20, 0)).Total);

        var page = await harness.Service.ListAsync(null, null, null, null, 1, 1);
        Assert.AreEqual(3, page.Total);
        Assert.AreEqual(1, page.Items.Count);
        Assert.AreEqual(1, page.Offset);
        Assert.AreEqual(1, page.Limit);
        Assert.AreEqual("run-2", page.Items[0].RunId);
    }

    [TestMethod]
    public void PaginationValidationMatchesCore()
    {
        Assert.IsNull(SubAgentRunQueryService.ValidatePagination(1, 0));
        Assert.IsNull(SubAgentRunQueryService.ValidatePagination(500, 10_000));
        StringAssert.Contains(SubAgentRunQueryService.ValidatePagination(0, 0)!, "1-500");
        StringAssert.Contains(SubAgentRunQueryService.ValidatePagination(501, 0)!, "1-500");
        StringAssert.Contains(SubAgentRunQueryService.ValidatePagination(20, -1)!, "offset");
    }

    [TestMethod]
    public async Task UnknownRunIsNullRatherThanAnEmptyDetail()
    {
        await using var harness = await Harness.CreateAsync();
        Assert.IsNull(await harness.Service.GetAsync("no-such-run"));
        Assert.IsNull(await harness.Service.EventsAsync("no-such-run", 100, 0));
        Assert.IsNull(await harness.Service.ToolsAsync("no-such-run", 100, 0));
        Assert.IsNull(await harness.Service.OutputAsync("no-such-run"));
    }

    [TestMethod]
    public async Task ArchiveProjectionFallsBackToArchiveCountsAndFlagsDegradation()
    {
        await using var harness = await Harness.CreateAsync();
        var detail = await harness.Service.GetAsync("run-1");
        Assert.IsNotNull(detail);
        var summary = detail.Summary;
        Assert.AreEqual("run-1", summary.RunId);
        Assert.AreEqual("failed", summary.Status);
        // Manifest 没给总用时/轮次/工具数时用归档回填。
        Assert.AreEqual(1, summary.TotalRounds);
        Assert.AreEqual(1, summary.TotalToolCalls);
        Assert.IsTrue(summary.TotalDurationMs > 0);
        Assert.AreEqual("boom", summary.ErrorMessage);
        // 归档降级必须原样带出，否则界面会以为时间线完整。
        Assert.IsNotNull(detail.ArchiveDegraded);
        Assert.AreEqual(2, detail.EventCount);
        Assert.AreEqual(1, detail.ToolCallCount);
        Assert.AreEqual("the output", detail.Output);
        Assert.AreEqual("the task", detail.Task);

        var events = await harness.Service.EventsAsync("run-1", 100, 0);
        Assert.IsNotNull(events);
        Assert.AreEqual(2, events.Total);
        // 事件投影保留完整 payload（回放需要），同时给出大小与预览。
        Assert.AreEqual("subagent.round.completed", events.Items[0].EventType);
        Assert.IsTrue(events.Items[0].PayloadSize > 0);
        Assert.IsNotNull(events.Items[0].PayloadPreview);

        var tools = await harness.Service.ToolsAsync("run-1", 1, 0);
        Assert.IsNotNull(tools);
        Assert.AreEqual(1, tools.Total);
        Assert.AreEqual(1, tools.Items.Count);

        Assert.AreEqual("the output", await harness.Service.OutputAsync("run-1"));
    }

    [TestMethod]
    public void EventProjectionHandlesJsonElementsAndPlainObjects()
    {
        var element = JsonDocument.Parse("""
            {"eventId":"e-1","eventType":"subagent.round.completed","timestamp":"2026-09-27T00:00:00Z","payload":{"k":"v"}}
            """).RootElement;
        var dto = SubAgentRunQueryService.ToEventDto(element);
        Assert.AreEqual("e-1", dto.EventId);
        Assert.AreEqual("subagent.round.completed", dto.EventType);
        Assert.IsNotNull(dto.Payload);
        Assert.IsTrue(dto.PayloadSize > 0);

        // 下划线命名的事件也要能读出 id/type/timestamp。
        var underscored = JsonDocument.Parse("""
            {"event_id":"e-2","type":"subagent.tool","recordedAt":"2026-09-27T00:00:01Z"}
            """).RootElement;
        var dto2 = SubAgentRunQueryService.ToEventDto(underscored);
        Assert.AreEqual("e-2", dto2.EventId);
        Assert.AreEqual("subagent.tool", dto2.EventType);
        Assert.AreEqual("2026-09-27T00:00:01Z", dto2.Timestamp);
        Assert.IsNull(dto2.Payload);

        // 既不是 JsonElement 也不是预期形状时给出 unknown，而不是抛异常。
        var dto3 = SubAgentRunQueryService.ToEventDto(new { });
        Assert.AreEqual("unknown", dto3.EventType);
        Assert.AreEqual(string.Empty, dto3.EventId);
    }

    private sealed class Harness(PlatformDbContext db, SqliteConnection connection, SubAgentRunQueryService service)
        : IAsyncDisposable
    {
        public SubAgentRunQueryService Service => service;

        public static async Task<Harness> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();

            db.SubAgentRuns.AddRange(
                Run("run-1", "parent-a", "ws-a", "agent-1", "failed", "2026-09-27T00:00:00Z"),
                Run("run-2", "parent-a", "ws-a", "agent-2", "succeeded", "2026-09-27T00:00:01Z"),
                Run("run-3", "parent-b", "ws-b", "agent-1", "running", "2026-09-27T00:00:02Z"));
            await db.SaveChangesAsync();

            var store = new StubRunStore();
            return new Harness(db, connection, new SubAgentRunQueryService(new SingleContextFactory(connection), store));
        }

        private static SubAgentRunEntity Run(
            string runId, string parentSessionId, string workspaceId, string agentInstanceId,
            string status, string startedAt) => new()
        {
            RunId = runId,
            ParentSessionId = parentSessionId,
            SubSessionId = $"sub-{runId}",
            WorkspaceId = workspaceId,
            AgentInstanceId = agentInstanceId,
            TemplateId = "global:composition",
            Status = status,
            StartedAt = startedAt,
            CompletedAt = startedAt,
        };

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }

        /// <summary>
        /// A fresh context per call, like the host's pooled factory: the query services dispose what they
        /// create, so handing out a shared instance would dispose the fixture's own context.
        /// </summary>
        private sealed class SingleContextFactory(SqliteConnection connection) : IDbContextFactory<PlatformDbContext>
        {
            public PlatformDbContext CreateDbContext() => new(
                new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options);

            public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
                Task.FromResult(CreateDbContext());
        }

        /// <summary>One archived run whose manifest omits the totals, so the archive fallback is exercised.</summary>
        private sealed class StubRunStore : ISubAgentRunStore
        {
            public Task<SubAgentRunArchive?> GetRunArchiveAsync(string runId, CancellationToken ct = default)
            {
                if (runId != "run-1") return Task.FromResult<SubAgentRunArchive?>(null);
                var events = new List<object>
                {
                    JsonDocument.Parse("""
                        {"eventId":"e-1","eventType":"subagent.round.completed","timestamp":"2026-09-27T00:00:00Z","payload":{"round":1}}
                        """).RootElement,
                    JsonDocument.Parse("""
                        {"eventId":"e-2","eventType":"subagent.round.started","timestamp":"2026-09-27T00:00:01Z"}
                        """).RootElement,
                };
                return Task.FromResult<SubAgentRunArchive?>(new SubAgentRunArchive
                {
                    Manifest = new SubAgentRunManifest
                    {
                        RunId = "run-1",
                        ParentSessionId = "parent-a",
                        SubSessionId = "sub-run-1",
                        WorkspaceId = "ws-a",
                        AgentInstanceId = "agent-1",
                        TemplateId = "global:composition",
                        Status = "failed",
                        StartedAt = DateTimeOffset.UtcNow.AddSeconds(-2),
                        CompletedAt = null,
                        Task = "the task",
                        ErrorMessage = "boom",
                    },
                    Events = events,
                    Tools =
                [
                    new SubAgentToolAuditEntry
                    {
                        ToolCallId = "call-1", ToolName = "read_file", ArgsHash = "hash-1",
                        Success = true, DurationMs = 12,
                    },
                ],
                    Output = "the output",
                    Degraded = new SubAgentArchiveDegradedInfo
                    {
                        DroppedEventCount = 3, LastEventType = "subagent.round.completed",
                        LastError = "disk busy", FirstFailureAt = "2026-09-27T00:00:00Z",
                        LastFailureAt = "2026-09-27T00:00:02Z",
                    },
                });
            }

            public Task<SubAgentRunHandle> CreateRunAsync(SubAgentRunCreateRequest request, CancellationToken ct = default)
                => throw new NotSupportedException();
            public Task AppendEventAsync(string runId, string eventType, object payload, CancellationToken ct = default)
                => throw new NotSupportedException();
            public Task AppendToolAuditAsync(string runId, SubAgentToolAuditEntry entry, CancellationToken ct = default)
                => throw new NotSupportedException();
            public Task<SubAgentRunTerminalWriteResult> CompleteRunAsync(string runId, SubAgentRunCompletion completion, CancellationToken ct = default)
                => throw new NotSupportedException();
            public Task<int> GetRunningCountByParentTurnAsync(string? parentTurnId, CancellationToken ct = default)
                => throw new NotSupportedException();
            public Task<int> RecoverInterruptedRunsAsync(DateTimeOffset olderThan, int maxRuns, CancellationToken ct = default)
                => throw new NotSupportedException();
            public Task<int> ReplayPendingConversationEventsAsync(int maxRuns, CancellationToken ct = default)
                => throw new NotSupportedException();
            public Task<bool> DeleteRunAsync(string runId, CancellationToken ct = default)
                => throw new NotSupportedException();
        }
    }
}
