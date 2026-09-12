using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Execution;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class ExecutionLeaseStoreRecoveryTests
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;
    private SqliteExecutionLeaseStore _store = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<PlatformDbContext>(options => options.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await db.Database.EnsureCreatedAsync();

        _store = new SqliteExecutionLeaseStore(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SqliteExecutionLeaseStore>.Instance);
    }

    [TestCleanup]
    public async Task CleanupAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [TestMethod]
    public async Task Release_RestoresCommandAndTurnForRetryInOneOperation()
    {
        var lease = await SeedRunningExecutionAsync(expired: false);

        await _store.ReleaseAsync(lease, RunStatus.LeaseLost, CancellationToken.None);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var command = await db.ChatExecutionCommands.AsNoTracking().SingleAsync();
        var turn = await db.ConversationTurns.AsNoTracking().SingleAsync();
        var run = await db.ExecutionRuns.AsNoTracking().SingleAsync();

        Assert.AreEqual("pending", command.Status);
        Assert.IsNull(command.LeaseOwner);
        Assert.IsNull(command.LeaseUntil);
        Assert.AreEqual("accepted", turn.Status);
        Assert.IsNull(turn.TerminalSequence);
        Assert.AreEqual("lease_lost", run.Status);
        Assert.IsNotNull(run.CompletedAt);
    }

    [TestMethod]
    public async Task Acquire_ReclaimsExpiredRunAndRestoresTurnBeforeRetry()
    {
        var expiredLease = await SeedRunningExecutionAsync(expired: true);

        var nextLease = await _store.TryAcquireAsync(
            "worker-2",
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        Assert.IsNotNull(nextLease);
        Assert.AreEqual(expiredLease.CommandId, nextLease.CommandId);
        Assert.AreEqual("worker-2", nextLease.WorkerId);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var command = await db.ChatExecutionCommands.AsNoTracking().SingleAsync();
        var turn = await db.ConversationTurns.AsNoTracking().SingleAsync();
        var runs = await db.ExecutionRuns.AsNoTracking()
            .OrderBy(run => run.FencingToken)
            .ToListAsync();

        Assert.AreEqual("leased", command.Status);
        Assert.AreEqual("worker-2", command.LeaseOwner);
        Assert.AreEqual(2, command.AttemptCount);
        Assert.AreEqual("accepted", turn.Status);
        Assert.AreEqual(2, runs.Count);
        Assert.AreEqual("lease_lost", runs[0].Status);
        Assert.IsNotNull(runs[0].CompletedAt);
        Assert.AreEqual("leased", runs[1].Status);
    }

    [TestMethod]
    public async Task Release_WithSucceededOutcome_MarksRunSucceededAndDoesNotRequeue()
    {
        var lease = await SeedRunningExecutionAsync(expired: false);

        await _store.ReleaseAsync(lease, RunStatus.Succeeded, CancellationToken.None);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var command = await db.ChatExecutionCommands.AsNoTracking().SingleAsync();
        var turn = await db.ConversationTurns.AsNoTracking().SingleAsync();
        var run = await db.ExecutionRuns.AsNoTracking().SingleAsync();

        // AC1：正常完成（调用方声明 Succeeded）不得被贴成 lease_lost。
        Assert.AreNotEqual("lease_lost", run.Status);
        Assert.AreEqual("succeeded", run.Status);
        Assert.IsNotNull(run.CompletedAt);
        Assert.AreEqual("succeeded", command.Status);
        Assert.IsNull(command.LeaseOwner);
        Assert.IsNull(command.LeaseUntil);
        Assert.IsNotNull(command.CompletedAt);
        // 优雅释放不得把 Turn 退回 accepted（否则同一 Turn 会被重复执行）。
        Assert.AreEqual("running", turn.Status);
    }

    [TestMethod]
    public async Task Renew_FailsForExpiredRun_AndReclaimKeepsLeaseLost()
    {
        var expiredLease = await SeedRunningExecutionAsync(expired: true);

        var renewed = await _store.RenewAsync(
            expiredLease, TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.IsFalse(renewed);

        var nextLease = await _store.TryAcquireAsync(
            "worker-2", TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.IsNotNull(nextLease);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var runs = await db.ExecutionRuns.AsNoTracking()
            .OrderBy(run => run.FencingToken)
            .ToListAsync();

        // AC1：真正丢失/过期回收的路径仍然写 lease_lost。
        Assert.AreEqual(2, runs.Count);
        Assert.AreEqual("lease_lost", runs[0].Status);
        Assert.IsNotNull(runs[0].CompletedAt);
    }

    [TestMethod]
    public async Task Acquire_ReplayedAttempt_ReusesExistingRunRowWithoutDuplicate()
    {
        await SeedReplayedAttemptAsync();

        var lease = await _store.TryAcquireAsync(
            "worker-2", TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.IsNotNull(lease);
        Assert.AreEqual("command-1", lease.CommandId);
        Assert.AreEqual("run-1", lease.RunId);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var runs = await db.ExecutionRuns.AsNoTracking().ToListAsync();
        var command = await db.ChatExecutionCommands.AsNoTracking().SingleAsync();

        // AC2：同一 (command_id, attempt) 只产生一行 run，且不抛 SqliteException。
        Assert.AreEqual(1, runs.Count);
        Assert.AreEqual("run-1", runs[0].RunId);
        Assert.AreEqual(1, runs[0].Attempt);
        Assert.AreEqual(lease.FencingToken, runs[0].FencingToken);
        Assert.AreEqual("leased", runs[0].Status);
        Assert.AreEqual("worker-2", runs[0].WorkerId);
        Assert.IsNull(runs[0].CompletedAt);
        Assert.AreEqual("leased", command.Status);
        Assert.AreEqual(1, command.AttemptCount);
    }

    /// <summary>
    /// attempt_count(=0) 落后于既有 run 行(attempt=1)：回收/重放会把命令退回 pending 但不回退
    /// attempt_count，旧实现此时直接 INSERT 会撞 (command_id, attempt) 唯一键并抛异常。
    /// </summary>
    private async Task SeedReplayedAttemptAsync()
    {
        const string commandId = "command-1";
        const string conversationId = "conversation-1";
        const string turnId = "turn-1";
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
        {
            BatchId = "batch-1",
            CommandId = commandId,
            WorkspaceId = "default",
            SessionId = conversationId,
            MessageId = "assistant-message-1",
            UserMessageId = "user-message-1",
            TurnId = turnId,
            AgentInstanceId = "agent-1",
            Status = "pending",
            AttemptCount = 0,
            CreatedAt = nowMs,
        });
        db.ExecutionRuns.Add(new ExecutionRunEntity
        {
            RunId = "run-1",
            CommandId = commandId,
            ConversationId = conversationId,
            TurnId = turnId,
            Attempt = 1,
            WorkerId = "worker-1",
            Status = "lease_lost",
            LeaseUntil = now.AddMinutes(-1).ToUnixTimeMilliseconds(),
            StartedAt = nowMs,
            CompletedAt = nowMs,
            TerminalSequence = 7,
        });
        await db.SaveChangesAsync();
    }

    private async Task<ExecutionLease> SeedRunningExecutionAsync(bool expired)
    {
        const string commandId = "command-1";
        const string conversationId = "conversation-1";
        const string turnId = "turn-1";
        const string runId = "run-1";
        const string workerId = "worker-1";
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = expired ? now.AddMinutes(-1) : now.AddMinutes(2);
        var nowMs = now.ToUnixTimeMilliseconds();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
        {
            BatchId = "batch-1",
            CommandId = commandId,
            WorkspaceId = "default",
            SessionId = conversationId,
            MessageId = "assistant-message-1",
            UserMessageId = "user-message-1",
            TurnId = turnId,
            AgentInstanceId = "agent-1",
            Status = "running",
            LeaseOwner = workerId,
            LeaseUntil = leaseUntil.ToUnixTimeMilliseconds(),
            AttemptCount = 1,
            CreatedAt = nowMs,
        });
        db.ConversationTurns.Add(new ConversationTurnEntity
        {
            ConversationId = conversationId,
            TurnId = turnId,
            CommandId = commandId,
            WorkspaceId = "default",
            Status = "running",
            AcceptedSequence = 1,
            CreatedAt = nowMs,
        });
        var run = new ExecutionRunEntity
        {
            RunId = runId,
            CommandId = commandId,
            ConversationId = conversationId,
            TurnId = turnId,
            Attempt = 1,
            WorkerId = workerId,
            Status = "running",
            LeaseUntil = leaseUntil.ToUnixTimeMilliseconds(),
            StartedAt = nowMs,
        };
        db.ExecutionRuns.Add(run);
        db.ConversationHeads.Add(new ConversationHeadEntity
        {
            ConversationId = conversationId,
            HeadSequence = 1,
        });
        await db.SaveChangesAsync();

        return new ExecutionLease(
            commandId,
            workerId,
            "default",
            conversationId,
            turnId,
            runId,
            run.FencingToken,
            leaseUntil)
        {
            TraceId = null,
        };
    }
}
