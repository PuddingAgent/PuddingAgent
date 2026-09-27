using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Execution;

namespace PuddingPlatformTests.Services;

/// <summary>
/// NC-01 S1/S2：审批暂停 park 与「一次暂停只能被恢复一次」。
/// 断言只声明「已写入且 CAS 成立」；Runtime 暂停信号与续行不在本测试范围。
/// </summary>
[TestClass]
public sealed class ApprovalPauseResumeTests
{
    private const string CommandId = "command-1";
    private const string ConversationId = "conversation-1";
    private const string TurnId = "turn-1";
    private const string RunId = "run-1";
    private const string WorkerId = "worker-1";
    private const string WorkspaceId = "default";
    private const string ApprovalId = "approval-1";
    private const string InvocationId = "call-1";

    private string _databasePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"pudding-approval-pause-{Guid.NewGuid():N}.db");
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// park：三行收敛为 waiting_approval、不写任何终态、释放租约，恢复点原样落盘并可读回。
    /// </summary>
    [TestMethod]
    public async Task ParkForApproval_WritesNoTerminalFactAndPersistsResumePoint()
    {
        await using var host = await TestHost.CreateAsync(_databasePath);
        var lease = await host.SeedRunningExecutionAsync();
        var point = NewResumePoint(lease);
        var pending = new[]
        {
            NewEvent(lease, ConversationEventTypes.MessageContentAppended, new { delta = "部分回复" }),
        };

        var result = await host.Journal.ParkForApprovalAsync(
            lease, point, pending, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(1L, result.LastSequence);
        Assert.AreEqual(1, result.EventCount);
        Assert.AreEqual((ConversationId, 1L), host.Signal.LastSignal);

        await using var db = await host.CreateDbAsync();
        var turn = await db.ConversationTurns.AsNoTracking().SingleAsync();
        var run = await db.ExecutionRuns.AsNoTracking().SingleAsync();
        var command = await db.ChatExecutionCommands.AsNoTracking().SingleAsync();
        var events = await db.ConversationEvents.AsNoTracking()
            .OrderBy(e => e.Sequence)
            .ToListAsync();

        Assert.AreEqual(ApprovalResumePoint.WaitingApprovalStatus, turn.Status);
        Assert.IsNull(turn.TerminalSequence);
        Assert.IsNull(turn.TerminalKind);
        Assert.IsNull(turn.CompletedAt);
        Assert.AreEqual(ApprovalResumePoint.WaitingApprovalStatus, run.Status);
        Assert.IsNull(run.TerminalSequence);
        Assert.IsNull(run.CompletedAt);
        Assert.IsNull(run.LeaseUntil);
        Assert.AreEqual(ApprovalResumePoint.WaitingApprovalStatus, command.Status);
        Assert.IsNull(command.LeaseOwner);
        Assert.IsNull(command.LeaseUntil);
        Assert.IsNull(command.TerminalSequence);

        CollectionAssert.AreEqual(
            new[] { ConversationEventTypes.MessageContentAppended },
            events.Select(e => e.Type).ToArray());

        // 恢复点必须逐字段读回：park 落盘与 acquire 读回使用同一份实现。
        Assert.IsNotNull(command.ApprovalResumeJson);
        Assert.IsTrue(ApprovalResumePoint.TryParse(
            command.ApprovalResumeJson, out var restored, out var error), error);
        AssertResumePointEqual(point, restored!);
    }

    /// <summary>
    /// 恢复点身份与租约不一致时拒绝 park，且不留下部分写入。
    /// </summary>
    [TestMethod]
    public async Task ParkForApproval_RejectsIdentityMismatchWithoutPartialWrite()
    {
        await using var host = await TestHost.CreateAsync(_databasePath);
        var lease = await host.SeedRunningExecutionAsync();
        var point = NewResumePoint(lease, turnId: "another-turn");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            host.Journal.ParkForApprovalAsync(lease, point, [], CancellationToken.None));

        await host.AssertStillRunningAsync();
    }

    /// <summary>恢复点形状非法（未通过 Validate）时拒绝 park，而不是静默存下一个无法续行的暂停。</summary>
    [TestMethod]
    public async Task ParkForApproval_RejectsInvalidResumePoint()
    {
        await using var host = await TestHost.CreateAsync(_databasePath);
        var lease = await host.SeedRunningExecutionAsync();

        // Runtime 状态必须是 JSON 对象，不能是裸数组或标量。
        var notAnObject = NewResumePoint(lease, runtimeStateJson: "[1,2,3]");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            host.Journal.ParkForApprovalAsync(lease, notAnObject, [], CancellationToken.None));

        // 未知 schema 版本一律拒绝，不做向后兼容猜测。
        var futureVersion = NewResumePoint(lease) with { SchemaVersion = 99 };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            host.Journal.ParkForApprovalAsync(lease, futureVersion, [], CancellationToken.None));

        // 冻结预算不得越界。
        var overspent = NewResumePoint(lease) with { UsedToolCalls = 999 };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            host.Journal.ParkForApprovalAsync(lease, overspent, [], CancellationToken.None));

        await host.AssertStillRunningAsync();
    }

    /// <summary>租约已被取代（fence 不匹配）时 park 返回 null，不覆盖新的 writer。</summary>
    [TestMethod]
    public async Task ParkForApproval_RejectsStaleFence()
    {
        await using var host = await TestHost.CreateAsync(_databasePath);
        var lease = await host.SeedRunningExecutionAsync();
        var stale = lease with { FencingToken = lease.FencingToken + 7 };

        var result = await host.Journal.ParkForApprovalAsync(
            stale, NewResumePoint(stale), [], CancellationToken.None);

        Assert.IsNull(result);
        await host.AssertStillRunningAsync();
    }

    /// <summary>park 拒绝终态事件：终态只能走 CommitTerminalAsync。</summary>
    [TestMethod]
    public async Task ParkForApproval_RejectsTerminalEvents()
    {
        await using var host = await TestHost.CreateAsync(_databasePath);
        var lease = await host.SeedRunningExecutionAsync();
        var terminal = new[]
        {
            NewEvent(lease, ConversationEventTypes.TurnCompleted, new { reply = "done" }),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.Journal.ParkForApprovalAsync(
                lease, NewResumePoint(lease), terminal, CancellationToken.None));

        await host.AssertStillRunningAsync();
    }

    /// <summary>
    /// 唯一性闸门：同一暂停连续两次领取，只有第一次成功；成功后 Turn 回到 running、
    /// 旧 run 记为 resumed、新 run 取得更高 fencing token。
    /// </summary>
    [TestMethod]
    public async Task TryAcquireApprovalResume_IsSingleShot()
    {
        await using var host = await TestHost.CreateAsync(_databasePath);
        var lease = await host.SeedRunningExecutionAsync();
        var point = NewResumePoint(lease);
        await host.Journal.ParkForApprovalAsync(lease, point, [], CancellationToken.None);

        var first = await host.LeaseStore.TryAcquireApprovalResumeAsync(
            "worker-2", TurnId, TimeSpan.FromMinutes(2), CancellationToken.None);
        var second = await host.LeaseStore.TryAcquireApprovalResumeAsync(
            "worker-3", TurnId, TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.IsNotNull(first);
        Assert.IsNull(second, "一次暂停只能被恢复一次");
        Assert.AreEqual("worker-2", first.Lease.WorkerId);
        Assert.AreEqual(RunId, point.RunId);
        Assert.AreNotEqual(RunId, first.Lease.RunId);
        Assert.IsTrue(first.Lease.FencingToken > lease.FencingToken,
            "恢复必须换发更高的 fencing token，否则旧 worker 仍可写入");
        AssertResumePointEqual(point, first.ResumePoint);

        await using var db = await host.CreateDbAsync();
        var turn = await db.ConversationTurns.AsNoTracking().SingleAsync();
        var runs = await db.ExecutionRuns.AsNoTracking().OrderBy(r => r.FencingToken).ToListAsync();
        var command = await db.ChatExecutionCommands.AsNoTracking().SingleAsync();

        Assert.AreEqual("running", turn.Status);
        Assert.IsNull(turn.TerminalSequence);
        Assert.AreEqual(2, runs.Count);
        Assert.AreEqual(ApprovalResumePoint.ResumedRunStatus, runs[0].Status);
        Assert.IsNull(runs[0].LeaseUntil);
        Assert.AreEqual("leased", runs[1].Status);
        Assert.AreEqual(first.Lease.RunId, runs[1].RunId);
        Assert.AreEqual("leased", command.Status);
        Assert.AreEqual("worker-2", command.LeaseOwner);
        Assert.AreEqual(first.Lease.RunId, command.RunId);
    }

    /// <summary>
    /// 恢复后旧租约被围栏挡下：它仍然是同一个 Turn/Run，但已不能写任何终态。
    /// 这是「已消费但结果未知」之外的基本安全属性。
    /// </summary>
    [TestMethod]
    public async Task TryAcquireApprovalResume_FencesOutTheParkedLease()
    {
        await using var host = await TestHost.CreateAsync(_databasePath);
        var lease = await host.SeedRunningExecutionAsync();
        await host.Journal.ParkForApprovalAsync(
            lease, NewResumePoint(lease), [], CancellationToken.None);

        var resumed = await host.LeaseStore.TryAcquireApprovalResumeAsync(
            "worker-2", TurnId, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.IsNotNull(resumed);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.Journal.CommitTerminalAsync(
                lease, TurnTerminal.Success("stale worker reply", null), [], CancellationToken.None));

        await using var db = await host.CreateDbAsync();
        var events = await db.ConversationEvents.AsNoTracking().ToListAsync();
        Assert.IsEmpty(events);
        var turn = await db.ConversationTurns.AsNoTracking().SingleAsync();
        Assert.AreEqual("running", turn.Status);
        Assert.IsNull(turn.TerminalSequence);
    }

    /// <summary>没有暂停行时领取返回 null（不误领 running/pending 的 Turn）。</summary>
    [TestMethod]
    public async Task TryAcquireApprovalResume_ReturnsNullWhenNothingParked()
    {
        await using var host = await TestHost.CreateAsync(_databasePath);
        await host.SeedRunningExecutionAsync();

        var result = await host.LeaseStore.TryAcquireApprovalResumeAsync(
            "worker-2", TurnId, TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.IsNull(result);
    }

    /// <summary>
    /// 冻结截止时间已过就不再续行，且不服宽预算。
    /// 已知缺口：本轮没有到期扫描把该行收口成超时终态，因此它保持 waiting_approval。
    /// </summary>
    [TestMethod]
    public async Task TryAcquireApprovalResume_ReturnsNullAfterFrozenDeadline()
    {
        await using var host = await TestHost.CreateAsync(_databasePath);
        var lease = await host.SeedRunningExecutionAsync();
        var expired = NewResumePoint(lease) with
        {
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
        };
        await host.Journal.ParkForApprovalAsync(lease, expired, [], CancellationToken.None);

        var result = await host.LeaseStore.TryAcquireApprovalResumeAsync(
            "worker-2", TurnId, TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.IsNull(result);

        await using var db = await host.CreateDbAsync();
        var turn = await db.ConversationTurns.AsNoTracking().SingleAsync();
        Assert.AreEqual(ApprovalResumePoint.WaitingApprovalStatus, turn.Status);
        Assert.IsNull(turn.TerminalSequence);
    }

    /// <summary>
    /// 跨进程重启：新 host（新的 DbContext/连接池）能读回同一恢复点并完成唯一一次领取。
    /// </summary>
    [TestMethod]
    public async Task TryAcquireApprovalResume_SurvivesRestart()
    {
        var point = default(ApprovalResumePoint);
        await using (var first = await TestHost.CreateAsync(_databasePath))
        {
            var lease = await first.SeedRunningExecutionAsync();
            point = NewResumePoint(lease);
            await first.Journal.ParkForApprovalAsync(lease, point, [], CancellationToken.None);
        }

        SqliteConnection.ClearAllPools();

        await using var restarted = await TestHost.CreateAsync(_databasePath);
        var resumed = await restarted.LeaseStore.TryAcquireApprovalResumeAsync(
            "worker-after-restart", TurnId, TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.IsNotNull(resumed);
        AssertResumePointEqual(point!, resumed.ResumePoint);
        Assert.AreEqual("worker-after-restart", resumed.Lease.WorkerId);

        // 重启后也只有一次：第二次领取必须落空。
        var again = await restarted.LeaseStore.TryAcquireApprovalResumeAsync(
            "worker-after-restart-2", TurnId, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.IsNull(again);
    }

    /// <summary>恢复点损坏（非 JSON / 字段缺失 / 版本未知）时放弃恢复，绝不部分还原。</summary>
    [TestMethod]
    [DataRow("not json at all")]
    [DataRow("{\"schemaVersion\":99}")]
    [DataRow("{\"schemaVersion\":1,\"workspaceId\":\"default\"}")]
    public async Task TryAcquireApprovalResume_RejectsCorruptedResumePoint(string stored)
    {
        await using var host = await TestHost.CreateAsync(_databasePath);
        var lease = await host.SeedRunningExecutionAsync();
        await host.Journal.ParkForApprovalAsync(
            lease, NewResumePoint(lease), [], CancellationToken.None);
        await host.OverwriteResumeJsonAsync(stored);

        var result = await host.LeaseStore.TryAcquireApprovalResumeAsync(
            "worker-2", TurnId, TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.IsNull(result);

        await using var db = await host.CreateDbAsync();
        var turn = await db.ConversationTurns.AsNoTracking().SingleAsync();
        Assert.AreEqual(ApprovalResumePoint.WaitingApprovalStatus, turn.Status);
        Assert.IsNull(turn.TerminalSequence);
        Assert.AreEqual(1, await db.ExecutionRuns.CountAsync());
    }

    /// <summary>并发领取最多只有一个成功；落败者必须是「没领到」而不是写入半截状态。</summary>
    [TestMethod]
    public async Task TryAcquireApprovalResume_ConcurrentClaimsYieldAtMostOneWinner()
    {
        await using var host = await TestHost.CreateAsync(_databasePath);
        var lease = await host.SeedRunningExecutionAsync();
        await host.Journal.ParkForApprovalAsync(
            lease, NewResumePoint(lease), [], CancellationToken.None);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            try
            {
                var acquired = await host.LeaseStore.TryAcquireApprovalResumeAsync(
                    $"worker-{index}", TurnId, TimeSpan.FromMinutes(2), CancellationToken.None);
                return acquired is null ? 0 : 1;
            }
            catch (SqliteException)
            {
                // SQLite 串行化争用可能直接报忙；它同样不是成功领取。
                return 0;
            }
        }));

        Assert.AreEqual(1, attempts.Sum(), "并发领取只能有一个赢家");

        await using var db = await host.CreateDbAsync();
        Assert.AreEqual(2, await db.ExecutionRuns.CountAsync());
        var command = await db.ChatExecutionCommands.AsNoTracking().SingleAsync();
        Assert.AreEqual("leased", command.Status);
    }

    private static ApprovalResumePoint NewResumePoint(
        ExecutionLease lease,
        string? turnId = null,
        string runtimeStateJson = "{\"round\":1}")
    {
        var point = new ApprovalResumePoint
        {
            WorkspaceId = lease.WorkspaceId,
            AgentInstanceId = "agent-1",
            SessionId = lease.ConversationId,
            RunId = lease.RunId,
            TurnId = turnId ?? lease.TurnId,
            CommandId = lease.CommandId,
            InvocationId = InvocationId,
            ApprovalId = ApprovalId,
            ToolId = "sample_high",
            ArgumentsJson = "{\"target\":\"a.txt\"}",
            ToolDefinitionJson = "{\"name\":\"sample_high\"}",
            ExecutionRoot = Path.GetPathRoot(Path.GetTempPath())!,
            PolicyRevision = "policy-r1",
            DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(10),
            MaxRounds = 20,
            MaxToolCallsTotal = 50,
            UsedToolCalls = 3,
            Round = 1,
            PendingToolIndex = 1,
            RuntimeStateJson = runtimeStateJson,
            SnapshotId = "snapshot-1",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        return point;
    }

    private static void AssertResumePointEqual(ApprovalResumePoint expected, ApprovalResumePoint actual)
    {
        Assert.AreEqual(expected.SchemaVersion, actual.SchemaVersion);
        Assert.AreEqual(expected.WorkspaceId, actual.WorkspaceId);
        Assert.AreEqual(expected.AgentInstanceId, actual.AgentInstanceId);
        Assert.AreEqual(expected.SessionId, actual.SessionId);
        Assert.AreEqual(expected.RunId, actual.RunId);
        Assert.AreEqual(expected.TurnId, actual.TurnId);
        Assert.AreEqual(expected.CommandId, actual.CommandId);
        Assert.AreEqual(expected.InvocationId, actual.InvocationId);
        Assert.AreEqual(expected.ApprovalId, actual.ApprovalId);
        Assert.AreEqual(expected.ToolId, actual.ToolId);
        Assert.AreEqual(expected.ArgumentsJson, actual.ArgumentsJson);
        Assert.AreEqual(expected.ToolDefinitionJson, actual.ToolDefinitionJson);
        Assert.AreEqual(expected.ExecutionRoot, actual.ExecutionRoot);
        Assert.AreEqual(expected.PolicyRevision, actual.PolicyRevision);
        Assert.AreEqual(expected.DeadlineUtc.ToUniversalTime(), actual.DeadlineUtc.ToUniversalTime());
        Assert.AreEqual(expected.MaxRounds, actual.MaxRounds);
        Assert.AreEqual(expected.MaxToolCallsTotal, actual.MaxToolCallsTotal);
        Assert.AreEqual(expected.UsedToolCalls, actual.UsedToolCalls);
        Assert.AreEqual(expected.Round, actual.Round);
        Assert.AreEqual(expected.PendingToolIndex, actual.PendingToolIndex);
        Assert.AreEqual(expected.SnapshotId, actual.SnapshotId);
        Assert.AreEqual(expected.RuntimeStateJson, actual.RuntimeStateJson);
        Assert.AreEqual(expected.CreatedAtUtc.ToUniversalTime(), actual.CreatedAtUtc.ToUniversalTime());
    }

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

    private sealed class TestHost : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _databasePath;

        private TestHost(ServiceProvider provider, string databasePath)
        {
            _provider = provider;
            _databasePath = databasePath;
            Signal = new RecordingCommittedEventSignal();
            Journal = new SqliteExecutionJournal(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Signal,
                NullLogger<SqliteExecutionJournal>.Instance);
            LeaseStore = new SqliteExecutionLeaseStore(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<SqliteExecutionLeaseStore>.Instance);
        }

        public SqliteExecutionJournal Journal { get; }
        public SqliteExecutionLeaseStore LeaseStore { get; }
        public RecordingCommittedEventSignal Signal { get; }

        public static async Task<TestHost> CreateAsync(string databasePath)
        {
            var services = new ServiceCollection();
            // 每个 DbContext 各自开连接：并发领取测试需要真实的多连接争用。
            services.AddDbContextFactory<PlatformDbContext>(
                options => options.UseSqlite($"Data Source={databasePath}"),
                ServiceLifetime.Singleton);
            var provider = services.BuildServiceProvider();

            await using var db = await provider
                .GetRequiredService<IDbContextFactory<PlatformDbContext>>()
                .CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();

            return new TestHost(provider, databasePath);
        }

        public Task<PlatformDbContext> CreateDbAsync() =>
            _provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>()
                .CreateDbContextAsync();

        public async Task<ExecutionLease> SeedRunningExecutionAsync()
        {
            var now = DateTimeOffset.UtcNow;
            var nowMs = now.ToUnixTimeMilliseconds();

            await using var db = await CreateDbAsync();
            db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
            {
                BatchId = "batch-1",
                CommandId = CommandId,
                WorkspaceId = WorkspaceId,
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
                WorkspaceId = WorkspaceId,
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
                WorkspaceId,
                ConversationId,
                TurnId,
                RunId,
                run.FencingToken,
                now.AddMinutes(2))
            {
                TraceId = null,
            };
        }

        /// <summary>park 被拒后必须一行未改：三行仍是 running、无恢复点、无事件。</summary>
        public async Task AssertStillRunningAsync()
        {
            await using var db = await CreateDbAsync();
            var turn = await db.ConversationTurns.AsNoTracking().SingleAsync();
            var run = await db.ExecutionRuns.AsNoTracking().SingleAsync();
            var command = await db.ChatExecutionCommands.AsNoTracking().SingleAsync();
            Assert.AreEqual("running", turn.Status);
            Assert.AreEqual("running", run.Status);
            Assert.AreEqual("running", command.Status);
            Assert.IsNull(command.ApprovalResumeJson);
            Assert.AreEqual(0, await db.ConversationEvents.CountAsync());
        }

        public async Task OverwriteResumeJsonAsync(string value)
        {
            await using var db = await CreateDbAsync();
            var command = await db.ChatExecutionCommands.SingleAsync();
            command.ApprovalResumeJson = value;
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            _ = _databasePath;
        }
    }

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
