using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class SessionSteeringServiceTests
{
    [TestMethod]
    public async Task ConsumeNextAsync_ReturnsPendingMessageOnce()
    {
        await using var scope = await CreateScopeAsync();
        var service = new SessionSteeringService(
            scope.Factory,
            NullLogger<SessionSteeringService>.Instance);

        var created = await service.CreateAsync(new CreateSessionSteeringMessage(
            WorkspaceId: "default",
            SessionId: "session-1",
            AgentId: "agent-1",
            MessageText: "请改为先检查错误日志。",
            SourceQueueItemId: "queue-1",
            CreatedBy: "admin",
            Priority: 1000));

        var consumed = await service.ConsumeNextAsync("session-1", "agent-1", 2);
        var consumedAgain = await service.ConsumeNextAsync("session-1", "agent-1", 3);

        Assert.IsNotNull(consumed);
        Assert.AreEqual(created.SteeringId, consumed.SteeringId);
        Assert.AreEqual("请改为先检查错误日志。", consumed.MessageText);
        Assert.AreEqual(2, consumed.Round);
        Assert.IsNull(consumedAgain);
    }

    private static async Task<TestScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }
        return new TestScope(connection, new TestDbContextFactory(options));
    }

    private sealed record TestScope(
        SqliteConnection Connection,
        IDbContextFactory<PlatformDbContext> Factory) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
