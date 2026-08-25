using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingWebApiTests;

/// <summary>
/// ADR-074 G1: Goal Control Plane API 契约测试。
/// 注意：依赖 PuddingHost 完整编译（当前仓库另有 ADR-076 Storage 在制品占位），
/// 该在制品修复后本套件随 CI 恢复运行。
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class GoalApiContractTests
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
            db.Workspaces.Add(new WorkspaceEntity
            {
                WorkspaceId = WorkspaceId,
                Name = "Default Workspace",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }

        if (!db.WorkspaceAgents.Any(a =>
                a.WorkspaceEntityId == workspace!.Id && a.AgentId == AgentId))
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
    public async Task GoalCommands_Create_Pause_Resume_Cancel_Lifecycle()
    {
        var conversationId = $"goal-conv-{Guid.NewGuid():N}";

        var created = await PostGoalCommandAsync(conversationId, new
        {
            agentId = AgentId,
            clientRequestId = NewId("req"),
            action = "set",
            objective = "修复全部失败测试并保持公开 API 不变",
            rounds = 32,
        });
        Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);
        var createBody = await ReadJsonAsync(created);
        Assert.IsTrue(createBody.Success);
        Assert.AreEqual("active", createBody.Goal.Phase);
        Assert.AreEqual(32, createBody.Goal.MaxIterations);
        Assert.IsNotNull(createBody.Goal.GoalRunId);

        var paused = await PostGoalCommandAsync(conversationId, new
        {
            agentId = AgentId,
            clientRequestId = NewId("req"),
            action = "pause",
            reason = "integration test",
        });
        var pauseBody = await ReadJsonAsync(paused);
        Assert.IsTrue(pauseBody.Success);
        Assert.AreEqual("paused", pauseBody.Goal.Phase);

        var resumed = await PostGoalCommandAsync(conversationId, new
        {
            agentId = AgentId,
            clientRequestId = NewId("req"),
            action = "resume",
        });
        var resumeBody = await ReadJsonAsync(resumed);
        Assert.IsTrue(resumeBody.Success);
        Assert.AreEqual("active", resumeBody.Goal.Phase);
        Assert.AreEqual(createBody.Goal.GoalRunId, resumeBody.Goal.GoalRunId);

        var cancelled = await PostGoalCommandAsync(conversationId, new
        {
            agentId = AgentId,
            clientRequestId = NewId("req"),
            action = "cancel",
        });
        var cancelBody = await ReadJsonAsync(cancelled);
        Assert.IsTrue(cancelBody.Success);
        Assert.AreEqual("cancelled", cancelBody.Goal.Phase);

        // G1 出口：命令链路不创建 Agent Turn。
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            Assert.AreEqual(0, await db.ConversationTurns
                .CountAsync(t => t.ConversationId == conversationId));
            Assert.AreEqual(1, await db.GoalRuns
                .CountAsync(g => g.CurrentConversationId == conversationId));
        }
    }

    [TestMethod]
    public async Task GoalCommands_Replay_With_Same_ClientRequestId_Returns_First_Goal()
    {
        var conversationId = $"goal-conv-{Guid.NewGuid():N}";
        var clientRequestId = NewId("req");

        var first = await PostGoalCommandAsync(conversationId, new
        {
            agentId = AgentId,
            clientRequestId,
            action = "set",
            objective = "第一次目标",
        });
        var replay = await PostGoalCommandAsync(conversationId, new
        {
            agentId = AgentId,
            clientRequestId,
            action = "set",
            objective = "第二次目标",
        });

        var firstBody = await ReadJsonAsync(first);
        var replayBody = await ReadJsonAsync(replay);
        Assert.AreEqual(firstBody.Goal.GoalRunId, replayBody.Goal.GoalRunId);
        Assert.AreEqual("第一次目标", replayBody.Goal.Objective);
    }

    [TestMethod]
    public async Task GoalCommands_Conflict_When_NonTerminal_Goal_Exists()
    {
        var conversationId = $"goal-conv-{Guid.NewGuid():N}";
        await PostGoalCommandAsync(conversationId, new
        {
            agentId = AgentId,
            clientRequestId = NewId("req"),
            action = "set",
            objective = "目标 A",
        });

        var conflict = await PostGoalCommandAsync(conversationId, new
        {
            agentId = AgentId,
            clientRequestId = NewId("req"),
            action = "set",
            objective = "目标 B",
        });

        var body = await ReadJsonAsync(conflict);
        Assert.IsFalse(body.Success);
        Assert.AreEqual("goal_conflict", body.ErrorCode);
    }

    [TestMethod]
    public async Task GoalCommands_Rejects_Out_Of_Range_Rounds()
    {
        var conversationId = $"goal-conv-{Guid.NewGuid():N}";
        var response = await PostGoalCommandAsync(conversationId, new
        {
            agentId = AgentId,
            clientRequestId = NewId("req"),
            action = "set",
            objective = "越界预算",
            rounds = 257,
        });

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [TestMethod]
    public async Task GoalQueries_Return_Snapshot_And_Empty_Iterations()
    {
        var conversationId = $"goal-conv-{Guid.NewGuid():N}";
        var created = await ReadJsonAsync(await PostGoalCommandAsync(conversationId, new
        {
            agentId = AgentId,
            clientRequestId = NewId("req"),
            action = "set",
            objective = "查询目标",
        }));
        var goalRunId = created.Goal.GoalRunId;

        _client.DefaultRequestHeaders.Add("X-Workspace-Id", WorkspaceId);
        var goalResponse = await _client.GetAsync(
            $"/api/v1/conversations/{conversationId}/goal");
        Assert.AreEqual(HttpStatusCode.OK, goalResponse.StatusCode);
        var goalBody = await goalResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(goalRunId, goalBody.GetProperty("goal").GetProperty("goalRunId").GetString());

        var byId = await _client.GetAsync($"/api/v1/goals/{goalRunId}");
        Assert.AreEqual(HttpStatusCode.OK, byId.StatusCode);

        var iterations = await _client.GetAsync($"/api/v1/goals/{goalRunId}/iterations");
        Assert.AreEqual(HttpStatusCode.OK, iterations.StatusCode);
        var iterationsBody = await iterations.Content.ReadFromJsonAsync<JsonElement>();
        // G1：无 durable outbox 续行，iteration 明细为空。
        Assert.AreEqual(0, iterationsBody.GetProperty("iterations").GetArrayLength());

        var missing = await _client.GetAsync($"/api/v1/goals/does-not-exist");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [TestMethod]
    public async Task GoalQueries_Require_Workspace_Header()
    {
        var response = await _client.GetAsync(
            $"/api/v1/conversations/goal-conv-none/goal");
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<HttpResponseMessage> PostGoalCommandAsync(
        string conversationId, object payload)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/goals/commands")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("X-Workspace-Id", WorkspaceId);
        return await _client.SendAsync(request);
    }

    private async Task<(bool Success, string? ErrorCode, GoalDto Goal)> ReadJsonAsync(
        HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var goal = body.GetProperty("goal");
        return (
            body.GetProperty("success").GetBoolean(),
            body.TryGetProperty("errorCode", out var code) ? code.GetString() : null,
            new GoalDto(
                goal.GetProperty("goalRunId").GetString()!,
                goal.GetProperty("phase").GetString()!,
                goal.GetProperty("objective").GetString()!,
                goal.GetProperty("maxIterations").GetInt32()));
    }

    private sealed record GoalDto(string GoalRunId, string Phase, string Objective, int MaxIterations);

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
