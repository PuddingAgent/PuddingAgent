using System.Text.Json;
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
/// 2026-11-16：完成时从 events.jsonl 回算 usage 与 LLM 分位并写入 run.json。
/// 这条路径此前只有读取侧（SubAgentLatencyAggregator）而没有写入侧，
/// 导致跨 provider 的成本核对只能去翻子代理记录、口径还不一致。
/// </summary>
[TestClass]
public sealed class SubAgentRunStoreUsageTests
{
    [TestMethod]
    public async Task CompleteRunAsync_Carries_Recomputed_Usage_Into_RunJson()
    {
        using var temp = TemporaryDirectory.Create();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var dbPath = Path.Combine(temp.Path, "platform.db");
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var store = new FileSubAgentRunStore(
            paths,
            NullLogger<FileSubAgentRunStore>.Instance,
            new TestDbContextFactory(options),
            new RecordingConversationEventStore());

        var handle = await store.CreateRunAsync(new SubAgentRunCreateRequest
        {
            ParentSessionId = "parent-session",
            SubSessionId = "parent-session/sub/usage",
            WorkspaceId = "default",
            AgentInstanceId = "default.researcher-001",
            TemplateId = "researcher",
            Task = "usage probe",
        });

        // 两条 LLM 调用：2 s 与 10 s。最近秩分位下 P50 = 2000（第 1 位）、P95 = 10000（第 2 位）；
        // 两个值不同，避免「分位退化成最大值」也能通过。
        // 缓存：命中 80 + 未命中 70 ⇒ 命中率按 hit/(hit+miss) = 53.3（用 total 作分母会得到 48.5）。
        var events = new[]
        {
            "{\"eventId\":\"e1\",\"eventType\":\"subagent.llm.started\",\"timestamp\":\"2026-11-16T00:00:00.000+00:00\",\"payload\":{}}",
            "{\"eventId\":\"e2\",\"eventType\":\"subagent.llm.completed\",\"timestamp\":\"2026-11-16T00:00:02.000+00:00\",\"payload\":{\"prompt_tokens\":100,\"completion_tokens\":10,\"cache_hit_tokens\":80,\"cache_miss_tokens\":20,\"total_tokens\":110}}",
            "{\"eventId\":\"e3\",\"eventType\":\"subagent.llm.started\",\"timestamp\":\"2026-11-16T00:00:10.000+00:00\",\"payload\":{}}",
            "{\"eventId\":\"e4\",\"eventType\":\"subagent.llm.completed\",\"timestamp\":\"2026-11-16T00:00:20.000+00:00\",\"payload\":{\"prompt_tokens\":50,\"completion_tokens\":5,\"cache_hit_tokens\":0,\"cache_miss_tokens\":50,\"total_tokens\":55}}",
        };
        await File.AppendAllLinesAsync(
            Path.Combine(handle.ArchivePath, "events.jsonl"),
            events,
            new System.Text.UTF8Encoding(false));

        var applied = await store.CompleteRunAsync(handle.RunId, new SubAgentRunCompletion
        {
            Status = "completed",
            Output = "done",
        });
        Assert.AreEqual(SubAgentRunTerminalWriteResult.Applied, applied);

        var manifest = JsonSerializer.Deserialize<SubAgentRunManifest>(
            await File.ReadAllTextAsync(Path.Combine(handle.ArchivePath, "run.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.IsNotNull(manifest, "run.json 必须可反序列化为 manifest");
        Assert.AreEqual(2, manifest!.LlmCallCount);
        Assert.AreEqual(10000L, manifest.LlmMaxMs);
        Assert.AreEqual(2000L, manifest.LlmP50Ms);
        Assert.AreEqual(10000L, manifest.LlmP95Ms);
        Assert.AreEqual(150L, manifest.PromptTokens);
        Assert.AreEqual(15L, manifest.CompletionTokens);
        Assert.AreEqual(165L, manifest.TotalTokens);
        Assert.AreEqual(80L, manifest.CacheHitTokens);
        Assert.AreEqual(70L, manifest.CacheMissTokens);
        Assert.AreEqual(53.3, manifest.CacheHitPct!.Value, 0.05);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
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
                "pudding-platform-tests",
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
