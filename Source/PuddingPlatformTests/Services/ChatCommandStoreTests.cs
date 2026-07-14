using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class ChatCommandStoreTests
{
    private SqliteConnection _connection = null!;
    private ChatCommandStore _store = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        _store = ChatCommandStoreFactory.Create(_connection, NullLogger<ChatCommandStore>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    [TestMethod]
    public async Task SaveAsync_Persists_Command()
    {
        var command = NewCommand("cmd-1", "workspace-1");
        var saved = await _store.SaveAsync(command);

        Assert.AreEqual(command.CommandId, saved.CommandId);
        Assert.AreEqual("pending", saved.Status);
        Assert.IsTrue(saved.CreatedAt > 0);
    }

    [TestMethod]
    public async Task GetAsync_Returns_Saved_Command()
    {
        var command = NewCommand("cmd-1", "workspace-1");
        await _store.SaveAsync(command);

        var retrieved = await _store.GetAsync("cmd-1");

        Assert.IsNotNull(retrieved);
        Assert.AreEqual(command.CommandId, retrieved.CommandId);
        Assert.AreEqual(command.WorkspaceId, retrieved.WorkspaceId);
        Assert.AreEqual(command.PayloadJson, retrieved.PayloadJson);
    }

    [TestMethod]
    public async Task GetAsync_Returns_Null_For_Unknown_Id()
    {
        var result = await _store.GetAsync("nonexistent");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task FindByClientRequestIdAsync_Returns_Existing()
    {
        var command = NewCommand("cmd-1", "workspace-1", clientRequestId: "req-abc");
        await _store.SaveAsync(command);

        var found = await _store.FindByClientRequestIdAsync("req-abc", "workspace-1");

        Assert.IsNotNull(found);
        Assert.AreEqual("cmd-1", found.CommandId);
    }

    [TestMethod]
    public async Task FindByClientRequestIdAsync_Returns_Null_For_Different_Workspace()
    {
        var command = NewCommand("cmd-1", "workspace-1", clientRequestId: "req-abc");
        await _store.SaveAsync(command);

        var found = await _store.FindByClientRequestIdAsync("req-abc", "workspace-2");

        Assert.IsNull(found);
    }

    [TestMethod]
    public async Task LeaseNextAsync_Claims_Pending_Command()
    {
        await _store.SaveAsync(NewCommand("cmd-1", "ws"));

        var leased = await _store.LeaseNextAsync("worker-1", 60_000);

        Assert.IsNotNull(leased);
        Assert.AreEqual("cmd-1", leased.CommandId);
        Assert.AreEqual("running", leased.Status);
        Assert.AreEqual("worker-1", leased.LeaseOwner);
        Assert.IsTrue(leased.LeaseUntil > 0);
        Assert.AreEqual(1, leased.AttemptCount);
    }

    [TestMethod]
    public async Task LeaseNextAsync_Returns_Null_When_No_Pending()
    {
        var leased = await _store.LeaseNextAsync("worker-1", 60_000);
        Assert.IsNull(leased);
    }

    [TestMethod]
    public async Task LeaseNextAsync_Skips_Already_Leased_Commands()
    {
        await _store.SaveAsync(NewCommand("cmd-1", "ws"));
        await _store.SaveAsync(NewCommand("cmd-2", "ws"));

        var first = await _store.LeaseNextAsync("worker-1", 60_000);
        Assert.IsNotNull(first);

        var second = await _store.LeaseNextAsync("worker-2", 60_000);
        Assert.IsNotNull(second);
        Assert.AreNotEqual(first.CommandId, second.CommandId,
            "Second lease should claim a different command.");
    }

    [TestMethod]
    public async Task LeaseNextAsync_Picks_Up_Expired_Lease()
    {
        var command = NewCommand("cmd-1", "ws");
        await _store.SaveAsync(command);

        // Initial lease with very short duration
        await _store.LeaseNextAsync("dead-worker", leaseDurationMs: 1);

        // Wait for lease to expire
        await Task.Delay(50);

        // Second worker should pick it up
        var retried = await _store.LeaseNextAsync("worker-2", 60_000);
        Assert.IsNotNull(retried);
        Assert.AreEqual("cmd-1", retried.CommandId);
        Assert.AreEqual(2, retried.AttemptCount,
            "Attempt count should increment when re-leased after expiry.");
    }

    [TestMethod]
    public async Task CompleteAsync_Marks_Succeeded()
    {
        await _store.SaveAsync(NewCommand("cmd-1", "ws"));
        await _store.LeaseNextAsync("worker-1", 60_000);

        await _store.CompleteAsync("cmd-1", "succeeded");

        var completed = await _store.GetAsync("cmd-1");
        Assert.IsNotNull(completed);
        Assert.AreEqual("succeeded", completed.Status);
        Assert.IsTrue(completed.CompletedAt > 0);
    }

    [TestMethod]
    public async Task CompleteAsync_Marks_Failed()
    {
        await _store.SaveAsync(NewCommand("cmd-1", "ws"));
        await _store.LeaseNextAsync("worker-1", 60_000);

        await _store.CompleteAsync("cmd-1", "failed", "LLM timeout");

        var completed = await _store.GetAsync("cmd-1");
        Assert.AreEqual("failed", completed.Status);
        Assert.AreEqual("LLM timeout", completed.LastError);
    }

    [TestMethod]
    public async Task ReleaseLeaseAsync_Resets_To_Pending()
    {
        await _store.SaveAsync(NewCommand("cmd-1", "ws"));
        await _store.LeaseNextAsync("worker-1", 60_000);

        await _store.ReleaseLeaseAsync("cmd-1");

        var released = await _store.GetAsync("cmd-1");
        Assert.AreEqual("pending", released!.Status);
        Assert.IsNull(released.LeaseOwner);
        Assert.IsNull(released.LeaseUntil);
    }

    [TestMethod]
    public async Task UpdateStatusAsync_Changes_Status()
    {
        await _store.SaveAsync(NewCommand("cmd-1", "ws"));
        await _store.LeaseNextAsync("worker-1", 60_000);

        await _store.UpdateStatusAsync("cmd-1", "cancelled", "User cancelled");

        var updated = await _store.GetAsync("cmd-1");
        Assert.AreEqual("cancelled", updated!.Status);
        Assert.AreEqual("User cancelled", updated.LastError);
    }

    private static ChatCommandRecord NewCommand(
        string commandId,
        string workspaceId,
        string? clientRequestId = null)
        => new()
        {
            CommandId = commandId,
            ClientRequestId = clientRequestId,
            WorkspaceId = workspaceId,
            SessionId = $"session-{commandId}",
            MessageId = $"msg-{commandId}",
            TurnId = $"turn-{commandId}",
            AgentInstanceId = "agent-1",
            AgentTemplateId = "template-1",
            UserId = "user-1",
            PayloadJson = """{"messageText":"Hello"}""",
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
}
