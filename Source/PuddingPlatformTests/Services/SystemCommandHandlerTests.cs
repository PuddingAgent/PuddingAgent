using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Conversation;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class SystemCommandHandlerTests
{
    [TestMethod]
    public async Task Yolo_PersistsSystemTranscript_WithoutCreatingAgentExecution()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var runtime = new RuntimeControlService();
        var handler = new SystemCommandHandler(
            db,
            runtime,
            NullLogger<SystemCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            new SystemCommandRequest(
                ConversationId: "conversation-1",
                WorkspaceId: "default",
                AgentId: "agent-1",
                UserId: "admin",
                ClientRequestId: "request-1",
                ClientMessageId: "user-message-1",
                ResponseMessageId: "system-message-1",
                CommandText: "/yolo"));

        Assert.AreEqual("Yolo", result.RuntimeMode);
        Assert.AreEqual(RuntimeExecutionMode.Yolo, runtime.Mode);
        Assert.AreEqual(2, await db.ChatMessages.CountAsync());
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(0, await db.ConversationTurns.CountAsync());

        var response = await db.ChatMessages
            .SingleAsync(message => message.MessageId == "system-message-1");
        Assert.AreEqual("agent", response.Role);
        StringAssert.Contains(response.MetadataJson, "\"sourceType\":\"system_command\"");
    }

    [TestMethod]
    public async Task Yolo_IsIdempotentByClientRequestAndResponseMessage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var runtime = new RuntimeControlService();
        var handler = new SystemCommandHandler(
            db,
            runtime,
            NullLogger<SystemCommandHandler>.Instance);
        var request = new SystemCommandRequest(
            "conversation-1",
            "default",
            "agent-1",
            "admin",
            "request-1",
            "user-message-1",
            "system-message-1",
            "/yolo");

        await handler.HandleAsync(request);
        runtime.SetMode(RuntimeExecutionMode.Normal, "simulate process-local state reset");
        await handler.HandleAsync(request);

        Assert.AreEqual(2, await db.ChatMessages.CountAsync());
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(RuntimeExecutionMode.Yolo, runtime.Mode);
    }

    [TestMethod]
    public async Task Yolo_FromNonWhitelistedFeishuUser_IsRecordedButDoesNotChangeMode()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var runtime = new RuntimeControlService();
        var handler = new SystemCommandHandler(
            db,
            runtime,
            NullLogger<SystemCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            new SystemCommandRequest(
                ConversationId: "conversation-feishu",
                WorkspaceId: "default",
                AgentId: "agent-1",
                UserId: "gateway:user-hash",
                ClientRequestId: "request-denied",
                ClientMessageId: "user-message-denied",
                ResponseMessageId: "system-message-denied",
                CommandText: "/yolo",
                IsPrivilegedUser: false,
                SourceChannel: "feishu",
                ExternalUserId: "ou_not_allowed"));

        Assert.AreEqual(RuntimeExecutionMode.Normal, runtime.Mode);
        Assert.AreEqual("Normal", result.RuntimeMode);
        Assert.IsFalse(result.ForwardToAgent);
        StringAssert.Contains(result.Message, "Permission denied");
        Assert.AreEqual(2, await db.ChatMessages.CountAsync());
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(0, await db.ConversationTurns.CountAsync());

        var response = await db.ChatMessages.SingleAsync(message =>
            message.MessageId == "system-message-denied");
        StringAssert.Contains(response.MetadataJson, "\"sourceChannel\":\"feishu\"");
        StringAssert.Contains(response.MetadataJson, "\"privilegedUser\":false");
    }

    [TestMethod]
    public async Task Help_FromNonWhitelistedFeishuUser_RemainsReadOnlyAndAvailable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var runtime = new RuntimeControlService();
        var handler = new SystemCommandHandler(
            db,
            runtime,
            NullLogger<SystemCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            new SystemCommandRequest(
                ConversationId: "conversation-feishu-help",
                WorkspaceId: "default",
                AgentId: "agent-1",
                UserId: "gateway:user-hash",
                ClientRequestId: "request-help",
                ClientMessageId: "user-message-help",
                ResponseMessageId: "system-message-help",
                CommandText: "/help",
                IsPrivilegedUser: false,
                SourceChannel: "feishu",
                ExternalUserId: "ou_not_allowed"));

        Assert.AreEqual(RuntimeExecutionMode.Normal, runtime.Mode);
        Assert.IsFalse(result.ForwardToAgent);
        StringAssert.Contains(result.Message, "System commands:");
        Assert.AreEqual(2, await db.ChatMessages.CountAsync());
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(0, await db.ConversationTurns.CountAsync());
    }

    [TestMethod]
    public async Task WhoAmI_FromFeishu_EchoesExternalUserIdWithoutAgentExecution()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var runtime = new RuntimeControlService();
        var handler = new SystemCommandHandler(
            db,
            runtime,
            NullLogger<SystemCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            new SystemCommandRequest(
                ConversationId: "conversation-feishu-whoami",
                WorkspaceId: "default",
                AgentId: "agent-1",
                UserId: "gateway:user-hash",
                ClientRequestId: "request-whoami",
                ClientMessageId: "user-message-whoami",
                ResponseMessageId: "system-message-whoami",
                CommandText: "/whoami",
                IsPrivilegedUser: false,
                SourceChannel: "feishu",
                ExternalUserId: "ou_current_sender"));

        Assert.AreEqual(RuntimeExecutionMode.Normal, runtime.Mode);
        Assert.IsFalse(result.ForwardToAgent);
        StringAssert.Contains(result.Message, "open_id");
        StringAssert.Contains(result.Message, "ou_current_sender");
        Assert.AreEqual(2, await db.ChatMessages.CountAsync());
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(0, await db.ConversationTurns.CountAsync());
    }
}
