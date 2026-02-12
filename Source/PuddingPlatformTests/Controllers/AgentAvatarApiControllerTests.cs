using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Controllers;

[TestClass]
public sealed class AgentAvatarApiControllerTests
{
    private static readonly string[] ExpectedGeneratedAvatarIds =
    [
        "neutral",
        "smile",
        "sleepy",
        "thinking",
        "angry",
        "silver",
        "amber",
        "mint",
    ];

    [TestMethod]
    public async Task List_ShouldReturnDefaultAvatar_WhenDatabaseHasNoSeedRows()
    {
        using var temp = TemporaryDirectory.Create();
        var options = await CreateDatabaseAsync(temp.Path);
        await using var db = new PlatformDbContext(options);
        var catalog = new AgentAvatarCatalog(
            new TestDbContextFactory(options),
            NullLogger<AgentAvatarCatalog>.Instance);
        var controller = new AgentAvatarApiController(db, catalog);

        var result = await controller.List(enabledOnly: true, CancellationToken.None);

        var ok = Assert.IsInstanceOfType<OkObjectResult>(result.Result);
        var avatars = Assert.IsInstanceOfType<List<AgentAvatarDto>>(ok.Value);
        Assert.HasCount(1, avatars);
        Assert.AreEqual("default", avatars[0].AvatarId);
        Assert.AreEqual("/assets/agent-avatars/agent-avatar-default.png", avatars[0].Url);
    }

    [TestMethod]
    public async Task PackagedAvatarManifest_ShouldSeedGeneratedAvatarCatalog()
    {
        using var temp = TemporaryDirectory.Create();
        var options = await CreateDatabaseAsync(temp.Path);
        var manifestDir = FindPackagedAvatarManifestDirectory();
        var seed = new AgentAvatarSeedService(
            new TestDbContextFactory(options),
            NullLogger<AgentAvatarSeedService>.Instance);

        await seed.SeedAsync(manifestDir);

        await using var db = new PlatformDbContext(options);
        var avatars = await db.AgentAvatars
            .AsNoTracking()
            .OrderBy(a => a.SortOrder)
            .ToListAsync();
        var ids = avatars.Select(a => a.AvatarId).ToArray();

        foreach (var expectedId in ExpectedGeneratedAvatarIds)
            CollectionAssert.Contains(ids, expectedId);

        foreach (var avatar in avatars.Where(a => ExpectedGeneratedAvatarIds.Contains(a.AvatarId)))
            Assert.IsTrue(
                File.Exists(Path.Combine(manifestDir, avatar.FileName)),
                $"Missing packaged avatar image for {avatar.AvatarId}: {avatar.FileName}");
    }

    private static async Task<DbContextOptions<PlatformDbContext>> CreateDatabaseAsync(string root)
    {
        var dbPath = Path.Combine(root, "platform.db");
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return options;
    }

    private static string FindPackagedAvatarManifestDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "Source",
                "PuddingAgent",
                "default-data",
                "assets",
                "agent-avatars");
            if (File.Exists(Path.Combine(candidate, "avatars.json")))
                return candidate;

            current = current.Parent;
        }

        Assert.Fail("Could not locate Source/PuddingAgent/default-data/assets/agent-avatars/avatars.json");
        throw new InvalidOperationException("unreachable");
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-agent-avatar-api-tests",
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
