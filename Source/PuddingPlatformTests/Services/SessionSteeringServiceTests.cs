using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
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
            TargetTurnId: "turn-1",
            AgentId: "agent-1",
            MessageText: "请改为先检查错误日志。",
            SourceQueueItemId: "queue-1",
            CreatedBy: "admin",
            Priority: 1000));

        var wrongTurn = await service.ConsumeNextAsync("session-1", "agent-1", "turn-2", 1);
        var consumed = await service.ConsumeNextAsync("session-1", "agent-1", "turn-1", 2);
        var consumedAgain = await service.ConsumeNextAsync("session-1", "agent-1", "turn-1", 3);

        Assert.IsNull(wrongTurn);
        Assert.IsNotNull(consumed);
        Assert.AreEqual(created.SteeringId, consumed.SteeringId);
        Assert.AreEqual("turn-1", consumed.TargetTurnId);
        Assert.AreEqual("请改为先检查错误日志。", consumed.MessageText);
        Assert.AreEqual(2, consumed.Round);
        Assert.IsNull(consumedAgain);
    }

    [TestMethod]
    public async Task EnqueueAsync_TargetsSubAgentSessionAndParentTurn_WithoutLeakingToParent()
    {
        // 子代理的消费键是 (subSessionId, 父 turn)。父会话与子代理共用同一个 turn，
        // 只有 session 不同——所以这条同时验证「能投到」与「不会漏给父会话」。
        await using var scope = await CreateScopeAsync();
        var service = new SessionSteeringService(
            scope.Factory,
            NullLogger<SessionSteeringService>.Instance);

        var enqueued = await service.EnqueueAsync(new SessionSteeringEnqueueRequest(
            WorkspaceId: "default",
            SessionId: "parent-sub-abc",
            TargetTurnId: "turn-9",
            AgentId: null,
            MessageText: "先看 churn 那一轮。",
            SourceQueueItemId: "steer-1",
            CreatedBy: "parent-agent"));

        Assert.IsFalse(string.IsNullOrWhiteSpace(enqueued.SteeringId));
        Assert.IsFalse(string.IsNullOrWhiteSpace(enqueued.Status));

        // 父会话消费（同 turn、不同 session）：必须拿不到。
        var parentSaw = await service.ConsumeNextAsync("parent-session", "agent-1", "turn-9", 1);
        Assert.IsNull(parentSaw, "sub-agent steering must not leak into the parent session");

        // 子代理消费：必须拿到。
        var subSaw = await service.ConsumeNextAsync("parent-sub-abc", "agent-1", "turn-9", 2);
        Assert.IsNotNull(subSaw);
        Assert.AreEqual(enqueued.SteeringId, subSaw.SteeringId);
        Assert.AreEqual("parent-sub-abc", subSaw.SessionId);
        Assert.AreEqual(2, subSaw.Round);
    }

    [TestMethod]
    public async Task EnqueueAsync_SameSourceQueueItem_IsIdempotent()
    {
        await using var scope = await CreateScopeAsync();
        var service = new SessionSteeringService(
            scope.Factory,
            NullLogger<SessionSteeringService>.Instance);

        var request = new SessionSteeringEnqueueRequest(
            WorkspaceId: "default",
            SessionId: "sub-1",
            TargetTurnId: "turn-1",
            AgentId: null,
            MessageText: "重复投递",
            SourceQueueItemId: "steer-dup",
            CreatedBy: "parent-agent");

        var first = await service.EnqueueAsync(request);
        var second = await service.EnqueueAsync(request);

        Assert.AreEqual(first.SteeringId, second.SteeringId,
            "same SourceQueueItemId must not create a second row");
    }

    [TestMethod]
    public async Task EnqueueAsync_WithBlankTargetTurn_FailsLoudly()
    {
        // 空 turn 必须报错，不能静默入库——静默入库的消息永远不会被消费，
        // 而从回执上看不出区别。
        await using var scope = await CreateScopeAsync();
        var service = new SessionSteeringService(
            scope.Factory,
            NullLogger<SessionSteeringService>.Instance);

        var threw = false;
        try
        {
            await service.EnqueueAsync(new SessionSteeringEnqueueRequest(
                WorkspaceId: "default",
                SessionId: "sub-1",
                TargetTurnId: "   ",
                AgentId: null,
                MessageText: "文本"));
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "an empty target turn must fail loudly instead of sitting unconsumed");
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
