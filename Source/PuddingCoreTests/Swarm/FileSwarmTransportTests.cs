using System.Collections.Concurrent;
using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Swarm;

namespace PuddingCodeTests.Swarm;

[TestClass]
public sealed class FileSwarmTransportTests : IDisposable
{
    private readonly string _testSwarmDir;
    private readonly string _testNodeId = "test-node-1";
    private readonly FileSwarmTransport _transport;

    public FileSwarmTransportTests()
    {
        // 创建临时测试目录
        _testSwarmDir = Path.Combine(Path.GetTempPath(), $"pudding-test-{Guid.NewGuid()}", "swarm");
        Directory.CreateDirectory(_testSwarmDir);
        Directory.CreateDirectory(Path.Combine(_testSwarmDir, "messages"));

        _transport = new FileSwarmTransport(_testSwarmDir, _testNodeId);
    }

    [TestMethod]
    public async Task SendAsync_CreatesInboxFile_WhenFirstMessage()
    {
        // Arrange
        var targetNode = "worker-1";
        var message = CreateTestMessage("leader", targetNode);

        // Act
        await _transport.SendAsync(targetNode, message);

        // Assert
        var inboxPath = Path.Combine(_testSwarmDir, "messages", $"{targetNode}.inbox.json");
        Assert.IsTrue(File.Exists(inboxPath), "Inbox file should be created");

        var json = await File.ReadAllTextAsync(inboxPath);
        var messages = JsonSerializer.Deserialize<List<SwarmMessage>>(json);

        Assert.IsNotNull(messages);
        Assert.HasCount(1, messages);
        Assert.AreEqual(message.Id, messages[0].Id);
    }

    [TestMethod]
    public async Task SendAsync_AppendsToExistingMessages_WhenInboxExists()
    {
        // Arrange
        var targetNode = "worker-2";
        var message1 = CreateTestMessage("leader", targetNode);
        var message2 = CreateTestMessage("leader", targetNode);

        // Act
        await _transport.SendAsync(targetNode, message1);
        await _transport.SendAsync(targetNode, message2);

        // Assert
        var inboxPath = Path.Combine(_testSwarmDir, "messages", $"{targetNode}.inbox.json");
        var json = await File.ReadAllTextAsync(inboxPath);
        var messages = JsonSerializer.Deserialize<List<SwarmMessage>>(json);

        Assert.IsNotNull(messages);
        Assert.HasCount(2, messages);
        Assert.AreEqual(message1.Id, messages[0].Id);
        Assert.AreEqual(message2.Id, messages[1].Id);
    }

    [TestMethod]
    public async Task BroadcastAsync_CreatesBroadcastFile()
    {
        // Arrange
        var message = CreateTestMessage("leader", null);

        // Act
        await _transport.BroadcastAsync(message);

        // Assert
        var broadcastPath = Path.Combine(_testSwarmDir, "messages", "broadcast.json");
        Assert.IsTrue(File.Exists(broadcastPath), "Broadcast file should be created");

        var json = await File.ReadAllTextAsync(broadcastPath);
        var messages = JsonSerializer.Deserialize<List<SwarmMessage>>(json);

        Assert.IsNotNull(messages);
        Assert.HasCount(1, messages);
        Assert.AreEqual(message.Id, messages[0].Id);
    }

    [TestMethod]
    public async Task BroadcastAsync_AppendsToExistingBroadcasts()
    {
        // Arrange
        var message1 = CreateTestMessage("leader", null);
        var message2 = CreateTestMessage("leader", null);

        // Act
        await _transport.BroadcastAsync(message1);
        await _transport.BroadcastAsync(message2);

        // Assert
        var broadcastPath = Path.Combine(_testSwarmDir, "messages", "broadcast.json");
        var json = await File.ReadAllTextAsync(broadcastPath);
        var messages = JsonSerializer.Deserialize<List<SwarmMessage>>(json);

        Assert.IsNotNull(messages);
        Assert.HasCount(2, messages);
    }

    [TestMethod]
    public async Task ReceiveAsync_ReturnsExistingMessages()
    {
        // Arrange
        var message = CreateTestMessage("sender", _testNodeId);
        var inboxPath = Path.Combine(_testSwarmDir, "messages", $"{_testNodeId}.inbox.json");

        // Pre-populate inbox
        await File.WriteAllTextAsync(inboxPath, JsonSerializer.Serialize(new[] { message }));

        // Act
        var receivedMessages = new List<SwarmMessage>();
        await foreach (var msg in _transport.ReceiveAsync())
        {
            receivedMessages.Add(msg);
        }

        // Assert
        Assert.HasCount(1, receivedMessages);
        Assert.AreEqual(message.Id, receivedMessages[0].Id);
    }

    [TestMethod]
    public async Task ReceiveAsync_ClearsInboxAfterReading()
    {
        // Arrange
        var message = CreateTestMessage("sender", _testNodeId);
        var inboxPath = Path.Combine(_testSwarmDir, "messages", $"{_testNodeId}.inbox.json");

        // Pre-populate inbox
        await File.WriteAllTextAsync(inboxPath, JsonSerializer.Serialize(new[] { message }));

        // Act - Consume all messages
        await foreach (var _ in _transport.ReceiveAsync())
        {
            // Process all messages until cancelled
        }

        // Small delay to allow file write
        await Task.Delay(100);

        // Assert - Inbox should be empty after reading
        var json = await File.ReadAllTextAsync(inboxPath);
        var messages = JsonSerializer.Deserialize<List<SwarmMessage>>(json);

        Assert.IsNotNull(messages);
        Assert.IsEmpty(messages);
    }

    [TestMethod]
    public async Task FileSystemWatcher_DeliversNewMessages()
    {
        // Arrange
        var senderNode = "sender-node";
        var senderTransport = new FileSwarmTransport(_testSwarmDir, senderNode);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        try
        {
            // Act - Send a message first
            var testMessage = CreateTestMessage(senderNode, _testNodeId);
            await senderTransport.SendAsync(_testNodeId, testMessage);

            // Small delay to allow file write
            await Task.Delay(100);

            // Now receive messages
            var receivedMessages = new List<SwarmMessage>();
            await foreach (var msg in _transport.ReceiveAsync(cts.Token))
            {
                receivedMessages.Add(msg);
                break; // Get first message then stop
            }

            // Assert
            Assert.IsTrue(receivedMessages.Any(), "Should have received at least one message");
            Assert.IsTrue(receivedMessages.Any(m => m.Id == testMessage.Id), "Should have received the test message");
        }
        finally
        {
            // Cleanup
            await senderTransport.DisposeAsync();
            cts.Cancel();
        }
    }

    [TestMethod]
    public void Constructor_CreatesMessagesDirectory_WhenNotExists()
    {
        // Arrange
        var newSwarmDir = Path.Combine(Path.GetTempPath(), $"pudding-test-{Guid.NewGuid()}", "swarm");

        // Act
        var transport = new FileSwarmTransport(newSwarmDir, "test-node");

        // Assert
        Assert.IsTrue(Directory.Exists(Path.Combine(newSwarmDir, "messages")));
        
        // Cleanup
        transport.DisposeAsync().AsTask().Wait();
        if (Directory.Exists(newSwarmDir))
        {
            Directory.Delete(newSwarmDir, true);
        }
    }

    [TestMethod]
    public void SendAsync_ThrowsArgumentNullException_WhenTargetNodeIdIsNull()
    {
        // Arrange
        var message = CreateTestMessage("leader", null);

        // Act & Assert
        var ex = Assert.ThrowsExactly<AggregateException>(() => _transport.SendAsync(null!, message).Wait());
        Assert.IsInstanceOfType<ArgumentNullException>(ex.InnerException);
    }

    [TestMethod]
    public void SendAsync_ThrowsArgumentNullException_WhenMessageIsNull()
    {
        // Act & Assert
        var ex = Assert.ThrowsExactly<AggregateException>(() => _transport.SendAsync("worker-1", null!).Wait());
        Assert.IsInstanceOfType<ArgumentNullException>(ex.InnerException);
    }

    [TestMethod]
    public void BroadcastAsync_ThrowsArgumentNullException_WhenMessageIsNull()
    {
        // Act & Assert
        var ex = Assert.ThrowsExactly<AggregateException>(() => _transport.BroadcastAsync(null!).Wait());
        Assert.IsInstanceOfType<ArgumentNullException>(ex.InnerException);
    }

    public void Dispose()
    {
        _transport.DisposeAsync().AsTask().Wait();

        // Cleanup test directory
        if (Directory.Exists(_testSwarmDir))
        {
            try
            {
                Directory.Delete(_testSwarmDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    private static SwarmMessage CreateTestMessage(string from, string? to)
    {
        return new SwarmMessage(
            Id: Guid.NewGuid().ToString("N"),
            From: from,
            To: to,
            Type: "Test",
            Content: $"Test message content {Guid.NewGuid():N}",
            Timestamp: DateTimeOffset.Now
        );
    }
}
