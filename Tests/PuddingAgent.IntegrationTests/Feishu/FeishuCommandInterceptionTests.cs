using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingAgent.Services;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingController.Services;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingAgent.IntegrationTests.Feishu;

[TestClass]
public sealed class FeishuCommandInterceptionTests
{
    private const string WorkspaceId = "default";
    private const string AgentId = "fake-command-agent";
    private const string ConversationId = "fake-command-main";
    private const string ConnectorId = "feishu:fake-command-agent";
    private const string SenderOpenId = "ou_command_sender";

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Yolo_IsInterceptedBeforeAgentDelivery_AndUsesFeishuWhitelist(
        bool whitelisted)
    {
        var context = await CreateContextAsync(
            whitelisted,
            request => new SystemCommandResult(
                request.ConversationId,
                request.ClientMessageId,
                request.ResponseMessageId,
                request.CommandText,
                request.IsPrivilegedUser
                    ? "YOLO enabled by Pudding."
                    : "Permission denied by Pudding.",
                request.IsPrivilegedUser ? "Yolo" : "Normal"));
        try
        {
            var result = await context.Gateway.AcceptAsync(
                CreateEnvelope("om_yolo", "/yolo"));

            Assert.HasCount(1, context.Handler.Requests);
            Assert.AreEqual(
                whitelisted,
                context.Handler.Requests.Single().IsPrivilegedUser);
            Assert.HasCount(1, context.Messages.Envelopes);
            var reply = context.Messages.Envelopes.Single();
            Assert.HasCount(1, reply.To);
            Assert.AreEqual(MessageEndpointKinds.Connector, reply.To.Single().Kind);
            Assert.AreEqual(ConnectorId, reply.To.Single().Id);
            Assert.AreEqual("om_yolo", reply.ReplyToMessageId);
            Assert.AreEqual(
                "true",
                reply.Metadata[MessageGatewayMetadata.IsGatewayCommand]);
            Assert.AreEqual("/yolo", reply.Metadata[MessageGatewayMetadata.GatewayCommand]);
            Assert.HasCount(1, result.DeliveryIds);
            Assert.IsFalse(context.Messages.Envelopes.Any(message =>
                message.To.Any(target => target.Kind == MessageEndpointKinds.Agent)));
        }
        finally
        {
            context.Dispose();
        }
    }

    [TestMethod]
    public async Task CommandResult_ReachesAgentOnlyWhenHandlerExplicitlyRequestsForwarding()
    {
        var context = await CreateContextAsync(
            whitelisted: true,
            request => new SystemCommandResult(
                request.ConversationId,
                request.ClientMessageId,
                request.ResponseMessageId,
                request.CommandText,
                "Pudding prepared an Agent task.",
                "Normal",
                ForwardToAgent: true,
                AgentMessage: "processed command result for agent"));
        try
        {
            await context.Gateway.AcceptAsync(
                CreateEnvelope("om_forward", "/memory"));

            Assert.HasCount(1, context.Messages.Envelopes);
            var forwarded = context.Messages.Envelopes.Single();
            Assert.HasCount(1, forwarded.To);
            Assert.AreEqual(MessageEndpointKinds.Agent, forwarded.To.Single().Kind);
            Assert.AreEqual(AgentId, forwarded.To.Single().Id);
            Assert.AreEqual("processed command result for agent", forwarded.Content);
            Assert.IsFalse(context.Messages.Envelopes.Any(message =>
                message.To.Any(target => target.Kind == MessageEndpointKinds.Connector)));
        }
        finally
        {
            context.Dispose();
        }
    }

    [TestMethod]
    public async Task WhoAmI_PassesSenderOpenIdAndRepliesWithoutPrivilegeOrAgentDelivery()
    {
        var context = await CreateContextAsync(
            whitelisted: false,
            request => new SystemCommandResult(
                request.ConversationId,
                request.ClientMessageId,
                request.ResponseMessageId,
                request.CommandText,
                $"Your Feishu user ID (open_id) is `{request.ExternalUserId}`.",
                "Normal"));
        try
        {
            await context.Gateway.AcceptAsync(
                CreateEnvelope("om_whoami", "/whoami"));

            Assert.HasCount(1, context.Handler.Requests);
            var request = context.Handler.Requests.Single();
            Assert.IsFalse(request.IsPrivilegedUser);
            Assert.AreEqual("feishu", request.SourceChannel);
            Assert.AreEqual(SenderOpenId, request.ExternalUserId);

            Assert.HasCount(1, context.Messages.Envelopes);
            var reply = context.Messages.Envelopes.Single();
            Assert.AreEqual(MessageEndpointKinds.Connector, reply.To.Single().Kind);
            StringAssert.Contains(reply.Content, SenderOpenId);
            Assert.IsFalse(context.Messages.Envelopes.Any(message =>
                message.To.Any(target => target.Kind == MessageEndpointKinds.Agent)));
        }
        finally
        {
            context.Dispose();
        }
    }

    private static async Task<TestContext> CreateContextAsync(
        bool whitelisted,
        Func<SystemCommandRequest, SystemCommandResult> resultFactory)
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "pudding-feishu-command-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        var paths = PuddingDataPaths.FromRoot(dataRoot);
        await WriteManifestAsync(paths, whitelisted);

        var sessions = new InMemorySessionRepository(paths);
        await sessions.CreateAsync(new SessionRecord
        {
            SessionId = ConversationId,
            WorkspaceId = WorkspaceId,
            AgentTemplateId = "general-assistant",
            AgentInstanceId = AgentId,
            ChannelId = "admin",
            OwnerUserId = "admin",
            SessionType = SessionType.ServiceSession,
            SessionRole = SessionRole.Main,
            PrincipalKind = "agent",
            PrincipalId = AgentId,
            Status = SessionStatus.Active,
        });

        var messages = new RecordingMessageSystem();
        var handler = new RecordingSystemCommandHandler(resultFactory);
        var services = new ServiceCollection();
        services.AddSingleton<IMessageSystem>(messages);
        services.AddSingleton<ISystemCommandHandler>(handler);
        var provider = services.BuildServiceProvider();
        var gateway = new MessageGatewayIngress(
            new AgentManifestCatalog(paths),
            sessions,
            new UnexpectedMainSessionBinder(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MessageGatewayIngress>.Instance);
        return new TestContext(dataRoot, provider, gateway, messages, handler);
    }

    private static PuddingIngressEnvelope CreateEnvelope(
        string messageId,
        string text) =>
        new()
        {
            ConnectorId = ConnectorId,
            WorkspaceId = WorkspaceId,
            AgentId = AgentId,
            ChannelId = "feishu",
            ChannelType = "feishu",
            UserExternalId = SenderOpenId,
            MessageText = text,
            MessageType = "chat",
            ExternalConversationId = "oc_command_chat",
            ExternalMessageId = messageId,
            CorrelationId = "oc_command_chat",
        };

    private static async Task WriteManifestAsync(
        PuddingDataPaths paths,
        bool whitelisted)
    {
        var root = paths.AgentInstanceRoot(AgentId);
        Directory.CreateDirectory(root);
        var manifest = new AgentInstanceManifest
        {
            AgentInstanceId = AgentId,
            TemplateId = "general-assistant",
            WorkspaceId = WorkspaceId,
            DisplayName = "Fake Command Agent",
            MainSessionId = ConversationId,
            IsEnabled = true,
            Feishu = new AgentFeishuBotConfig
            {
                Enabled = true,
                AppId = "cli_fake_command",
                AppSecret = "fake-command-secret",
                PrivilegedUserOpenIds = whitelisted ? [SenderOpenId] : [],
            },
        };
        await File.WriteAllTextAsync(
            Path.Combine(root, "manifest.json"),
            JsonSerializer.Serialize(
                manifest,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private sealed class RecordingMessageSystem : IMessageSystem
    {
        public List<MessageEnvelope> Envelopes { get; } = [];

        public Task<MessageSendResult> SendAsync(
            MessageEnvelope envelope,
            CancellationToken ct = default)
        {
            Envelopes.Add(envelope);
            return Task.FromResult(new MessageSendResult
            {
                MessageId = envelope.MessageId,
                RoomId = envelope.RoomId,
                DeliveryIds = [$"delivery-{Envelopes.Count}"],
            });
        }
    }

    private sealed class RecordingSystemCommandHandler(
        Func<SystemCommandRequest, SystemCommandResult> resultFactory)
        : ISystemCommandHandler
    {
        public List<SystemCommandRequest> Requests { get; } = [];

        public Task<SystemCommandResult> HandleAsync(
            SystemCommandRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(resultFactory(request));
        }
    }

    private sealed class UnexpectedMainSessionBinder : IAgentMainSessionBinder
    {
        public Task<WorkspaceAgentDto> SetAgentMainSessionAsync(
            string workspaceId,
            string agentId,
            string mainSessionId,
            CancellationToken ct = default)
            => throw new AssertFailedException(
                "The configured fake main session should be reused.");
    }

    private sealed record TestContext(
        string DataRoot,
        ServiceProvider Provider,
        MessageGatewayIngress Gateway,
        RecordingMessageSystem Messages,
        RecordingSystemCommandHandler Handler) : IDisposable
    {
        public void Dispose()
        {
            Provider.Dispose();
            if (Directory.Exists(DataRoot))
                Directory.Delete(DataRoot, recursive: true);
        }
    }
}
