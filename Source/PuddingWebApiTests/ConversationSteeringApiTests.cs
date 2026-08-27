using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingWebApiTests;

[TestClass]
[DoNotParallelize]
public sealed class ConversationSteeringApiTests
{
    private static CustomWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
        => _factory = new CustomWebApplicationFactory();

    [ClassCleanup]
    public static void ClassCleanup()
        => _factory.Dispose();

    [TestInitialize]
    public void TestInitialize()
    {
        _client = _factory.CreateClient();
        JwtHelper.SetBearerToken(_client);
    }

    [TestCleanup]
    public void TestCleanup()
        => _client.Dispose();

    [TestMethod]
    public async Task SteeringEndpoint_AcceptsRunningTurn_AndCreatesRuntimeQueueItem()
    {
        var ids = await InsertCommandAsync("running");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/conversations/{ids.ConversationId}/turns/{ids.TurnId}/steering")
        {
            Content = JsonContent.Create(new
            {
                text = "先停止当前方向，检查最新错误日志。",
                priority = 1000,
                agentId = ids.AgentId,
                sourceQueueItemId = "local-queue-1",
            }),
        };
        request.Headers.Add("X-Workspace-Id", ids.WorkspaceId);

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SteeringResponse>();
        Assert.IsNotNull(body);
        Assert.IsFalse(string.IsNullOrWhiteSpace(body.SteeringId));

        using var retryRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/conversations/{ids.ConversationId}/turns/{ids.TurnId}/steering")
        {
            Content = JsonContent.Create(new
            {
                text = "先停止当前方向，检查最新错误日志。",
                priority = 1000,
                agentId = ids.AgentId,
                sourceQueueItemId = "local-queue-1",
            }),
        };
        retryRequest.Headers.Add("X-Workspace-Id", ids.WorkspaceId);
        var retryResponse = await _client.SendAsync(retryRequest);
        var retryBody = await retryResponse.Content.ReadFromJsonAsync<SteeringResponse>();
        Assert.AreEqual(HttpStatusCode.Accepted, retryResponse.StatusCode);
        Assert.IsNotNull(retryBody);
        Assert.AreEqual(body.SteeringId, retryBody.SteeringId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var stored = await db.SessionSteeringMessages
            .AsNoTracking()
            .SingleAsync(item => item.SteeringId == body.SteeringId);
        Assert.AreEqual(1, await db.SessionSteeringMessages.CountAsync(item =>
            item.SessionId == ids.ConversationId
            && item.SourceQueueItemId == "local-queue-1"));
        Assert.AreEqual(ids.ConversationId, stored.SessionId);
        Assert.AreEqual(ids.TurnId, stored.TargetTurnId);
        Assert.AreEqual(ids.AgentId, stored.AgentId);
        Assert.AreEqual("local-queue-1", stored.SourceQueueItemId);
        Assert.AreEqual(SessionSteeringStatuses.Pending, stored.Status);

        var steeringService = scope.ServiceProvider.GetRequiredService<SessionSteeringService>();
        Assert.IsNull(await steeringService.ConsumeNextAsync(
            ids.ConversationId,
            ids.AgentId,
            $"other-{ids.TurnId}",
            round: 1,
            CancellationToken.None));
        var consumed = await steeringService.ConsumeNextAsync(
            ids.ConversationId,
            ids.AgentId,
            ids.TurnId,
            round: 2,
            CancellationToken.None);
        Assert.IsNotNull(consumed);
        Assert.AreEqual(body.SteeringId, consumed.SteeringId);
        Assert.IsNull(await steeringService.ConsumeNextAsync(
            ids.ConversationId,
            ids.AgentId,
            ids.TurnId,
            round: 3,
            CancellationToken.None));
    }

    [TestMethod]
    public async Task SteeringEndpoint_RejectsTerminalTurn_WithoutCreatingQueueItem()
    {
        var ids = await InsertCommandAsync("succeeded");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/conversations/{ids.ConversationId}/turns/{ids.TurnId}/steering")
        {
            Content = JsonContent.Create(new { text = "这条不应被下一个 Turn 消费。" }),
        };
        request.Headers.Add("X-Workspace-Id", ids.WorkspaceId);

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.IsFalse(await db.SessionSteeringMessages
            .AnyAsync(item => item.SessionId == ids.ConversationId));
    }

    private static async Task<CommandIds> InsertCommandAsync(string status)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var ids = new CommandIds(
            $"ws-{suffix}",
            $"conv-{suffix}",
            $"turn-{suffix}",
            $"agent-{suffix}");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
        {
            CommandId = $"cmd-{suffix}",
            BatchId = $"batch-{suffix}",
            WorkspaceId = ids.WorkspaceId,
            SessionId = ids.ConversationId,
            MessageId = $"assistant-{suffix}",
            UserMessageId = $"user-{suffix}",
            TurnId = ids.TurnId,
            AgentInstanceId = ids.AgentId,
            UserId = "admin",
            ChannelId = "admin-chat",
            RunId = $"run-{suffix}",
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        await db.SaveChangesAsync();
        return ids;
    }

    private sealed record SteeringResponse(string SteeringId);
    private sealed record CommandIds(
        string WorkspaceId,
        string ConversationId,
        string TurnId,
        string AgentId);
}
