using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.SubAgents;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// P0 子代理终态事务 Phase 2：终态提交竞态（TryCompleteSubAgentRunAsync 的幂等语义由
/// FileSubAgentRunStore 承载——Runtime 与 Manager 异常边界都可能提交，唯一性必须由
/// 持久层裁决）。覆盖三种竞态形态：
///   ① 并发完成/取消：first-writer-wins，恰好一个 Applied + 单条终态投影；
///   ② 完成 vs 重启恢复扫描：已终态的 run 不被恢复改写为 interrupted；
///   ③ 恢复标记 interrupted 后的迟到完成：AlreadyTerminal，不再补发第二条终态事件。
/// </summary>
[TestClass]
public sealed class SubAgentRunTerminalRaceTests
{
    private static readonly HashSet<string> TerminalEventTypes = new(StringComparer.Ordinal)
    {
        ConversationEventTypes.SubAgentRunCompleted,
        ConversationEventTypes.SubAgentRunFailed,
        ConversationEventTypes.SubAgentRunCancelled,
        ConversationEventTypes.SubAgentRunTimedOut,
        ConversationEventTypes.SubAgentRunBudgetExhausted,
        ConversationEventTypes.SubAgentRunInterrupted,
    };

    [TestMethod]
    public async Task CompleteRunAsync_ConcurrentCancelAndComplete_FirstWriterWins()
    {
        using var harness = await Harness.CreateAsync();
        var handle = await harness.CreateRunAsync("Concurrent terminal race");

        var completionTask = harness.Store.CompleteRunAsync(handle.RunId, new SubAgentRunCompletion
        {
            Status = "completed",
            Output = "first writer reply",
        });
        var cancellationTask = harness.Store.CompleteRunAsync(handle.RunId, new SubAgentRunCompletion
        {
            Status = "cancelled",
            ErrorMessage = "racing cancel request",
        });
        var results = await Task.WhenAll(completionTask, cancellationTask);

        // 恰好一个 Applied + 一个 AlreadyTerminal——并发提交由 per-run gate 串行化裁决。
        Assert.IsTrue(results.Contains(SubAgentRunTerminalWriteResult.Applied));
        Assert.IsTrue(results.Contains(SubAgentRunTerminalWriteResult.AlreadyTerminal));
        Assert.AreEqual(1, results.Count(r => r == SubAgentRunTerminalWriteResult.Applied));

        var appliedStatus = completionTask.Result == SubAgentRunTerminalWriteResult.Applied
            ? "completed"
            : "cancelled";
        await using var verifyDb = harness.CreateVerifyDb();
        var index = await verifyDb.SubAgentRuns.SingleAsync(r => r.RunId == handle.RunId);
        Assert.AreEqual(appliedStatus, index.Status);

        // 终态会话投影恰好一条：败者直接短路，不产生第二条终态事件。
        var terminalEvents = harness.ConversationEvents.Appended
            .Where(item => TerminalEventTypes.Contains(item.Event.Type))
            .ToList();
        Assert.HasCount(1, terminalEvents);
        Assert.AreEqual(
            appliedStatus == "completed"
                ? ConversationEventTypes.SubAgentRunCompleted
                : ConversationEventTypes.SubAgentRunCancelled,
            terminalEvents[0].Event.Type);
    }

    [TestMethod]
    public async Task RecoverInterruptedRunsAsync_SkipsRunCompletedJustBeforeRecovery()
    {
        using var harness = await Harness.CreateAsync();
        var handle = await harness.CreateRunAsync("Complete wins over recovery scan");

        var applied = await harness.Store.CompleteRunAsync(handle.RunId, new SubAgentRunCompletion
        {
            Status = "completed",
            Output = "terminal before restart",
        });
        Assert.AreEqual(SubAgentRunTerminalWriteResult.Applied, applied);

        // 完成后进程重启、恢复扫描启动：已终态 run 不得被改写为 interrupted。
        var recovered = await harness.Store.RecoverInterruptedRunsAsync(
            DateTimeOffset.UtcNow.AddSeconds(1),
            maxRuns: 100);

        Assert.AreEqual(0, recovered);
        await using var verifyDb = harness.CreateVerifyDb();
        var index = await verifyDb.SubAgentRuns.SingleAsync(r => r.RunId == handle.RunId);
        Assert.AreEqual("completed", index.Status);
        Assert.IsFalse(harness.ConversationEvents.Appended.Any(item =>
            item.Event.Type == ConversationEventTypes.SubAgentRunInterrupted));
    }

    [TestMethod]
    public async Task CompleteRunAsync_AfterInterruptedRecovery_ReturnsAlreadyTerminal()
    {
        using var harness = await Harness.CreateAsync();
        var handle = await harness.CreateRunAsync("Late completion after recovery");

        var recovered = await harness.Store.RecoverInterruptedRunsAsync(
            DateTimeOffset.UtcNow.AddSeconds(1),
            maxRuns: 100);
        Assert.AreEqual(1, recovered);

        // 迟到的 Runtime 终态提交：AlreadyTerminal，终态不被覆盖、不补发第二条终态事件。
        var late = await harness.Store.CompleteRunAsync(handle.RunId, new SubAgentRunCompletion
        {
            Status = "failed",
            ErrorMessage = "late terminal write after restart",
        });

        Assert.AreEqual(SubAgentRunTerminalWriteResult.AlreadyTerminal, late);
        await using var verifyDb = harness.CreateVerifyDb();
        var index = await verifyDb.SubAgentRuns.SingleAsync(r => r.RunId == handle.RunId);
        Assert.AreEqual("interrupted", index.Status);

        var terminalEvents = harness.ConversationEvents.Appended
            .Where(item => TerminalEventTypes.Contains(item.Event.Type))
            .ToList();
        Assert.HasCount(1, terminalEvents);
        Assert.AreEqual(ConversationEventTypes.SubAgentRunInterrupted, terminalEvents[0].Event.Type);
    }

    // ─────────────────────────────── 测试基建 ───────────────────────────────

    private sealed class Harness : IDisposable
    {
        private TemporaryDirectory _temp = null!;
        private DbContextOptions<PlatformDbContext> _options = null!;

        public FileSubAgentRunStore Store { get; private set; } = null!;

        public RecordingConversationEventStore ConversationEvents { get; } = new();

        public static async Task<Harness> CreateAsync()
        {
            var harness = new Harness
            {
                _temp = TemporaryDirectory.Create(),
            };
            harness._options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={Path.Combine(harness._temp.Path, "platform.db")}")
                .Options;
            await using (var db = new PlatformDbContext(harness._options))
            {
                await db.Database.EnsureCreatedAsync();
            }

            harness.Store = new FileSubAgentRunStore(
                PuddingDataPaths.FromRoot(harness._temp.Path),
                NullLogger<FileSubAgentRunStore>.Instance,
                new TestDbContextFactory(harness._options),
                harness.ConversationEvents);
            return harness;
        }

        public async Task<SubAgentRunHandle> CreateRunAsync(string task)
            => await Store.CreateRunAsync(new SubAgentRunCreateRequest
            {
                ParentSessionId = "parent-session",
                SubSessionId = "sub-session-race",
                WorkspaceId = "default",
                AgentInstanceId = "agent-1",
                TemplateId = "workspace-task-agent",
                Task = task,
            });

        public PlatformDbContext CreateVerifyDb() => new(_options);

        public void Dispose()
        {
            _temp.Dispose();
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class RecordingConversationEventStore : IConversationEventStore
    {
        public List<(string ConversationId, NewConversationEvent Event)> Appended { get; } = [];

        public Task<AppendResult> AppendAsync(
            string conversationId,
            long expectedVersion,
            IReadOnlyList<NewConversationEvent> events,
            EventWriteCondition condition,
            CancellationToken ct)
        {
            foreach (var item in events)
            {
                if (Appended.All(existing => existing.Event.EventId != item.EventId))
                    Appended.Add((conversationId, item));
            }

            var last = Appended.Count;
            return Task.FromResult(new AppendResult(last, last, events.Count));
        }

        public Task<EventPage> ReadForwardAsync(
            string conversationId,
            long afterExclusive,
            long? throughInclusive,
            int limit,
            CancellationToken ct) =>
            Task.FromResult(new EventPage([], null, false));

        public Task<EventPage> ReadBackwardAsync(
            string conversationId,
            long beforeExclusive,
            int limit,
            CancellationToken ct) =>
            Task.FromResult(new EventPage([], null, false));

        public Task<EventPage> ReadByTypePrefixBackwardAsync(
            string conversationId,
            string typePrefix,
            long beforeExclusive,
            int limit,
            CancellationToken ct) =>
            Task.FromResult(new EventPage([], null, false));

        public Task<EventBounds> GetBoundsAsync(
            string conversationId,
            CancellationToken ct) =>
            Task.FromResult(new EventBounds(null, null));

        public Task EnsureTablesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-terminal-race-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
