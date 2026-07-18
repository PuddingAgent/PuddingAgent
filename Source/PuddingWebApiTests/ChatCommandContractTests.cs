using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingWebApiTests;

[TestClass]
[DoNotParallelize]
public sealed class ChatCommandContractTests
{
    private const string WorkspaceId = "default";
    private const string AgentId = "default-agent";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static CustomWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        _factory = new CustomWebApplicationFactory();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var workspace = db.Workspaces.FirstOrDefault(w => w.WorkspaceId == WorkspaceId);
        if (workspace is null)
        {
            workspace = new WorkspaceEntity
            {
                WorkspaceId = WorkspaceId,
                Name = "Default Workspace",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Workspaces.Add(workspace);
            db.SaveChanges();
        }

        if (!db.WorkspaceAgents.Any(a =>
                a.WorkspaceEntityId == workspace.Id && a.AgentId == AgentId))
        {
            db.WorkspaceAgents.Add(new WorkspaceAgentEntity
            {
                AgentId = AgentId,
                Name = "Default Agent",
                SourceTemplateId = "global:general-assistant",
                DisplayName = "Assistant",
                WorkspaceEntityId = workspace.Id,
                IsEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }
    }

    [ClassCleanup]
    public static void ClassCleanup() => _factory.Dispose();

    [TestInitialize]
    public void TestInit()
    {
        _client = _factory.CreateClient();
        JwtHelper.SetBearerToken(_client);
    }

    [TestCleanup]
    public void TestCleanup() => _client.Dispose();

    [TestMethod]
    public async Task SubmitTurn_ReturnsCanonicalAcceptanceContract()
    {
        var conversationId = NewId("conversation");
        var response = await PostTurnAsync(conversationId, "Hello");

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        var acceptance = await ReadAcceptanceAsync(response);
        Assert.AreEqual(conversationId, acceptance.ConversationId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(acceptance.MessageId));
        Assert.AreEqual(1, acceptance.TurnIds.Count);
        Assert.AreEqual(1, acceptance.CommandIds.Count);
        Assert.IsTrue(acceptance.AcceptedSequence > 0);
    }

    [TestMethod]
    public async Task SubmitTurn_DifferentRequests_CreateDifferentCommands()
    {
        var conversationId = NewId("conversation");
        var first = await ReadAcceptanceAsync(
            await PostTurnAsync(conversationId, "Hello 1"));
        var second = await ReadAcceptanceAsync(
            await PostTurnAsync(conversationId, "Hello 2"));

        Assert.AreNotEqual(first.CommandIds.Single(), second.CommandIds.Single());
        Assert.AreNotEqual(first.TurnIds.Single(), second.TurnIds.Single());
        Assert.AreNotEqual(first.MessageId, second.MessageId);
        Assert.IsTrue(second.AcceptedSequence > first.AcceptedSequence);
    }

    [TestMethod]
    public async Task SubmitTurn_PreservesRouteConversationId()
    {
        var conversationId = NewId("conversation");
        var acceptance = await ReadAcceptanceAsync(
            await PostTurnAsync(conversationId, "Hello"));

        Assert.AreEqual(conversationId, acceptance.ConversationId);
    }

    [TestMethod]
    public async Task SubmitTurn_SameClientRequestId_IsIdempotent()
    {
        var conversationId = NewId("conversation");
        var clientRequestId = NewId("request");
        var clientMessageId = NewId("message");

        var first = await ReadAcceptanceAsync(
            await PostTurnAsync(
                conversationId,
                "Hello",
                clientRequestId,
                clientMessageId));
        var second = await ReadAcceptanceAsync(
            await PostTurnAsync(
                conversationId,
                "Hello",
                clientRequestId,
                clientMessageId));

        Assert.AreEqual(first.ConversationId, second.ConversationId);
        Assert.AreEqual(first.MessageId, second.MessageId);
        CollectionAssert.AreEqual(first.TurnIds.ToArray(), second.TurnIds.ToArray());
        CollectionAssert.AreEqual(first.CommandIds.ToArray(), second.CommandIds.ToArray());
        Assert.AreEqual(first.AcceptedSequence, second.AcceptedSequence);
    }

    [TestMethod]
    public async Task SubmitTurn_PersistsAcceptanceAtomically()
    {
        var conversationId = NewId("conversation");
        var clientRequestId = NewId("request");
        var clientMessageId = NewId("message");
        var acceptance = await ReadAcceptanceAsync(
            await PostTurnAsync(
                conversationId,
                "Persist this",
                clientRequestId,
                clientMessageId));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var batch = await db.AcceptanceBatches.AsNoTracking()
            .SingleAsync(b =>
                b.WorkspaceId == WorkspaceId &&
                b.ClientRequestId == clientRequestId);
        var message = await db.ChatMessages.AsNoTracking()
            .SingleAsync(m => m.MessageId == clientMessageId);
        var command = await db.ChatExecutionCommands.AsNoTracking()
            .SingleAsync(c => c.BatchId == batch.BatchId);
        var turn = await db.ConversationTurns.AsNoTracking()
            .SingleAsync(t => t.TurnId == command.TurnId);
        var acceptedEvent = await db.ConversationEvents.AsNoTracking()
            .SingleAsync(e =>
                e.ConversationId == conversationId &&
                e.TurnId == command.TurnId &&
                e.Type == "turn.accepted");

        Assert.AreEqual(conversationId, batch.ConversationId);
        Assert.AreEqual(conversationId, message.SessionId);
        Assert.AreEqual(AgentId, command.AgentInstanceId);
        Assert.AreEqual("accepted", turn.Status);
        Assert.AreEqual(acceptance.AcceptedSequence, acceptedEvent.Sequence);
    }

    [TestMethod]
    public async Task SubmitTurn_InvalidRecipient_IsRejectedWithoutAcceptance()
    {
        var conversationId = NewId("conversation");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/turns");
        request.Headers.Add("X-Workspace-Id", WorkspaceId);
        request.Content = JsonContent.Create(new
        {
            clientRequestId = NewId("request"),
            clientMessageId = NewId("message"),
            recipients = new { type = "all", agentIds = Array.Empty<string>() },
            content = new[] { new { type = "text", text = "Hello" } },
        });

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.IsFalse(await db.AcceptanceBatches.AsNoTracking()
            .AnyAsync(b => b.ConversationId == conversationId));
    }

    private async Task<HttpResponseMessage> PostTurnAsync(
        string conversationId,
        string text,
        string? clientRequestId = null,
        string? clientMessageId = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/turns");
        request.Headers.Add("X-Workspace-Id", WorkspaceId);
        request.Content = JsonContent.Create(new
        {
            clientRequestId = clientRequestId ?? NewId("request"),
            clientMessageId = clientMessageId ?? NewId("message"),
            recipients = new { type = "agent", agentIds = new[] { AgentId } },
            content = new[] { new { type = "text", text } },
        });
        return await _client.SendAsync(request);
    }

    private static async Task<AcceptanceDto> ReadAcceptanceAsync(
        HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(
            HttpStatusCode.Accepted,
            response.StatusCode,
            $"Unexpected response: {body}");
        return JsonSerializer.Deserialize<AcceptanceDto>(body, JsonOpts)
            ?? throw new AssertFailedException($"Invalid acceptance response: {body}");
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private sealed record AcceptanceDto(
        string ConversationId,
        string MessageId,
        IReadOnlyList<string> TurnIds,
        IReadOnlyList<string> CommandIds,
        long AcceptedSequence);
}
