using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Scheduling;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatformTests.Services.Scheduling;

[TestClass]
public sealed class AgentExecutionReservationStoreTests
{
    private string _root = null!;
    private PlatformDbContextFactory _factory = null!;
    private MutableTimeProvider _clock = null!;
    private AgentExecutionReservationStore _store = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "PuddingAgent", "reservation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "platform.db")};Default Timeout=10")
            .Options;
        _factory = new PlatformDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        _clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-26T00:00:00Z"));
        _store = new AgentExecutionReservationStore(
            _factory,
            _clock,
            NullLogger<AgentExecutionReservationStore>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task OneAgentCanOwnOnlyOneActiveAutomaticTask()
    {
        var first = await _store.TryReserveAsync(
            "ws", "agent-1", "task-1", "coordinator-1", TimeSpan.FromMinutes(5));
        var conflict = await _store.TryReserveAsync(
            "ws", "agent-1", "task-2", "coordinator-2", TimeSpan.FromMinutes(5));

        Assert.AreEqual(AgentReservationResultKind.Acquired, first.Kind);
        Assert.AreEqual(AgentReservationResultKind.Conflict, conflict.Kind);
        Assert.AreEqual("task-1", conflict.Reservation.TaskId);
    }

    [TestMethod]
    public async Task ConcurrentReservations_ProduceOneOwnerAndOneConflict()
    {
        var results = await System.Threading.Tasks.Task.WhenAll(
            _store.TryReserveAsync(
                "ws", "agent-1", "task-1", "coordinator-1", TimeSpan.FromMinutes(5)),
            _store.TryReserveAsync(
                "ws", "agent-1", "task-2", "coordinator-2", TimeSpan.FromMinutes(5)));

        Assert.HasCount(1, results.Where(item => item.Kind == AgentReservationResultKind.Acquired));
        Assert.HasCount(1, results.Where(item => item.Kind == AgentReservationResultKind.Conflict));
        Assert.AreEqual(
            results[0].Reservation.ReservationId,
            results[1].Reservation.ReservationId);
    }

    [TestMethod]
    public async Task SameOwnerAndTask_IsIdempotent()
    {
        var first = await _store.TryReserveAsync(
            "ws", "agent-1", "task-1", "coordinator", TimeSpan.FromMinutes(5));
        var replay = await _store.TryReserveAsync(
            "ws", "agent-1", "task-1", "coordinator", TimeSpan.FromMinutes(5));

        Assert.AreEqual(AgentReservationResultKind.AlreadyOwned, replay.Kind);
        Assert.AreEqual(first.Reservation.ReservationId, replay.Reservation.ReservationId);
        Assert.AreEqual(first.Reservation.FencingToken, replay.Reservation.FencingToken);
    }

    [TestMethod]
    public async Task ExpiredReservationReleasesLogicalWorkSlot()
    {
        await _store.TryReserveAsync(
            "ws", "agent-1", "task-1", "coordinator", TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(2));

        var second = await _store.TryReserveAsync(
            "ws", "agent-1", "task-2", "coordinator", TimeSpan.FromMinutes(1));

        Assert.AreEqual(AgentReservationResultKind.Acquired, second.Kind);
        Assert.AreEqual("task-2", second.Reservation.TaskId);
    }

    [TestMethod]
    public async Task StaleFencingTokenCannotReleaseNewerOwner()
    {
        var acquired = await _store.TryReserveAsync(
            "ws", "agent-1", "task-1", "coordinator", TimeSpan.FromMinutes(5));

        var released = await _store.ReleaseAsync(
            acquired.Reservation.ReservationId,
            acquired.Reservation.FencingToken + 1,
            "coordinator",
            "done");

        Assert.IsFalse(released);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
