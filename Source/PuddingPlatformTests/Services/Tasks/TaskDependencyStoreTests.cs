using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatformTests.Services.Tasks;

[TestClass]
public sealed class TaskDependencyStoreTests
{
    private string _root = null!;
    private PlatformDbContextFactory _factory = null!;
    private TaskDependencyStore _store = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "PuddingAgent", "task-dependency-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "platform.db")};Default Timeout=10")
            .Options;
        _factory = new PlatformDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        db.WorkspaceTasks.AddRange(
            Task("a", WorkspaceTaskStatus.Ready),
            Task("b", WorkspaceTaskStatus.Ready),
            Task("c", WorkspaceTaskStatus.Ready));
        await db.SaveChangesAsync();
        _store = new TaskDependencyStore(_factory, TimeProvider.System);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task UnfinishedPredecessorMakesSuccessorWait()
    {
        await _store.AddAsync("ws", "a", "b");

        var evaluation = await _store.EvaluateAsync("ws", "b");

        Assert.AreEqual(TaskDependencyEvaluationState.Waiting, evaluation.State);
        CollectionAssert.AreEqual(new[] { "a" }, evaluation.WaitingOnTaskIds.ToArray());
    }

    [TestMethod]
    public async Task CompletedPredecessorMakesSuccessorEligible()
    {
        await _store.AddAsync("ws", "a", "b");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.WorkspaceTasks.Where(item => item.TaskId == "a")
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, WorkspaceTaskStatus.Completed));
        }

        var evaluation = await _store.EvaluateAsync("ws", "b");

        Assert.AreEqual(TaskDependencyEvaluationState.Satisfied, evaluation.State);
    }

    [TestMethod]
    public async Task FailedPredecessorIsBroken_NotAnInfiniteWait()
    {
        await _store.AddAsync("ws", "a", "b");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.WorkspaceTasks.Where(item => item.TaskId == "a")
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, WorkspaceTaskStatus.Failed));
        }

        var evaluation = await _store.EvaluateAsync("ws", "b");

        Assert.AreEqual(TaskDependencyEvaluationState.Broken, evaluation.State);
        CollectionAssert.AreEqual(new[] { "a" }, evaluation.BrokenByTaskIds.ToArray());
    }

    [TestMethod]
    public async Task AddRejectsDependencyCycle()
    {
        await _store.AddAsync("ws", "a", "b");
        await _store.AddAsync("ws", "b", "c");

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _store.AddAsync("ws", "c", "a"));

        Assert.AreEqual("task_dependency_cycle", error.Message);
    }

    [TestMethod]
    public async Task EvaluateMissingTaskFailsClosed()
    {
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _store.EvaluateAsync("ws", "missing"));

        Assert.AreEqual("task_dependency_task_not_found", error.Message);
    }

    private static WorkspaceTaskEntity Task(string id, WorkspaceTaskStatus status) => new()
    {
        TaskId = id,
        WorkspaceId = "ws",
        Title = id,
        Status = status,
        Priority = TaskPriority.P3,
        ExecutionWindow = TaskExecutionWindow.Anytime,
        SortOrder = 0,
        Version = 1,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };
}
