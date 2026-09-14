using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class AgentRunProjectionQueryTests
{
    private SqliteConnection _connection = null!;
    private PlatformDbContext _db = null!;
    private QueryRecorder _queries = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _queries = new QueryRecorder();
        _db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(_connection).AddInterceptors(_queries).Options);
        await _db.Database.EnsureCreatedAsync();
        _queries.Commands.Clear();
    }

    [TestCleanup]
    public async Task CleanupAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [TestMethod]
    public async Task LatestLifecycle_ReusesHeadAndNeverReadsPayloadOrRanksHistory()
    {
        for (var i = 1; i <= 100; i++)
            AddEvent("a", i, ConversationEventTypes.MessageContentAppended);
        AddEvent("a", 101, ConversationEventTypes.TurnCompleted);
        await SaveAsync();
        var heads = await AgentRunProjectionService.LoadEventHeadsAsync(_db, ["a", "a"], CancellationToken.None);
        Assert.AreEqual(101L, heads["a"].Latest!.Sequence);
        Assert.AreEqual(heads["a"].Latest, heads["a"].Lifecycle);
        Assert.AreEqual(1, _queries.Commands.Count, "Duplicate main-session bindings must not repeat the query.");
        var sql = _queries.Commands.Single();
        Assert.IsFalse(sql.Contains("payload", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(sql.Contains("ROW_NUMBER", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task NonLifecycleHead_PreservesLatestCursorAndSeparateLifecycleAcrossConversations()
    {
        AddEvent("a", 1, ConversationEventTypes.TurnStarted);
        AddEvent("a", 2, ConversationEventTypes.TurnCancelled);
        AddEvent("a", 3, "control.steering.accepted");
        AddEvent("b", 1, ConversationEventTypes.TurnStarted);
        AddEvent("c", 1, "control.steering.accepted");
        await SaveAsync();
        var heads = await AgentRunProjectionService.LoadEventHeadsAsync(_db, ["a", "b", "c", "missing"], CancellationToken.None);
        Assert.AreEqual(3L, heads["a"].Latest!.Sequence);
        Assert.AreEqual(ConversationEventTypes.TurnCancelled, heads["a"].Lifecycle!.Type);
        Assert.AreEqual("run-b", heads["b"].Lifecycle!.RunId);
        Assert.IsNull(heads["c"].Lifecycle);
        Assert.IsNull(heads["missing"].Latest);
        Assert.AreEqual(6, _queries.Commands.Count);
    }

    [TestMethod]
    public async Task EmptyRoster_DoesNotReadEventStore()
    {
        var heads = await AgentRunProjectionService.LoadEventHeadsAsync(_db, [], CancellationToken.None);
        Assert.AreEqual(0, heads.Count);
        Assert.AreEqual(0, _queries.Commands.Count);
    }

    [TestMethod]
    public async Task CancelledRead_DoesNotContinueRosterScan()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            AgentRunProjectionService.LoadEventHeadsAsync(_db, ["a", "b"], cancellation.Token));
    }

    private void AddEvent(string conversationId, long sequence, string type) =>
        _db.ConversationEvents.Add(new ConversationEventEntity
        {
            ConversationId = conversationId, Sequence = sequence, Type = type,
            EventId = Guid.NewGuid().ToString("N"), WorkspaceId = "default",
            TurnId = "turn-" + conversationId, RunId = "run-" + conversationId,
            Payload = new string('x', 8192),
            OccurredAt = DateTimeOffset.UtcNow.ToString("O"), CommittedAt = DateTimeOffset.UtcNow.ToString("O"),
        });

    private async Task SaveAsync()
    {
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        _queries.Commands.Clear();
    }

    private sealed class QueryRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
