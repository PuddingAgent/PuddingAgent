using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Services;

namespace PuddingMemoryEngineTests;

/// <summary>
/// 新版事实优先记忆库的核心行为测试。
/// </summary>
[TestClass]
public sealed class FactMemoryServiceTests
{
    [TestMethod]
    public void ComputeFreshness_Exponential_ShouldDecayByHalfLife()
    {
        var now = DateTimeOffset.Parse("2026-06-04T12:00:00Z");
        var freshness = new FactFreshnessInput
        {
            ObservedAt = now.AddHours(-2),
            HalfLifeSeconds = 3600,
            DecayKind = FactFreshnessDecayKind.Exponential,
            StaleThreshold = 0.5,
            ExpiredThreshold = 0.1,
        };

        var result = FactFreshnessCalculator.Compute(now, freshness);

        Assert.AreEqual(0.25, result.Score, 0.0001);
        Assert.AreEqual(FactFreshnessStatus.Stale, result.Status);
    }

    [TestMethod]
    public void MatchContext_RequiredKeyConflict_ShouldReturnZero()
    {
        var factContext = new Dictionary<string, string?>
        {
            ["product"] = "mysql",
            ["os"] = "windows",
        };
        var queryContext = new Dictionary<string, string?>
        {
            ["product"] = "sqlserver",
            ["os"] = "windows",
        };

        var result = FactContextMatcher.Match(
            queryContext,
            factContext,
            new FactContextMatchOptions
            {
                RequiredKeys = ["product"],
                WeightedKeys = new Dictionary<string, double> { ["os"] = 1.0 },
            });

        Assert.AreEqual(0.0, result.Score);
        Assert.IsTrue(result.ConflictingKeys.Contains("product"));
    }

    [TestMethod]
    public async Task CreateFactAsync_ShouldPersistEvidenceFreshnessContextAndRevision()
    {
        await using var scope = await CreateScopeAsync();
        var service = new FactMemoryService(scope.Factory);

        var space = await service.EnsureDefaultMemorySpaceAsync("ws-fact", "agent-a");
        var fact = await service.CreateFactAsync("ws-fact", "agent-a", space.MemorySpaceId, new CreateFactRequest
        {
            Statement = "用户当前项目使用 MySQL。",
            FactType = "project_database",
            Confidence = 0.82,
            Status = MemoryFactStatus.Accepted,
            ContextJson = """{"project":"PuddingAgent","product":"mysql"}""",
            Freshness = new CreateFactFreshnessRequest
            {
                ObservedAt = DateTimeOffset.Parse("2026-06-04T12:00:00Z"),
                HalfLifeSeconds = 60 * 60 * 24 * 30,
                DecayKind = FactFreshnessDecayKind.Exponential,
                FreshnessReason = "项目技术栈通常按月变化。",
            },
            Evidence =
            [
                new CreateFactEvidenceRequest
                {
                    SourceType = "session_message",
                    SourceId = "session-1",
                    SourceRange = "message:3",
                    QuoteSummary = "用户说明当前项目使用 MySQL。",
                    Confidence = 0.9,
                }
            ],
            EntityMentions =
            [
                new CreateFactEntityMentionRequest
                {
                    EntityKey = "project:puddingagent",
                    EntityType = "project",
                    DisplayName = "PuddingAgent",
                    Role = "subject",
                    Confidence = 0.9,
                },
                new CreateFactEntityMentionRequest
                {
                    EntityKey = "software:mysql",
                    EntityType = "software",
                    DisplayName = "MySQL",
                    Role = "object",
                    Confidence = 0.9,
                }
            ],
            Associations =
            [
                new CreateFactAssociationRequest
                {
                    SourceKind = "entity",
                    SourceKey = "project:puddingagent",
                    TargetKind = "entity",
                    TargetKey = "software:mysql",
                    AssociationType = "uses",
                    Weight = 0.8,
                    Confidence = 0.9,
                    Reason = "用户在会话中明确说明项目数据库。",
                }
            ],
            ActorType = "conscious_llm",
            ActorId = "agent-a",
        });

        await using var db = await scope.Factory.CreateDbContextAsync();
        Assert.AreEqual("ws-fact", fact.WorkspaceId);
        Assert.AreEqual("agent-a", fact.AgentId);
        Assert.AreEqual(MemoryFactStatus.Accepted, fact.Status);
        Assert.AreEqual(1, await db.MemoryFactEvidence.CountAsync(e => e.FactId == fact.FactId));
        Assert.AreEqual(1, await db.MemoryFactFreshness.CountAsync(f => f.FactId == fact.FactId));
        Assert.AreEqual(1, await db.MemoryFactContexts.CountAsync(c => c.FactId == fact.FactId));
        Assert.AreEqual(2, await db.MemoryFactEntityMentions.CountAsync(m => m.FactId == fact.FactId));
        Assert.AreEqual(1, await db.MemoryFactAssociations.CountAsync(a => a.FactId == fact.FactId));
        Assert.AreEqual(1, await db.MemoryFactRevisions.CountAsync(r => r.FactId == fact.FactId && r.RevisionType == "create"));
    }

    [TestMethod]
    public async Task CreateFactAsync_WithoutEvidence_ShouldReject()
    {
        await using var scope = await CreateScopeAsync();
        var service = new FactMemoryService(scope.Factory);
        var space = await service.EnsureDefaultMemorySpaceAsync("ws-fact", "agent-a");

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            service.CreateFactAsync("ws-fact", "agent-a", space.MemorySpaceId, new CreateFactRequest
            {
                Statement = "没有证据的事实。",
                FactType = "test",
                Confidence = 0.5,
                ActorType = "conscious_llm",
                ActorId = "agent-a",
            }));
    }

    [TestMethod]
    public async Task MemoryLibraryDbInitializer_ShouldCreateFactMemoryTables()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"pudding-fact-memory-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<MemoryLibraryDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            var factory = new FactMemoryTestDbContextFactory(options);

            await MemoryLibraryDbInitializer.InitializeAsync(factory);

            await using var db = factory.CreateDbContext();
            Assert.IsTrue(await TableExistsAsync(db, "MemorySpaces"));
            Assert.IsTrue(await TableExistsAsync(db, "MemoryFacts"));
            Assert.IsTrue(await TableExistsAsync(db, "MemoryFactEvidence"));
            Assert.IsTrue(await TableExistsAsync(db, "MemoryFactContexts"));
            Assert.IsTrue(await TableExistsAsync(db, "MemoryFactFreshness"));
            Assert.IsTrue(await TableExistsAsync(db, "MemoryFactEntityMentions"));
            Assert.IsTrue(await TableExistsAsync(db, "MemoryFactAssociations"));
            Assert.IsTrue(await TableExistsAsync(db, "MemoryFactRevisions"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    private static async Task<FactMemoryTestScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MemoryLibraryDbContext>()
            .UseSqlite(connection)
            .EnableSensitiveDataLogging()
            .Options;

        var factory = new FactMemoryTestDbContextFactory(options);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new FactMemoryTestScope(connection, factory);
    }

    private static async Task<bool> TableExistsAsync(MemoryLibraryDbContext db, string tableName)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$name";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);

        return await command.ExecuteScalarAsync() is not null;
    }

    private sealed class FactMemoryTestScope(SqliteConnection connection, IDbContextFactory<MemoryLibraryDbContext> factory) : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = connection;

        public IDbContextFactory<MemoryLibraryDbContext> Factory { get; } = factory;

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }

    private sealed class FactMemoryTestDbContextFactory(DbContextOptions<MemoryLibraryDbContext> options)
        : IDbContextFactory<MemoryLibraryDbContext>
    {
        public MemoryLibraryDbContext CreateDbContext() => new(options);
    }
}
