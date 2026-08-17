using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class SubAgentTransientDirectoryGcServiceTests
{
    [TestMethod]
    public async Task Sweep_QuarantinesOnlyOldTerminalEmptyScaffold_ThenPurgesAfterRetention()
    {
        using var temp = new TempDataRoot();
        var now = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
        const string subSessionId = "parent-session-sub-1234abcd";
        CreateEmptySkillScaffold(temp.Paths, subSessionId, now.AddDays(-2));
        await temp.AddRunAsync(subSessionId, "completed", now.AddDays(-2), now.AddDays(-2));
        var service = temp.CreateService();

        var first = await service.SweepOnceAsync(now);

        Assert.AreEqual(1, first.Quarantined);
        Assert.IsFalse(Directory.Exists(temp.Paths.AgentInstanceRoot(subSessionId)));
        var quarantined = Directory.GetDirectories(temp.Paths.SubAgentTransientDirectoryQuarantineRoot);
        Assert.HasCount(1, quarantined);
        Assert.IsTrue(File.Exists(Path.Combine(quarantined[0], "gc.json")));

        var second = await service.SweepOnceAsync(now.AddDays(8));

        Assert.AreEqual(1, second.Purged);
        Assert.IsFalse(Directory.EnumerateDirectories(
            temp.Paths.SubAgentTransientDirectoryQuarantineRoot).Any());
    }

    [TestMethod]
    public async Task Sweep_PreservesStatefulAndNonTerminalDirectories()
    {
        using var temp = new TempDataRoot();
        var now = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
        const string statefulId = "parent-session-sub-1111aaaa";
        const string runningId = "parent-session-sub-2222bbbb";
        CreateEmptySkillScaffold(temp.Paths, statefulId, now.AddDays(-2));
        await File.WriteAllTextAsync(
            Path.Combine(temp.Paths.AgentInstanceRoot(statefulId), "goal.md"),
            "durable child state");
        CreateEmptySkillScaffold(temp.Paths, runningId, now.AddDays(-2));
        await temp.AddRunAsync(statefulId, "completed", now.AddDays(-2), now.AddDays(-2));
        await temp.AddRunAsync(runningId, "running", now.AddDays(-2), completedAt: null);
        var service = temp.CreateService();

        var result = await service.SweepOnceAsync(now);

        Assert.AreEqual(0, result.Quarantined);
        Assert.AreEqual(1, result.SkippedStatefulOrUnknown);
        Assert.AreEqual(1, result.SkippedNonTerminal);
        Assert.IsTrue(Directory.Exists(temp.Paths.AgentInstanceRoot(statefulId)));
        Assert.IsTrue(Directory.Exists(temp.Paths.AgentInstanceRoot(runningId)));
    }

    [TestMethod]
    public async Task Sweep_PreservesRecentTerminalScaffold()
    {
        using var temp = new TempDataRoot();
        var now = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
        const string subSessionId = "parent-session-sub-3333cccc";
        CreateEmptySkillScaffold(temp.Paths, subSessionId, now.AddHours(-2));
        await temp.AddRunAsync(subSessionId, "completed", now.AddHours(-2), now.AddHours(-1));

        var result = await temp.CreateService().SweepOnceAsync(now);

        Assert.AreEqual(0, result.Quarantined);
        Assert.AreEqual(1, result.SkippedRecent);
        Assert.IsTrue(Directory.Exists(temp.Paths.AgentInstanceRoot(subSessionId)));
    }

    [TestMethod]
    public async Task Sweep_PreservesPooledSubSessionEvenWhenLatestRunIsOldAndTerminal()
    {
        using var temp = new TempDataRoot();
        var now = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
        const string subSessionId = "parent-session-sub-4444dddd";
        CreateEmptySkillScaffold(temp.Paths, subSessionId, now.AddDays(-2));
        await temp.AddRunAsync(subSessionId, "completed", now.AddDays(-2), now.AddDays(-2));

        var result = await temp.CreateService([subSessionId]).SweepOnceAsync(now);

        Assert.AreEqual(0, result.Quarantined);
        Assert.AreEqual(1, result.SkippedPooled);
        Assert.IsTrue(Directory.Exists(temp.Paths.AgentInstanceRoot(subSessionId)));
    }

    private static void CreateEmptySkillScaffold(
        PuddingDataPaths paths,
        string subSessionId,
        DateTimeOffset lastWriteAt)
    {
        var root = paths.AgentInstanceRoot(subSessionId);
        var skillsRoot = Path.Combine(root, "skills");
        Directory.CreateDirectory(skillsRoot);
        File.WriteAllText(
            Path.Combine(skillsRoot, "index.json"),
            JsonSerializer.Serialize(new
            {
                agentInstanceId = subSessionId,
                generatedAt = lastWriteAt,
                skills = Array.Empty<object>(),
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Directory.SetLastWriteTimeUtc(skillsRoot, lastWriteAt.UtcDateTime);
        Directory.SetLastWriteTimeUtc(root, lastWriteAt.UtcDateTime);
    }

    private sealed class TempDataRoot : IDisposable
    {
        private readonly string _databasePath;
        private readonly DbContextOptions<PlatformDbContext> _dbOptions;

        public TempDataRoot()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "pudding-subagent-directory-gc-tests",
                Guid.NewGuid().ToString("N"));
            Paths = PuddingDataPaths.FromRoot(Root);
            Directory.CreateDirectory(Paths.DatabasesRoot);
            _databasePath = Path.Combine(Paths.DatabasesRoot, "platform.db");
            _dbOptions = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={_databasePath}")
                .Options;
            using var db = new PlatformDbContext(_dbOptions);
            db.Database.EnsureCreated();
        }

        public string Root { get; }
        public PuddingDataPaths Paths { get; }

        public async Task AddRunAsync(
            string subSessionId,
            string status,
            DateTimeOffset startedAt,
            DateTimeOffset? completedAt)
        {
            await using var db = new PlatformDbContext(_dbOptions);
            db.SubAgentRuns.Add(new SubAgentRunEntity
            {
                RunId = "run-" + Guid.NewGuid().ToString("N"),
                ParentSessionId = "parent-session",
                SubSessionId = subSessionId,
                WorkspaceId = "default",
                AgentInstanceId = "persistent-agent",
                TemplateId = "researcher",
                Status = status,
                StartedAt = startedAt.ToString("O"),
                CompletedAt = completedAt?.ToString("O"),
                ArchivePath = Path.Combine(Root, "runs", Guid.NewGuid().ToString("N")),
            });
            await db.SaveChangesAsync();
        }

        public SubAgentTransientDirectoryGcService CreateService(
            IReadOnlyCollection<string>? pooledSubSessionIds = null) => new(
            Paths,
            new TestDbContextFactory(_dbOptions),
            new TestSubAgentPool(pooledSubSessionIds ?? []),
            new FixedRuntimeExecutionConfigService(),
            NullLogger<SubAgentTransientDirectoryGcService>.Instance);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class TestSubAgentPool(IReadOnlyCollection<string> subSessionIds) : ISubAgentPool
    {
        public int Count => subSessionIds.Count;
        public int MaxCapacity => 100;

        public IReadOnlyList<PooledSubAgent> List() => subSessionIds
            .Select((id, index) => new PooledSubAgent
            {
                Name = $"pooled-{index}",
                SubSessionId = id,
                TemplateId = "researcher",
                CreatedAt = DateTimeOffset.UtcNow,
                LastUsedAt = DateTimeOffset.UtcNow,
                Status = PooledSubAgentStatus.Sleeping,
                TaskCount = 1,
            })
            .ToArray();

        public Task<PooledSubAgent> CreateAsync(
            string name,
            SubAgentSpawnRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PooledSubAgent?> GetAsync(string name, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SubAgentExecuteResult> ExecuteAsync(
            string name,
            SubAgentSpawnRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> SleepAsync(string name, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> DestroyAsync(string name, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string?> EvictLeastRecentlyUsedAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FixedRuntimeExecutionConfigService : IRuntimeExecutionConfigService
    {
        public RuntimeExecutionOptions GetOptions() => new()
        {
            SubAgents = new SubAgentExecutionOptions
            {
                TransientDirectoryRetention = new SubAgentTransientDirectoryRetentionOptions
                {
                    Enabled = true,
                    ScanIntervalMinutes = 360,
                    ScaffoldRetentionHours = 24,
                    OrphanRetentionHours = 168,
                    QuarantineRetentionDays = 7,
                    MaxItemsPerSweep = 200,
                },
            },
        };
    }
}
