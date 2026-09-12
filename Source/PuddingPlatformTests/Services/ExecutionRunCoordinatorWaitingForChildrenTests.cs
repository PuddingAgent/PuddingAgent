using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.AgentChat;
using PuddingPlatform.Services.Execution;

namespace PuddingPlatformTests.Services;

/// <summary>
/// A01-slice-4c：父 Turn waiting_child park / 唤醒收口。
/// 断言只声明「已写入」，测试结论以父级进程外复跑为准。
/// </summary>
[TestClass]
public sealed class ExecutionRunCoordinatorWaitingForChildrenTests
{
    private const string CommandId = "command-1";
    private const string ConversationId = "conversation-1";
    private const string TurnId = "turn-1";
    private const string RunId = "run-1";
    private const string WorkerId = "worker-1";

    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;
    private RecordingCommittedEventSignal _signal = null!;
    private SqliteExecutionJournal _journal = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContextFactory<PlatformDbContext>(
            options => options.UseSqlite(_connection),
            ServiceLifetime.Singleton);
        _provider = services.BuildServiceProvider();

        await using var db = await _provider
            .GetRequiredService<IDbContextFactory<PlatformDbContext>>()
            .CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        _signal = new RecordingCommittedEventSignal();
        _journal = new SqliteExecutionJournal(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _signal,
            NullLogger<SqliteExecutionJournal>.Instance);
    }

    [TestCleanup]
    public async Task CleanupAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>
    /// 1) park：本 Turn 有 running child → 三行收敛为 waiting_child、只写非终态输出、
    /// 不写 terminal 事件、不写 completed_at / terminal_sequence、释放租约。
    /// </summary>
    [TestMethod]
    public async Task ParkForChildren_WritesNoTerminalFactAndMarksAllThreeRowsWaiting()
    {
        var lease = await SeedRunningExecutionAsync();
        var pending = new[]
        {
            NewEvent(lease, ConversationEventTypes.MessageContentAppended, new { delta = "部分回复" }),
        };

        var result = await _journal.ParkForChildrenAsync(
            lease,
            TurnTerminal.Success("部分回复", null),
            pending,
            CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(1L, result.LastSequence);
        Assert.AreEqual(1, result.EventCount);

        await using var db = await CreateDbAsync();
        var turn = await db.ConversationTurns.AsNoTracking().SingleAsync();
        var run = await db.ExecutionRuns.AsNoTracking().SingleAsync();
        var command = await db.ChatExecutionCommands.AsNoTracking().SingleAsync();
        var events = await db.ConversationEvents.AsNoTracking()
            .OrderBy(e => e.Sequence)
            .ToListAsync();

        Assert.AreEqual("waiting_child", turn.Status);
        Assert.IsNull(turn.TerminalSequence);
        Assert.IsNull(turn.TerminalKind);
        Assert.IsNull(turn.CompletedAt);
        Assert.AreEqual("waiting_child", run.Status);
        Assert.IsNull(run.TerminalSequence);
        Assert.IsNull(run.CompletedAt);
        Assert.IsNull(run.LeaseUntil);
        Assert.AreEqual("waiting_child", command.Status);
        Assert.IsNull(command.LeaseOwner);
        Assert.IsNull(command.LeaseUntil);
        Assert.IsNull(command.TerminalSequence);
        Assert.IsTrue(
            command.MetadataJson is not null
            && command.MetadataJson.Contains("parked_terminal", StringComparison.Ordinal),
            "park 必须把待提交终态持久化进命令 metadata_json");

        CollectionAssert.AreEqual(
            new[] { ConversationEventTypes.MessageContentAppended },
            events.Select(e => e.Type).ToArray());
        Assert.AreEqual((ConversationId, 1L), _signal.LastSignal);
    }

    /// <summary>
    /// 2) park 只接受正常完成终态：失败终态必须立即终态化，不得悬停等待子代理。
    /// </summary>
    [TestMethod]
    public async Task ParkForChildren_RejectsNonCompletedTerminal()
    {
        var lease = await SeedRunningExecutionAsync();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            _journal.ParkForChildrenAsync(
                lease,
                TurnTerminal.Failure("runtime_execution_failed", "boom"),
                [],
                CancellationToken.None));

        await using var db = await CreateDbAsync();
        var turn = await db.ConversationTurns.AsNoTracking().SingleAsync();
        var run = await db.ExecutionRuns.AsNoTracking().SingleAsync();
        Assert.AreEqual("running", turn.Status);
        Assert.AreEqual("running", run.Status);
        Assert.AreEqual(0, await db.ConversationEvents.CountAsync());
    }

    /// <summary>
    /// 3) 回归：无 running child（或非正常完成）时父 Turn 不 park，与改前行为一致。
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ShouldParkForChildren_OnlyParksCompletedTerminals(bool completed)
    {
        Assert.AreEqual(
            completed,
            ExecutionRunCoordinator.ShouldParkForChildren(
                completed
                    ? TurnTerminal.Success(null, null)
                    : TurnTerminal.Failure("runtime_execution_failed", "boom")));
    }

    [TestMethod]
    public void ShouldParkForChildren_NeverParksCancelledOrLeaseLost()
    {
        Assert.IsFalse(ExecutionRunCoordinator.ShouldParkForChildren(TurnTerminal.Cancelled));
        Assert.IsFalse(ExecutionRunCoordinator.ShouldParkForChildren(TurnTerminal.LeaseLost));
    }

    /// <summary>
    /// 4) 唤醒收口：最后一个 child 归零后父 Turn 收敛为终态；重复收口不得写第二个终态事实。
    /// </summary>
    [TestMethod]
    public async Task TryFinalizeWaitingTurn_ClosesWaitingTurnExactlyOnce()
    {
        var lease = await SeedRunningExecutionAsync();
        await _journal.ParkForChildrenAsync(
            lease,
            TurnTerminal.Success("部分回复", null),
            [NewEvent(lease, ConversationEventTypes.MessageContentAppended, new { delta = "部分回复" })],
            CancellationToken.None);

        var first = await _journal.TryFinalizeWaitingTurnAsync(TurnId, CancellationToken.None);

        Assert.IsNotNull(first);
        Assert.AreEqual(TurnId, first.TurnId);
        Assert.AreEqual(RunId, first.RunId);
        Assert.AreEqual("completed", first.TerminalKind);
        Assert.AreEqual(2L, first.TerminalSequence);

        await using (var db = await CreateDbAsync())
        {
            var turn = await db.ConversationTurns.AsNoTracking().SingleAsync();
            var run = await db.ExecutionRuns.AsNoTracking().SingleAsync();
            var command = await db.ChatExecutionCommands.AsNoTracking().SingleAsync();
            var events = await db.ConversationEvents.AsNoTracking()
                .OrderBy(e => e.Sequence)
                .ToListAsync();

            Assert.AreEqual("completed", turn.Status);
            Assert.AreEqual(2L, turn.TerminalSequence);
            Assert.AreEqual("completed", turn.TerminalKind);
            Assert.IsNotNull(turn.CompletedAt);
            Assert.AreEqual("succeeded", run.Status);
            Assert.AreEqual(2L, run.TerminalSequence);
            Assert.IsNotNull(run.CompletedAt);
            Assert.AreEqual("succeeded", command.Status);
            Assert.AreEqual(2L, command.TerminalSequence);
            Assert.IsNull(command.LeaseOwner);
            CollectionAssert.AreEqual(
                new[]
                {
                    ConversationEventTypes.MessageContentAppended,
                    ConversationEventTypes.TurnCompleted,
                },
                events.Select(e => e.Type).ToArray());
            Assert.AreEqual(1, events.Count(e => e.Type == ConversationEventTypes.TurnCompleted));
        }

        // 并发/重复收口：CAS 已完成终态，第二个调用必须返回 null 且不写第二个终态事实。
        var second = await _journal.TryFinalizeWaitingTurnAsync(TurnId, CancellationToken.None);
        Assert.IsNull(second);

        await using (var db = await CreateDbAsync())
        {
            var events = await db.ConversationEvents.AsNoTracking().ToListAsync();
            Assert.AreEqual(1, events.Count(e => e.Type == ConversationEventTypes.TurnCompleted));
        }
    }

    /// <summary>
    /// 5) Turn 级归属：未 park（仍 running）的 Turn 不被收口；收口只作用于 waiting_child 行。
    /// </summary>
    [TestMethod]
    public async Task TryFinalizeWaitingTurn_IgnoresTurnThatWasNotParked()
    {
        await SeedRunningExecutionAsync();

        var result = await _journal.TryFinalizeWaitingTurnAsync(TurnId, CancellationToken.None);

        Assert.IsNull(result);
        await using var db = await CreateDbAsync();
        var turn = await db.ConversationTurns.AsNoTracking().SingleAsync();
        var run = await db.ExecutionRuns.AsNoTracking().SingleAsync();
        Assert.AreEqual("running", turn.Status);
        Assert.IsNull(turn.TerminalSequence);
        Assert.AreEqual("running", run.Status);
        Assert.AreEqual(0, await db.ConversationEvents.CountAsync());
    }

    private async Task<ExecutionLease> SeedRunningExecutionAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();

        await using var db = await CreateDbAsync();
        db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
        {
            BatchId = "batch-1",
            CommandId = CommandId,
            WorkspaceId = "default",
            SessionId = ConversationId,
            MessageId = "assistant-message-1",
            UserMessageId = "user-message-1",
            TurnId = TurnId,
            AgentInstanceId = "agent-1",
            Status = "running",
            RunId = RunId,
            LeaseOwner = WorkerId,
            LeaseUntil = now.AddMinutes(2).ToUnixTimeMilliseconds(),
            CreatedAt = nowMs,
            StartedAt = nowMs,
        });
        db.ConversationTurns.Add(new ConversationTurnEntity
        {
            ConversationId = ConversationId,
            TurnId = TurnId,
            CommandId = CommandId,
            WorkspaceId = "default",
            Status = "running",
            AcceptedSequence = 0,
            CreatedAt = nowMs,
        });
        var run = new ExecutionRunEntity
        {
            RunId = RunId,
            CommandId = CommandId,
            ConversationId = ConversationId,
            TurnId = TurnId,
            Attempt = 1,
            WorkerId = WorkerId,
            Status = "running",
            LeaseUntil = now.AddMinutes(2).ToUnixTimeMilliseconds(),
            StartedAt = nowMs,
        };
        db.ExecutionRuns.Add(run);
        db.ConversationHeads.Add(new ConversationHeadEntity
        {
            ConversationId = ConversationId,
            HeadSequence = 0,
        });
        await db.SaveChangesAsync();

        return new ExecutionLease(
            CommandId,
            WorkerId,
            "default",
            ConversationId,
            TurnId,
            RunId,
            run.FencingToken,
            now.AddMinutes(2))
        {
            TraceId = null,
        };
    }

    private Task<PlatformDbContext> CreateDbAsync() =>
        _provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>()
            .CreateDbContextAsync();

    private static NewConversationEvent NewEvent(
        ExecutionLease lease,
        string type,
        object payload) =>
        new(
            EventId: Guid.NewGuid().ToString("N"),
            Type: type,
            SchemaVersion: 1,
            WorkspaceId: lease.WorkspaceId,
            TurnId: lease.TurnId,
            CommandId: lease.CommandId,
            RunId: lease.RunId,
            MessageId: null,
            CorrelationId: lease.ConversationId,
            CausationId: lease.TurnId,
            ProducerEventId: null,
            Payload: JsonSerializer.SerializeToElement(payload));

    private sealed class RecordingCommittedEventSignal : ICommittedEventSignal
    {
        public (string ConversationId, long Sequence)? LastSignal { get; private set; }

        public ValueTask WaitForChangeAsync(
            string conversationId,
            long knownHead,
            CancellationToken ct) =>
            ValueTask.CompletedTask;

        public void Signal(string conversationId, long committedThroughSequence) =>
            LastSignal = (conversationId, committedThroughSequence);
    }
}
