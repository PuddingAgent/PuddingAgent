using System.Text.Json;
using System.Security.AccessControl;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;
using PuddingAgent.Connectors;
using PuddingAgent.Controllers;
using PuddingAgent.Services;
using PuddingAgent.Tools;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Services;
using PuddingCode.Platform;
using PuddingController.Services;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.MessageGateway;
using PuddingSlack;

namespace PuddingAgent.IntegrationTests.Slack;

[TestClass]
public sealed class SlackGatewayTests
{
    [TestMethod]
    public async Task DirectoryBeforeConnectorStartDoesNotClaimCredentialRejection()
    {
        var connector = new SlackConnector("channel", "default", "agent", Settings,
            "unused-directory", NullLogger<SlackConnector>.Instance);
        var result = await connector.OperateAsync("lookup_member", new() { ["query"] = "dot" });
        Assert.IsFalse(result.Success);
        Assert.AreEqual("slack_connector_not_ready", result.Error);
    }

    [TestMethod]
    [DataRow("valid", true)]
    [DataRow("delegated", true)]
    [DataRow("wrong_workspace", false)]
    [DataRow("wrong_agent", false)]
    [DataRow("wrong_conversation", false)]
    [DataRow("not_slack", false)]
    [DataRow("wrong_connector", false)]
    [DataRow("missing_identity", false)]
    [DataRow("missing_scope", false)]
    [DataRow("starting", false)]
    public async Task MemberLookupUsesOnlyTheTrustedCurrentRouteAndNeverSends(string scenario, bool success)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection().AddDbContext<PlatformDbContext>(o => o.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
            {
                CommandId = "command", BatchId = "batch", WorkspaceId = "default", SessionId = "conversation", AgentInstanceId = "agent",
                MessageId = "assistant", UserMessageId = "user", TurnId = "turn", Status = "running",
                MetadataJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    [MessageGatewayMetadata.IsGatewayIngress] = "true",
                    [MessageGatewayMetadata.ChannelType] = scenario == "not_slack" ? "feishu" : "slack",
                    [MessageGatewayMetadata.ConnectorId] = scenario == "wrong_connector" ? "slack:other" : "slack:channel",
                    [MessageGatewayMetadata.ChannelId] = "channel", [SlackChannelMetadata.TeamId] = "T1"
                })
            });
            await db.SaveChangesAsync();
        }
        var connector = new Mock<IPuddingConnector>(MockBehavior.Strict);
        connector.SetupGet(c => c.Descriptor).Returns(new ConnectorDescriptor
        { ConnectorId = "slack:channel", ConnectorType = "slack", Protocol = "socket_mode" });
        connector.Setup(c => c.StartAsync(It.IsAny<ConnectorContext>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connector.Setup(c => c.OperateAsync("lookup_member", It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, Dictionary<string, string> p, CancellationToken _) =>
            {
                Assert.AreEqual("dot", p["query"]); Assert.AreEqual("T1", p["teamId"]);
                Assert.AreEqual("channel", p["channelId"]); Assert.AreEqual("default", p["workspaceId"]); Assert.AreEqual("agent", p["agentId"]);
                return Task.FromResult(scenario == "missing_scope" ? new ConnectorOperationResult { Error = "directory_requires_users_read" }
                    : scenario == "starting" ? new ConnectorOperationResult { Error = "slack_connector_not_ready" }
                    : new ConnectorOperationResult { Success = true, Data = "{\"status\":\"resolved\",\"mention\":\"<@UDOT>\"}" });
            });
        var host = new ConnectorHost((_, _) => Task.CompletedTask, NullLogger<ConnectorHost>.Instance);
        host.Register(connector.Object); await host.StartAsync("slack:channel");
        var result = await new SlackLookupMemberTool(provider.GetRequiredService<IServiceScopeFactory>(), host).ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call", ArgumentsJson = """{"query":"dot","connectorId":"slack:other","teamId":"TOTHER"}""",
            Context = new ToolExecutionContext
            {
                WorkspaceId = scenario == "wrong_workspace" ? "other" : "default", SessionId = "conversation",
                AgentInstanceId = scenario == "delegated" ? "child" : scenario == "wrong_agent" ? "other" : "agent",
                ConfigurationAgentInstanceId = scenario == "delegated" ? "agent" : null,
                ExecutionIdentity = scenario == "missing_identity" ? null : new RuntimeExecutionIdentity
                {
                    Kind = scenario == "delegated" ? RuntimeExecutionKind.SubAgent : RuntimeExecutionKind.ConversationTurn,
                    ParentRunId = scenario == "delegated" ? "parent" : null,
                    ConversationId = scenario == "wrong_conversation" ? "other" : "conversation", CommandId = "command",
                    TurnId = "turn", RunId = "run", ToolCallId = "call", TraceId = null
                }
            }
        });
        Assert.AreEqual(success, result.Success, result.Error);
        if (success) Assert.Contains("<@UDOT>", result.Output!);
        if (scenario == "missing_scope") Assert.Contains("users:read", result.Error!);
        if (scenario == "starting") Assert.Contains("does not indicate invalid credentials", result.Error!);
        connector.Verify(c => c.OperateAsync("lookup_member", It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()),
            success || scenario is "missing_scope" or "starting" ? Times.Once() : Times.Never());
        connector.Verify(c => c.SendAsync(It.IsAny<ConnectorMessage>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    private static SlackChannelSettings Settings => new()
    {
        TeamId = "T1", AppId = "A1", CredentialFile = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "Keys", "slack-test.json"),
        AllowedUserIds = ["U1"], AllowedChannelIds = ["C1"]
    };

    [TestMethod]
    public void RotatingCredentialFilePreservesAclAndUnknownFieldsWithExclusiveOwnership()
    {
        var root = TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "credentials.json");
            File.WriteAllText(path, "{\"appToken\":\"xoxe.xapp-old\",\"botToken\":\"xoxb-bot\",\"appRefreshToken\":\"refresh-old\",\"userNote\":\"preserve\"}");
            var acl = OperatingSystem.IsWindows() ? new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access) : null;
            using var store = new SlackCredentialFile(path);
            Assert.Throws<IOException>(() => new SlackCredentialFile(path));
            var before = store.Read(); var next = before.Credentials.Copy();
            next.AppToken = "xoxe.xapp-new"; next.AppRefreshToken = "refresh-new";
            store.Write(next, before.Revision);
            var actual = store.Read().Credentials;
            Assert.AreEqual("refresh-new", actual.AppRefreshToken);
            Assert.AreEqual("xoxe.xapp-new", actual.AppToken);
            Assert.AreEqual("preserve", actual.AdditionalFields!["userNote"].GetString());
            Assert.HasCount(0, Directory.GetFiles(root, "*.tmp"));
            if (OperatingSystem.IsWindows())
                Assert.AreEqual(acl, new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            Assert.Throws<IOException>(() => store.Write(before.Credentials, before.Revision));
            Assert.AreEqual("refresh-new", store.Read().Credentials.AppRefreshToken);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ConfigurationRegistersProviderPersistsOnlyReferenceAndRejectsDuplicateApp()
    {
        var root = TemporaryDirectory();
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            var service = Config(paths);
            var providers = await service.ListProvidersAsync();
            Assert.IsTrue(providers.Any(p => p.ChannelType == "slack"));
            Assert.IsTrue(providers.Any(p => p.ChannelType == "feishu"));
            var request = new UpsertWorkspaceChannelRequest("Slack", null, "slack", null, null, null, false, [], true, Slack: Settings);
            var created = await service.CreateWorkspaceChannelAsync("default", request);
            Assert.AreEqual("T1", created.Slack!.TeamId);
            Assert.IsNull((await service.GetChannelAsync(created.ChannelId))!.Feishu);
            var json = await File.ReadAllTextAsync(paths.ChannelManifestFile(created.ChannelId));
            Assert.DoesNotContain("botToken", json);
            Assert.DoesNotContain("appToken", JsonSerializer.Serialize(created));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateWorkspaceChannelAsync("default", request));
            var updated = await service.UpdateWorkspaceChannelAsync("default", created.ChannelId,
                request with { Slack = Settings with { AllowedUserIds = [] } });
            Assert.HasCount(0, updated.Slack!.AllowedUserIds);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ConfigurationRejectsWorkspaceNamesAndGitCredentialPaths()
    {
        Assert.Throws<ArgumentException>(() => SlackChannelValidation.Normalize(Settings with { TeamId = "puddingagent" }));
        var root = TemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Assert.Throws<ArgumentException>(() => SlackChannelValidation.Normalize(Settings with { CredentialFile = Path.Combine(root, "keys.json") }));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task IngressRoutesEverySlackMessageIntoTheAgentMainSession()
    {
        var root = TemporaryDirectory();
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            await WriteJson(Path.Combine(paths.AgentInstanceRoot("agent"), "manifest.json"), new AgentInstanceManifest
            { AgentInstanceId = "agent", WorkspaceId = "default", TemplateId = "general-assistant", IsEnabled = true, ChannelIds = ["channel"], MainSessionId = "existing-main" });
            await WriteJson(paths.ChannelManifestFile("channel"), new ChannelInstanceManifest
            { ChannelId = "channel", WorkspaceId = "default", ProviderId = "slack", Name = "Slack", Slack = Settings });
            var sent = new List<MessageEnvelope>();
            var system = new Mock<IMessageSystem>();
            system.Setup(s => s.SendAsync(It.IsAny<MessageEnvelope>(), It.IsAny<CancellationToken>()))
                .Returns((MessageEnvelope e, CancellationToken _) => { sent.Add(e); return Task.FromResult(new MessageSendResult { MessageId = e.MessageId!, RoomId = e.RoomId!, DeliveryIds = ["delivery"] }); });
            var services = new ServiceCollection().AddSingleton(system.Object);
            using var provider = services.BuildServiceProvider();
            var main = new Mock<IAgentMainSessionBinder>(MockBehavior.Strict);
            var sessions = new InMemorySessionRepository(paths);
            await sessions.CreateAsync(new SessionRecord
            {
                SessionId = "existing-main",
                WorkspaceId = "default",
                AgentTemplateId = "general-assistant",
                AgentInstanceId = "agent",
                ChannelId = "admin",
                OwnerUserId = "admin",
                SessionType = SessionType.ServiceSession,
                SessionRole = SessionRole.Main,
                PrincipalKind = "agent",
                PrincipalId = "agent",
                Status = SessionStatus.Active,
                Title = "主线",
            });
            var factory = new SlackConnectorFactory(new AgentManifestCatalog(paths), Config(paths), paths,
                NullLoggerFactory.Instance, NullLogger<SlackConnectorFactory>.Instance);
            Assert.HasCount(1, await factory.CreateAsync());
            var gateway = new MessageGatewayIngress(new AgentManifestCatalog(paths), Config(paths), sessions, main.Object,
                provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<MessageGatewayIngress>.Instance);
            var incoming = new SlackInboundMessage("Ev1", "T1", "C1", "U1", "1.000001", null, false, "中文问题");
            var first = SlackConnector.Map(incoming, "channel", "default", "agent");
            var accepted = await gateway.AcceptAsync(first);
            var replay = await gateway.AcceptAsync(first);
            var same = await gateway.AcceptAsync(SlackConnector.Map(incoming with { MessageTs = "2.000002", ThreadTs = incoming.MessageTs }, "channel", "default", "agent"));
            var different = await gateway.AcceptAsync(SlackConnector.Map(incoming with { MessageTs = "3.000003" }, "channel", "default", "agent"));
            Assert.AreEqual(accepted.MessageId, replay.MessageId);
            // 单一会话：线程、用户、消息时序都不再决定会话归属。
            Assert.AreEqual("existing-main", accepted.ConversationId);
            Assert.AreEqual("existing-main", same.ConversationId);
            Assert.AreEqual("existing-main", different.ConversationId);
            var session = (await sessions.GetAsync(accepted.ConversationId))!;
            Assert.AreEqual(SessionRole.Main, session.SessionRole);
            Assert.AreEqual("agent", session.AgentInstanceId);
            Assert.AreEqual("agent", session.PrincipalKind);
            Assert.AreEqual("主线", session.Title);
            Assert.AreEqual("slack:T1:U1", sent[0].From.Id);
            // 用户可见正文不得混入渠道身份前缀：身份走元数据 → 运行时上下文。
            Assert.AreEqual("中文问题", sent[0].Content);
            Assert.AreEqual(incoming.MessageTs, sent[0].Metadata![SlackChannelMetadata.ThreadTs]);
            main.VerifyNoOtherCalls();
            await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.AcceptAsync(
                SlackConnector.Map(incoming with { UserId = "U2" }, "channel", "default", "agent")));
            Assert.HasCount(4, sent);
            await WriteJson(paths.ChannelManifestFile("channel"), new ChannelInstanceManifest
            { ChannelId = "channel", WorkspaceId = "default", ProviderId = "slack", Name = "Slack", Slack = Settings with { AllowedBotUserIds = ["UDOT"] } });
            await gateway.AcceptAsync(SlackConnector.Map(incoming with { UserId = "UDOT", IsBot = true, MessageTs = "5.0" }, "channel", "default", "agent"));
            Assert.AreEqual("slack:T1:UDOT", sent[^1].From.Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.AcceptAsync(
                SlackConnector.Map(incoming with { UserId = "UDOT", IsBot = true, IsDirectMessage = true, ChannelId = "D1" }, "channel", "default", "agent")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.AcceptAsync(
                SlackConnector.Map(incoming with { IsBot = true }, "channel", "default", "agent")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task TerminalProjectionPreservesSlackThreadAndProjectsOnce()
    {
        var root = TemporaryDirectory();
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var sent = new List<MessageEnvelope>();
            var system = new Mock<IMessageSystem>();
            system.Setup(s => s.SendAsync(It.IsAny<MessageEnvelope>(), It.IsAny<CancellationToken>()))
                .Returns((MessageEnvelope e, CancellationToken _) => { sent.Add(e); return Task.FromResult(new MessageSendResult { MessageId = e.MessageId!, RoomId = e.RoomId!, DeliveryIds = ["delivery"] }); });
            var images = new Mock<IImageGenerationService>(MockBehavior.Strict);
            var services = new ServiceCollection();
            services.AddDbContext<PlatformDbContext>(o => o.UseSqlite(connection));
            services.AddSingleton(system.Object); services.AddSingleton(images.Object);
            services.AddSingleton(new VisionArtifactStorageService(PuddingDataPaths.FromRoot(root), NullLogger<VisionArtifactStorageService>.Instance));
            await using var provider = services.BuildServiceProvider();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                await db.Database.EnsureCreatedAsync();
                db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
                {
                    CommandId = "command", BatchId = "batch", WorkspaceId = "default", SessionId = "slack-session", AgentInstanceId = "agent",
                    MessageId = "assistant", UserMessageId = "user", TurnId = "turn", Status = "succeeded", TerminalSequence = 1,
                    MetadataJson = JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        [MessageGatewayMetadata.IsGatewayIngress] = "true", [MessageGatewayMetadata.ChannelType] = "slack",
                        [MessageGatewayMetadata.ConnectorId] = "slack:channel", [MessageGatewayMetadata.ExternalConversationId] = "C1",
                        [SlackChannelMetadata.TeamId] = "T1", [SlackChannelMetadata.ThreadTs] = "1.000001"
                    })
                });
                db.ConversationEvents.Add(new ConversationEventEntity
                {
                    ConversationId = "slack-session", Sequence = 1, EventId = "event", WorkspaceId = "default", TurnId = "turn",
                    CommandId = "command", RunId = "run", MessageId = "assistant", Type = ConversationEventTypes.TurnCompleted,
                    Payload = """{"kind":"Completed","reply":"中文答复"}""", OccurredAt = "2026-11-04T00:00:00Z", CommittedAt = "2026-11-04T00:00:00Z"
                });
                await db.SaveChangesAsync();
            }
            var projector = new ConversationReplyProjectionWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ConversationReplyProjectionWorker>.Instance);
            Assert.AreEqual(1, await projector.ProjectBatchAsync());
            Assert.AreEqual(0, await projector.ProjectBatchAsync());
            Assert.HasCount(1, sent);
            Assert.AreEqual("slack:channel", sent[0].To.Single().Id);
            Assert.AreEqual("1.000001", sent[0].Metadata![SlackChannelMetadata.ThreadTs]);
            Assert.AreEqual("中文答复", sent[0].Content);
            images.VerifyNoOtherCalls();
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(ConnectorDeliveryFailure.Unknown)]
    [DataRow(ConnectorDeliveryFailure.Rejected)]
    [DataRow(ConnectorDeliveryFailure.Retryable)]
    public async Task DeliveryOutcomesNeverAckFailureAndOnlyRetryKnownSafeFailures(ConnectorDeliveryFailure failure)
    {
        var target = new MessageAddress { Kind = MessageEndpointKinds.Connector, Id = "slack:channel", WorkspaceId = "default" };
        var item = new MessageInboxItem { MessageId = "message", DeliveryId = "delivery", WorkspaceId = "default",
            From = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "agent" }, Target = target, Content = "reply",
            Status = MessageDeliveryStatuses.Queued, Metadata = new Dictionary<string, string>
            { [MessageGatewayMetadata.ExternalConversationId] = "C1", [MessageGatewayMetadata.IdempotencyKey] = "op1", [SlackChannelMetadata.ThreadTs] = "1.0" } };
        var inbox = new Mock<IMessageInbox>();
        inbox.Setup(i => i.ClaimNextAsync(It.IsAny<MessageClaimRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(item);
        using var provider = new ServiceCollection().AddSingleton(inbox.Object).BuildServiceProvider();
        var connector = new Mock<IPuddingConnector>();
        connector.SetupGet(c => c.Descriptor).Returns(new ConnectorDescriptor { ConnectorId = target.Id, ConnectorType = "slack", Protocol = "test" });
        connector.Setup(c => c.StartAsync(It.IsAny<ConnectorContext>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(2);
        connector.Setup(c => c.SendAsync(It.IsAny<ConnectorMessage>(), It.IsAny<CancellationToken>()))
            .Returns((ConnectorMessage message, CancellationToken _) =>
            {
                Assert.AreEqual("C1", message.Target); Assert.AreEqual("1.0", message.Metadata[SlackChannelMetadata.ThreadTs]);
                return Task.FromException(new ConnectorDeliveryException(failure, "test_outcome", retryAt));
            });
        var host = new ConnectorHost((_, _) => Task.CompletedTask, NullLogger<ConnectorHost>.Instance);
        host.Register(connector.Object); await host.StartAsync(target.Id);
        var dispatcher = new ConnectorDeliveryDispatcher(Mock.Of<IInternalEventBus>(), provider.GetRequiredService<IServiceScopeFactory>(), host,
            NullLogger<ConnectorDeliveryDispatcher>.Instance);
        await dispatcher.HandleAsync(new InternalEvent { Type = "message.deliver", Payload = new MessageDeliverEventPayload
        { MessageId = item.MessageId, DeliveryId = item.DeliveryId, WorkspaceId = "default", RoomId = "default", From = item.From, Target = target, Content = item.Content } });
        inbox.Verify(i => i.AckAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        inbox.Verify(i => i.DeadLetterAsync("delivery", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), failure == ConnectorDeliveryFailure.Retryable ? Times.Never : Times.Once);
        inbox.Verify(i => i.RetryAsync("delivery", It.IsAny<string>(), It.IsAny<string>(), retryAt, It.IsAny<CancellationToken>()), failure == ConnectorDeliveryFailure.Retryable ? Times.Once : Times.Never);
    }

    [TestMethod]
    public async Task DynamicConnectorDiagnosticsAndReceiptsAreAvailableThroughAuthenticatedController()
    {
        Assert.IsTrue(Attribute.IsDefined(typeof(SlackChannelDiagnosticsController), typeof(AuthorizeAttribute)));
        var connector = new Mock<IPuddingConnector>();
        connector.SetupGet(c => c.Descriptor).Returns(new ConnectorDescriptor { ConnectorId = "slack:channel", ConnectorType = "slack", Protocol = "test" });
        connector.Setup(c => c.StartAsync(It.IsAny<ConnectorContext>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connector.Setup(c => c.GetDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ConnectorDiagnostics { Status = "connected" });
        connector.Setup(c => c.OperateAsync("delivery_receipt", It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorOperationResult { Success = true, Data = JsonSerializer.Serialize(new SlackSendReceipt(SlackSendStatus.Sent, "slack_accepted", "C1", "1.0")) });
        var host = new ConnectorHost((_, _) => Task.CompletedTask, NullLogger<ConnectorHost>.Instance);
        host.Register(connector.Object);
        var controller = new SlackChannelDiagnosticsController(host);
        var unavailable = await controller.Receipt("channel", "operation", default);
        Assert.IsInstanceOfType<ConflictObjectResult>(unavailable);
        await host.StartAsync("slack:channel");
        var diagnostics = (OkObjectResult)await controller.Diagnostics("channel", default);
        Assert.AreEqual("connected", ((ConnectorDiagnostics)diagnostics.Value!).Status);
        var receipt = (OkObjectResult)await controller.Receipt("channel", "operation", default);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(receipt.Value));
        Assert.AreEqual("sent", json.RootElement.GetProperty("status").GetString());
        Assert.IsTrue(json.RootElement.GetProperty("providerAccepted").GetBoolean());
        Assert.IsInstanceOfType<NotFoundObjectResult>(await controller.Diagnostics("missing", default));
    }

    [TestMethod]
    public async Task VisibleChannelCatalogIsBoundedAndDoesNotRequireTranscriptBackfill()
    {
        using var http = new HttpClient(new SessionListHandler()) { BaseAddress = new Uri("http://localhost") };
        var api = new PlatformApiClient(http, NullLogger<PlatformApiClient>.Instance);
        var controller = new PuddingPlatform.Controllers.Api.SessionApiController(api, null!, null!, null!, null!,
            NullLogger<PuddingPlatform.Controllers.Api.SessionApiController>.Instance);
        var response = await controller.List("default", default, channelOnly: true);
        var sessions = (List<SessionRecord>)((OkObjectResult)response.Result!).Value!;
        Assert.HasCount(100, sessions);
        Assert.AreEqual("channel109", sessions[0].SessionId);
        Assert.IsTrue(sessions.All(s => s.PrincipalKind == "channel" && s.Status != SessionStatus.Frozen && s.AgentInstanceId == "agent"));
    }

    [TestMethod]
    public async Task HistoryExposesChannelSenderWithoutLeakingInternalMetadata()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.ChatMessages.Add(new ChatMessageEntity { SessionId = "slack", WorkspaceId = "default", Role = "user", Content = "hello", CreatedAt = 1,
            MetadataJson = JsonSerializer.Serialize(new Dictionary<string, string> { [MessageGatewayMetadata.ChannelType] = "slack",
                [SlackChannelMetadata.UserId] = "UDOT", [SlackChannelMetadata.IsBot] = "true", [MessageFabricTurnMetadata.DeliveryId] = "private-delivery" }) });
        await db.SaveChangesAsync();
        var messages = new Mock<IChatMessageRepository>();
        messages.Setup(m => m.AnyBySessionIdAsync("slack", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var controller = new PuddingPlatform.Controllers.Api.MessageApiController(db, messages.Object,
            NullLogger<PuddingPlatform.Controllers.Api.MessageApiController>.Instance);
        var result = await controller.List("slack");
        var body = (PuddingPlatform.Controllers.Api.MessageApiController.MessageListResponse)((OkObjectResult)result.Result!).Value!;
        var metadata = body.Items.Single().Metadata!;
        Assert.AreEqual("UDOT", metadata[SlackChannelMetadata.UserId]);
        Assert.AreEqual("true", metadata[SlackChannelMetadata.IsBot]);
        Assert.IsFalse(metadata.ContainsKey(MessageFabricTurnMetadata.DeliveryId));
    }

    private sealed class SessionListHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.AreEqual("/api/session/workspace/default", request.RequestUri!.AbsolutePath);
            var rows = Enumerable.Range(0, 110).Select(i => new SessionRecord
            {
                SessionId = "channel" + i, WorkspaceId = "default", AgentTemplateId = "general-assistant", ChannelId = "channel",
                OwnerUserId = "slack:T1:U1", AgentInstanceId = "agent", PrincipalKind = "channel", PrincipalId = "T1/C1/" + i,
                LastActiveAt = DateTimeOffset.UnixEpoch.AddSeconds(i)
            }).ToList();
            rows.Add(rows[0] with { SessionId = "main", PrincipalKind = "agent" });
            rows.Add(rows[0] with { SessionId = "frozen", Status = SessionStatus.Frozen });
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(rows) });
        }
    }

    private static ChannelConfigurationFileService Config(PuddingDataPaths paths)
    {
        var catalog = new Mock<IWorkspaceAgentCatalog>();
        catalog.Setup(c => c.ListAgentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<WorkspaceAgentDto>());
        return new(paths, catalog.Object, Mock.Of<IAgentChannelBinder>(), NullLogger<ChannelConfigurationFileService>.Instance);
    }
    private static string TemporaryDirectory()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("PUDDING_TEST_ROOT") ?? Path.GetTempPath(), "SlackIntegration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    private static async Task WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
