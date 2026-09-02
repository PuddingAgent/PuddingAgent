using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Security;
using PuddingPlatform.Controllers.External.V1;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.ExternalApi;
using PuddingPlatform.Services.Security;
using PuddingPlatformTests.Security;

namespace PuddingPlatformTests.Controllers;

[TestClass]
public sealed class ExternalWorkspaceAgentApiV1Tests
{
    private sealed class ApiHarness : IAsyncDisposable
    {
        public required ExternalAccessTokenTestHarness Db { get; init; }
        public required ExternalWorkspaceAgentController Controller { get; init; }
        public required RecordingMessageSystem MessageSystem { get; init; }
        public required string ConfigRoot { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (Directory.Exists(ConfigRoot))
                Directory.Delete(ConfigRoot, recursive: true);
        }
    }

    private static async Task<ApiHarness> CreateAsync()
    {
        var dbHarness = await ExternalAccessTokenTestHarness.CreateAsync();
        await using (var db = await dbHarness.Factory.CreateDbContextAsync())
        {
            var team = new TeamEntity { TeamId = "team", Name = "Team" };
            db.Teams.Add(team);
            await db.SaveChangesAsync();
            var defaultWorkspace = await db.Workspaces.SingleAsync(item => item.WorkspaceId == "default");
            defaultWorkspace.Name = "Default Workspace";
            defaultWorkspace.Description = "Visible";
            defaultWorkspace.UserProfile = "must-not-leak";
            defaultWorkspace.IsEnabled = true;
            defaultWorkspace.IsFrozen = false;
            db.Workspaces.Add(new WorkspaceEntity
            {
                WorkspaceId = "private",
                Slug = "private",
                TeamEntityId = team.Id,
                Name = "Private Workspace",
                IsEnabled = true,
            });
            await db.SaveChangesAsync();
        }

        var catalog = new StubAgentCatalog(
        [
            Agent("agent-a", enabled: true, frozen: false),
            Agent("agent-disabled", enabled: false, frozen: false),
        ]);
        var messageSystem = new RecordingMessageSystem(dbHarness.Factory);
        var configRoot = Path.Combine(Path.GetTempPath(), $"pudding-external-v1-{Guid.NewGuid():N}");
        var controller = new ExternalWorkspaceAgentController(
            dbHarness.Factory,
            catalog,
            messageSystem,
            new ExternalApiIdempotencyStore(dbHarness.Factory),
            new ExternalTaskApiOptionsProvider(
                PuddingDataPaths.FromRoot(configRoot),
                NullLogger<ExternalTaskApiOptionsProvider>.Instance));

        var identity = new ClaimsIdentity(authenticationType: ExternalAccessTokenDefaults.Scheme);
        identity.AddClaim(new Claim(ExternalAccessTokenClaimNames.TokenId, "token-1"));
        identity.AddClaim(new Claim(ClaimTypes.Name, "integration-client"));
        identity.AddClaim(new Claim(ExternalAccessTokenClaimNames.Workspace, "default"));
        identity.AddClaim(new Claim(ExternalAccessTokenClaimNames.Scope, ExternalTaskApiScopes.WorkspacesRead));
        identity.AddClaim(new Claim(ExternalAccessTokenClaimNames.Scope, ExternalTaskApiScopes.AgentsRead));
        identity.AddClaim(new Claim(ExternalAccessTokenClaimNames.Scope, ExternalTaskApiScopes.MessagesSend));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };

        return new ApiHarness
        {
            Db = dbHarness,
            Controller = controller,
            MessageSystem = messageSystem,
            ConfigRoot = configRoot,
        };
    }

    [TestMethod]
    public async Task ListWorkspaces_ReturnsOnlyTokenAllowList_AndSafeProjection()
    {
        await using var api = await CreateAsync();

        var result = await api.Controller.ListWorkspaces(CancellationToken.None);
        var items = Body<IReadOnlyList<ExternalWorkspaceDto>>(result.Result!, 200);

        Assert.AreEqual(1, items.Count);
        Assert.AreEqual("default", items[0].WorkspaceId);
        Assert.AreEqual("Default Workspace", items[0].Name);
        Assert.IsFalse(typeof(ExternalWorkspaceDto).GetProperties()
            .Any(property => property.Name == "UserProfile" || property.Name == "Members"));
    }

    [TestMethod]
    public async Task ListAndGetAgents_ExposeSafeDirectoryProjection()
    {
        await using var api = await CreateAsync();

        var listResult = await api.Controller.ListAgents("default", enabledOnly: true, CancellationToken.None);
        var items = Body<IReadOnlyList<ExternalAgentDto>>(listResult.Result!, 200);
        Assert.AreEqual(1, items.Count);
        Assert.AreEqual("agent-a", items[0].AgentId);
        CollectionAssert.Contains(items[0].CapabilityIds.ToList(), "browser");
        Assert.IsFalse(typeof(ExternalAgentDto).GetProperties()
            .Any(property => property.Name.Contains("Prompt", StringComparison.Ordinal)
                             || property.Name == "MainSessionId"));

        var get = Body<ExternalAgentDto>(
            await api.Controller.GetAgent("default", "agent-a", CancellationToken.None), 200);
        Assert.AreEqual("Agent agent-a", get.DisplayName);
    }

    [TestMethod]
    public async Task SendMessage_UsesTokenActor_CanonicalFabric_AndIdempotentReplay()
    {
        await using var api = await CreateAsync();
        SetRequest(api, "/api/external/v1/workspaces/default/agents/agent-a/messages", "idem-1");

        var first = Body<ExternalAgentMessageReceiptDto>(await api.Controller.SendMessage(
            "default",
            "agent-a",
            new ExternalSendAgentMessageRequest { Content = "  inspect the queue  " },
            CancellationToken.None), 202);

        Assert.AreEqual(1, api.MessageSystem.SendCount);
        Assert.AreEqual(MessageEndpointKinds.Connector, api.MessageSystem.LastEnvelope!.From.Kind);
        Assert.AreEqual("access-token:token-1", api.MessageSystem.LastEnvelope.From.Id);
        Assert.AreEqual("agent-a", api.MessageSystem.LastEnvelope.To.Single().Id);
        Assert.AreEqual("inspect the queue", api.MessageSystem.LastEnvelope.Content);
        Assert.AreEqual("external.api", api.MessageSystem.LastEnvelope.Metadata["source"]);
        Assert.AreEqual("true", api.MessageSystem.LastEnvelope.Metadata["requires_response"]);
        Assert.AreEqual(
            "true",
            api.MessageSystem.LastEnvelope.Metadata[MessageDeliveryPolicy.CanonicalTurnMetadataKey]);
        Assert.AreEqual(first.StatusUrl, api.Controller.Response.Headers.Location.ToString());

        SetRequest(api, "/api/external/v1/workspaces/default/agents/agent-a/messages", "idem-1");
        var replay = Body<ExternalAgentMessageReceiptDto>(await api.Controller.SendMessage(
            "default",
            "agent-a",
            new ExternalSendAgentMessageRequest { Content = "  inspect the queue  " },
            CancellationToken.None), 202);
        Assert.AreEqual(first.MessageId, replay.MessageId);
        Assert.AreEqual(1, api.MessageSystem.SendCount, "幂等重放不得再次发送 Message Fabric 消息");
    }

    [TestMethod]
    public async Task GetMessage_SeparatesDeliveryAcceptance_FromCanonicalTerminalReply()
    {
        await using var api = await CreateAsync();
        SetRequest(api, "/api/external/v1/workspaces/default/agents/agent-a/messages", "idem-terminal");
        var sent = Body<ExternalAgentMessageReceiptDto>(await api.Controller.SendMessage(
            "default",
            "agent-a",
            new ExternalSendAgentMessageRequest { Content = "report status" },
            CancellationToken.None), 202);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using (var db = await api.Db.Factory.CreateDbContextAsync())
        {
            var delivery = await db.MessageDeliveries.SingleAsync(item => item.MessageId == sent.MessageId);
            delivery.Status = MessageDeliveryStatuses.Delivered;
            delivery.AckAt = now;
            delivery.UpdatedAt = now;

            var commandId = "command-1";
            db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
            {
                CommandId = commandId,
                BatchId = "batch-1",
                WorkspaceId = "default",
                SessionId = "session-agent-a",
                MessageId = "turn-message-1",
                UserMessageId = "turn-message-1",
                TurnId = "turn-1",
                AgentInstanceId = "agent-a",
                Status = "succeeded",
                TerminalSequence = 1,
                CreatedAt = now - 100,
                CompletedAt = now,
                MetadataJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    [MessageFabricTurnMetadata.IsIngress] = "true",
                    [MessageFabricTurnMetadata.MessageId] = sent.MessageId,
                }),
            });
            db.ConversationEvents.Add(new ConversationEventEntity
            {
                ConversationId = "session-agent-a",
                Sequence = 1,
                EventId = "event-1",
                WorkspaceId = "default",
                TurnId = "turn-1",
                CommandId = commandId,
                Type = "turn.completed",
                Payload = "{\"kind\":\"Completed\",\"reply\":\"external reply\"}",
                OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
                CommittedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            await db.SaveChangesAsync();
        }

        var receipt = Body<ExternalAgentMessageReceiptDto>(await api.Controller.GetMessage(
            "default", "agent-a", sent.MessageId, CancellationToken.None), 200);
        Assert.AreEqual("accepted", receipt.DeliveryStatus);
        Assert.AreEqual("succeeded", receipt.ExecutionStatus);
        Assert.AreEqual("external reply", receipt.Reply);
        Assert.AreEqual("回复完成", receipt.ReplySummary);
        Assert.AreEqual(false, receipt.ReplyIsError);
    }

    [TestMethod]
    public async Task SendMessage_DisabledAgentMissingContentAndMissingIdempotencyKey_AreRejected()
    {
        await using var api = await CreateAsync();
        api.Controller.HttpContext.Request.Path = "/api/external/v1/workspaces/default/agents/agent-disabled/messages";
        api.Controller.HttpContext.Request.Headers["Idempotency-Key"] = "disabled-agent";

        var unavailable = await api.Controller.SendMessage(
            "default",
            "agent-disabled",
            new ExternalSendAgentMessageRequest { Content = "hello" },
            CancellationToken.None);
        Assert.AreEqual(409, ((ObjectResult)unavailable).StatusCode);

        api.Controller.HttpContext.Request.Path = "/api/external/v1/workspaces/default/agents/agent-a/messages";
        api.Controller.HttpContext.Request.Headers["Idempotency-Key"] = "missing-content";
        var missingContent = await api.Controller.SendMessage(
            "default",
            "agent-a",
            new ExternalSendAgentMessageRequest(),
            CancellationToken.None);
        var missingContentError = Body<ExternalErrorResponse>(missingContent, 400);
        Assert.AreEqual("external.invalid_request", missingContentError.Code);

        api.Controller.HttpContext.Request.Headers.Remove("Idempotency-Key");
        var missingKey = await api.Controller.SendMessage(
            "default",
            "agent-a",
            new ExternalSendAgentMessageRequest { Content = "hello" },
            CancellationToken.None);
        var error = Body<ExternalErrorResponse>(missingKey, 400);
        Assert.AreEqual("external.invalid_request", error.Code);
        Assert.AreEqual(0, api.MessageSystem.SendCount);
    }

    private static WorkspaceAgentDto Agent(string id, bool enabled, bool frozen)
        => new(
            AgentId: id,
            Name: id,
            Description: "directory item",
            DisplayName: $"Agent {id}",
            AvatarId: null,
            AvatarUrl: null,
            SourceTemplateId: null,
            MainSessionId: $"session-{id}",
            SystemPromptOverride: "secret prompt",
            PreferredProviderId: "deepseek",
            PreferredModelId: "chat",
            IsEnabled: enabled,
            IsFrozen: frozen,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Role: "assistant",
            SelectedCapabilityIds: ["browser"]);

    private static void SetRequest(ApiHarness api, string path, string idempotencyKey)
    {
        api.Controller.HttpContext.Request.Method = "POST";
        api.Controller.HttpContext.Request.Path = path;
        api.Controller.HttpContext.Request.Headers["Idempotency-Key"] = idempotencyKey;
    }

    private static T Body<T>(IActionResult result, int expectedStatus)
    {
        var objectResult = (ObjectResult)result;
        Assert.AreEqual(expectedStatus, objectResult.StatusCode,
            $"expected {expectedStatus}, got {objectResult.StatusCode}");
        return (T)objectResult.Value!;
    }

    private sealed class StubAgentCatalog(IReadOnlyList<WorkspaceAgentDto> agents) : IWorkspaceAgentCatalog
    {
        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(
            string workspaceId,
            CancellationToken ct = default)
            => Task.FromResult(agents);
    }

    private sealed class RecordingMessageSystem(IDbContextFactory<PlatformDbContext> dbFactory) : IMessageSystem
    {
        public int SendCount { get; private set; }
        public MessageEnvelope? LastEnvelope { get; private set; }

        public async Task<MessageSendResult> SendAsync(
            MessageEnvelope envelope,
            CancellationToken ct = default)
        {
            SendCount++;
            LastEnvelope = envelope;
            var deliveryId = $"delivery-{envelope.MessageId}";
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.RoomMessages.Add(new RoomMessageEntity
            {
                MessageId = envelope.MessageId,
                WorkspaceId = envelope.From.WorkspaceId!,
                RoomId = envelope.RoomId!,
                FromKind = envelope.From.Kind,
                FromId = envelope.From.Id,
                FromDisplayName = envelope.From.DisplayName,
                Audience = envelope.Audience,
                Visibility = envelope.Visibility,
                Content = envelope.Content,
                CorrelationId = envelope.CorrelationId,
                MetadataJson = JsonSerializer.Serialize(envelope.Metadata),
                CreatedAt = now,
            });
            db.MessageDeliveries.Add(new MessageDeliveryEntity
            {
                DeliveryId = deliveryId,
                MessageId = envelope.MessageId,
                WorkspaceId = envelope.From.WorkspaceId!,
                RoomId = envelope.RoomId,
                TargetKind = envelope.To.Single().Kind,
                TargetId = envelope.To.Single().Id,
                Status = MessageDeliveryStatuses.Queued,
                HandlingMode = MessageDeliveryHandlingModes.Execute,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(ct);
            return new MessageSendResult
            {
                MessageId = envelope.MessageId,
                RoomId = envelope.RoomId,
                DeliveryIds = [deliveryId],
            };
        }
    }
}
