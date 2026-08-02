using PuddingBrowser.Protocol;
using PuddingHost.BrowserBridge;

namespace PuddingHost.Tests.BrowserBridge;

/// <summary>
/// Tests for DesktopBrowserCommandBroker: generation isolation, pending management,
/// duplicate operation id, deadline, and cancellation semantics.
/// </summary>
public class DesktopBrowserCommandBrokerTests
{
    private static BrowserBridgeCommand MakeCommand(
        string name = "page.goto",
        Guid? operationId = null,
        int deadlineSeconds = 30)
    {
        return new BrowserBridgeCommand
        {
            OperationId = operationId ?? Guid.NewGuid(),
            Name = name,
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(deadlineSeconds),
            Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { url = "https://example.com" })
        };
    }

    [Fact]
    public void IsDesktopConnected_ReturnsFalse_WhenNoConnection()
    {
        var registry = new DesktopBrowserConnectionRegistry();
        var broker = new DesktopBrowserCommandBroker(registry);

        Assert.False(broker.IsDesktopConnected);
    }

    [Fact]
    public void IsDesktopConnected_ReturnsFalse_BeforeHelloAccepted()
    {
        var registry = new DesktopBrowserConnectionRegistry();
        var broker = new DesktopBrowserCommandBroker(registry);

        var connection = new DesktopBrowserConnection(Guid.NewGuid(), 1);
        registry.TryAttach(connection);

        // Not accepted yet
        Assert.False(broker.IsDesktopConnected);
    }

    [Fact]
    public void IsDesktopConnected_ReturnsTrue_AfterHelloAccepted()
    {
        var registry = new DesktopBrowserConnectionRegistry();
        var broker = new DesktopBrowserCommandBroker(registry);

        var connection = new DesktopBrowserConnection(Guid.NewGuid(), 1);
        registry.TryAttach(connection);
        connection.TryAcceptHello(new BrowserBridgeHello
        {
            ProtocolVersion = BrowserBridgeProtocol.CurrentVersion,
            DesktopInstanceId = "test",
            Capabilities = ["context"]
        }, out _);

        Assert.True(broker.IsDesktopConnected);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsNotAvailable_WhenNoConnection()
    {
        var registry = new DesktopBrowserConnectionRegistry();
        var broker = new DesktopBrowserCommandBroker(registry);

        var result = await broker.ExecuteAsync(MakeCommand(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserNotAvailable, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsNotSupported_ForUnknownCommand()
    {
        var registry = new DesktopBrowserConnectionRegistry();
        var broker = new DesktopBrowserCommandBroker(registry);
        var connection = CreateAcceptedConnection(registry);

        var result = await broker.ExecuteAsync(MakeCommand("unknown.cmd"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserOperationNotSupported, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsDeadlineExceeded_WhenDeadlinePassed()
    {
        var registry = new DesktopBrowserConnectionRegistry();
        var broker = new DesktopBrowserCommandBroker(registry);
        CreateAcceptedConnection(registry);

        var result = await broker.ExecuteAsync(MakeCommand(deadlineSeconds: -1), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserDeadlineExceeded, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsDuplicate_ForSameOperationId()
    {
        var registry = new DesktopBrowserConnectionRegistry();
        var broker = new DesktopBrowserCommandBroker(registry);
        CreateAcceptedConnection(registry);

        var opId = Guid.NewGuid();
        var cmd1 = MakeCommand(operationId: opId);
        var cmd2 = MakeCommand(operationId: opId);

        // First command will be pending (no result comes back)
        var task1 = broker.ExecuteAsync(cmd1, CancellationToken.None);

        // Second command with same id should return duplicate error
        var result2 = await broker.ExecuteAsync(cmd2, CancellationToken.None);

        Assert.False(result2.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserInvalidCommand, result2.ErrorCode);
        Assert.Contains("Duplicate", result2.ErrorMessage);

        // Clean up: fail the first pending
        broker.FailPendingForConnection(registry.Current!.ConnectionId, 1,
            BrowserBridgeErrorCodes.BrowserBridgeDisconnected, "test cleanup");
        await task1;
    }

    [Fact]
    public async Task HandleResult_CompletesPending_WhenGenerationMatches()
    {
        var registry = new DesktopBrowserConnectionRegistry();
        var broker = new DesktopBrowserCommandBroker(registry);
        var connection = CreateAcceptedConnection(registry);

        var cmd = MakeCommand();
        var task = broker.ExecuteAsync(cmd, CancellationToken.None);

        // Simulate result from Desktop
        var result = new BrowserBridgeCommandResult
        {
            OperationId = cmd.OperationId,
            Success = true,
            Value = System.Text.Json.JsonSerializer.SerializeToElement(new { ok = true })
        };
        broker.HandleResult(connection.ConnectionId, connection.Generation, result);

        var completed = await task;
        Assert.True(completed.Success);
    }

    [Fact]
    public async Task HandleResult_IgnoresStale_WhenGenerationMismatch()
    {
        var registry = new DesktopBrowserConnectionRegistry();
        var broker = new DesktopBrowserCommandBroker(registry);
        var connection = CreateAcceptedConnection(registry);

        var cmd = MakeCommand();
        var task = broker.ExecuteAsync(cmd, CancellationToken.None);

        // Simulate result from WRONG generation
        var result = new BrowserBridgeCommandResult
        {
            OperationId = cmd.OperationId,
            Success = true
        };
        broker.HandleResult(connection.ConnectionId, 999, result); // wrong gen

        // Should still be pending — fail it to complete the test
        broker.FailPendingForConnection(connection.ConnectionId, connection.Generation,
            BrowserBridgeErrorCodes.BrowserBridgeDisconnected, "test");

        var completed = await task;
        Assert.False(completed.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserBridgeDisconnected, completed.ErrorCode);
    }

    [Fact]
    public async Task FailPendingForConnection_OnlyFailsMatchingGeneration()
    {
        var registry = new DesktopBrowserConnectionRegistry();
        var broker = new DesktopBrowserCommandBroker(registry);
        var connection = CreateAcceptedConnection(registry);

        var cmd = MakeCommand();
        var task = broker.ExecuteAsync(cmd, CancellationToken.None);

        // Fail with WRONG generation — should not affect pending
        broker.FailPendingForConnection(connection.ConnectionId, 999,
            BrowserBridgeErrorCodes.BrowserBridgeDisconnected, "wrong gen");

        // Pending should still be alive — now fail with correct generation
        broker.FailPendingForConnection(connection.ConnectionId, connection.Generation,
            BrowserBridgeErrorCodes.BrowserBridgeDisconnected, "correct gen");

        var completed = await task;
        Assert.False(completed.Success);
        Assert.Equal("correct gen", completed.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCancelled_WhenCallerCancels()
    {
        var registry = new DesktopBrowserConnectionRegistry();
        var broker = new DesktopBrowserCommandBroker(registry);
        CreateAcceptedConnection(registry);

        using var cts = new CancellationTokenSource();
        var cmd = MakeCommand();
        var task = broker.ExecuteAsync(cmd, cts.Token);

        cts.Cancel();

        var result = await task;
        Assert.False(result.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserCancelled, result.ErrorCode);
    }

    private static DesktopBrowserConnection CreateAcceptedConnection(
        DesktopBrowserConnectionRegistry registry)
    {
        var connection = new DesktopBrowserConnection(Guid.NewGuid(), registry.NextGeneration());
        registry.TryAttach(connection);
        connection.TryAcceptHello(new BrowserBridgeHello
        {
            ProtocolVersion = BrowserBridgeProtocol.CurrentVersion,
            DesktopInstanceId = "test-desktop",
            Capabilities = ["context", "page", "navigation"]
        }, out _);
        return connection;
    }
}
