using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Goals;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Goals;

namespace PuddingWebApiTests;

/// <summary>
/// W2：GET /api/v1/goals/{goalId}/steps 只读投影契约测试。
/// 覆盖：steps 按 sequence_no 升序、currentStepId 与 GoalRunStore.FindCurrentTaskWorkUnitAsync 同源、
/// 无冻结计划 → 200 + 空投影、goal 不存在 → 404 goal_not_found、checks 为目标级且仅当前 epoch。
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class GoalStepsApiTests
{
    private const string WorkspaceId = "default";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static CustomWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _) => _factory = new CustomWebApplicationFactory();

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
    public async Task Steps_Are_Sorted_By_SequenceNo_With_Progress_And_Evidence()
    {
        var goalId = $"g-steps-{Guid.NewGuid():N}";
        var planId = $"plan-{goalId}";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = GetDb(scope);
            await SeedGoalAsync(db, goalId);
            await SeedBindingAsync(db, goalId, planId);
            await SeedPlanAsync(db, planId, planVersion: 3);

            // 故意乱序播种；depth0 根节点与其它 plan 的 depth1 叶子都必须被过滤掉。
            await SeedNodeAsync(db, MakeNode(planId, "n-b", 2, "Completed",
                artifactRef: "artifact://result-b", startedMs: NowMs, completedMs: NowMs));
            await SeedNodeAsync(db, MakeNode(planId, "n-a", 1, "Running", startedMs: NowMs));
            await SeedNodeAsync(db, MakeNode(planId, "n-c", 3, "Planned"));
            await SeedNodeAsync(db, MakeNode(planId, "n-root", 0, "Planned", depth: 0));
            await SeedNodeAsync(db, MakeNode("plan-other", "n-other", 1, "Running"));

            // await handle 是 (plan_id, task_node_id) 真实外键 → evidenceRefs
            db.WorkUnitAwaitHandles.Add(new WorkUnitAwaitHandleEntity
            {
                AwaitHandleId = $"ah-{goalId}",
                PlanId = planId,
                TaskNodeId = "n-a",
                Kind = "subagent",
                ExternalId = "ext-1",
                FencingToken = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/v1/goals/{goalId}/steps");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.AreEqual(goalId, body.GetProperty("goalRunId").GetString());
        Assert.AreEqual("active", body.GetProperty("phase").GetString());
        Assert.AreEqual(3, body.GetProperty("planVersion").GetInt32());
        Assert.IsTrue(body.GetProperty("hasPlan").GetBoolean());

        var steps = body.GetProperty("steps");
        Assert.AreEqual(3, steps.GetArrayLength());
        Assert.AreEqual("n-a", steps[0].GetProperty("nodeId").GetString());
        Assert.AreEqual(1, steps[0].GetProperty("sequenceNo").GetInt32());
        Assert.AreEqual("n-b", steps[1].GetProperty("nodeId").GetString());
        Assert.AreEqual(2, steps[1].GetProperty("sequenceNo").GetInt32());
        Assert.AreEqual("n-c", steps[2].GetProperty("nodeId").GetString());
        Assert.AreEqual(3, steps[2].GetProperty("sequenceNo").GetInt32());

        var progress = body.GetProperty("progress");
        Assert.AreEqual(3, progress.GetProperty("stepsTotal").GetInt32());
        Assert.AreEqual(1, progress.GetProperty("stepsPassed").GetInt32());
        Assert.AreEqual(0, progress.GetProperty("stepsFailed").GetInt32());
        Assert.AreEqual(2, progress.GetProperty("stepsInProgress").GetInt32());
        // 首个非终态（seq1 Running）= n-a，与 GoalRunStore 选定一致。
        Assert.AreEqual("n-a", progress.GetProperty("currentStepId").GetString());

        // Completed 步骤：时间戳与 result_artifact_ref 证据
        Assert.AreEqual("Completed", steps[1].GetProperty("status").GetString());
        Assert.AreEqual(
            JsonValueKind.String, steps[1].GetProperty("startedAtUtc").ValueKind);
        Assert.AreEqual(
            JsonValueKind.String, steps[1].GetProperty("completedAtUtc").ValueKind);
        Assert.AreEqual("artifact://result-b", steps[1].GetProperty("evidenceRefs")[0].GetString());

        // Running 步骤：await handle 外键投射（blockerCode 无列，恒 null）
        var evidenceA = steps[0].GetProperty("evidenceRefs");
        Assert.AreEqual(1, evidenceA.GetArrayLength());
        Assert.AreEqual($"await-handle:ah-{goalId}", evidenceA[0].GetString());
        Assert.AreEqual(JsonValueKind.Null, steps[0].GetProperty("blockerCode").ValueKind);
    }

    [TestMethod]
    public async Task CurrentStepId_Matches_GoalRunStore_Selection()
    {
        var goalId = $"g-cur-{Guid.NewGuid():N}";
        var planId = $"plan-{goalId}";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = GetDb(scope);
            await SeedGoalAsync(db, goalId);
            await SeedBindingAsync(db, goalId, planId);
            await SeedPlanAsync(db, planId, planVersion: 1);
            await SeedNodeAsync(db, MakeNode(planId, "c1", 1, "Completed", completedMs: NowMs));
            await SeedNodeAsync(db, MakeNode(planId, "c2", 2, "Blocked"));
            await SeedNodeAsync(db, MakeNode(planId, "c3", 3, "Planned"));
        }

        // 期望值直接取自 GoalRunStore 的选定逻辑（同源判据，不是另写一套判断）。
        string expected;
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<GoalRunStore>();
            expected = (await store.FindCurrentTaskWorkUnitAsync(planId))!.TaskNodeId;
        }

        var response = await _client.GetAsync($"/api/v1/goals/{goalId}/steps");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("c2", expected);
        Assert.AreEqual(expected, body.GetProperty("progress").GetProperty("currentStepId").GetString());

        // 全部终态后：API 与 Store 同为 null。
        using (var scope = _factory.Services.CreateScope())
        {
            var db = GetDb(scope);
            var nodes = await db.TaskNodes.Where(n => n.PlanId == planId).ToListAsync();
            foreach (var node in nodes)
            {
                node.Status = "Completed";
                node.CompletedAt = NowMs;
            }
            await db.SaveChangesAsync();
        }

        var after = await _client.GetAsync($"/api/v1/goals/{goalId}/steps");
        var afterBody = await after.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(
            JsonValueKind.Null,
            afterBody.GetProperty("progress").GetProperty("currentStepId").ValueKind);

        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<GoalRunStore>();
            Assert.IsNull(await store.FindCurrentTaskWorkUnitAsync(planId));
        }
    }

    [TestMethod]
    public async Task NoPlan_Returns_200_With_Empty_Projection()
    {
        // case 1：goal 无 task 绑定。
        var noBinding = $"g-noplan-{Guid.NewGuid():N}";
        using (var scope = _factory.Services.CreateScope())
        {
            await SeedGoalAsync(GetDb(scope), noBinding);
        }

        var r1 = await _client.GetAsync($"/api/v1/goals/{noBinding}/steps");
        Assert.AreEqual(HttpStatusCode.OK, r1.StatusCode);
        var b1 = await r1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsFalse(b1.GetProperty("hasPlan").GetBoolean());
        Assert.AreEqual(0, b1.GetProperty("steps").GetArrayLength());
        Assert.AreEqual(0, b1.GetProperty("checks").GetArrayLength());
        Assert.AreEqual(JsonValueKind.Null, b1.GetProperty("planVersion").ValueKind);
        var p1 = b1.GetProperty("progress");
        Assert.AreEqual(0, p1.GetProperty("stepsTotal").GetInt32());
        Assert.AreEqual(0, p1.GetProperty("stepsPassed").GetInt32());
        Assert.AreEqual(0, p1.GetProperty("stepsFailed").GetInt32());
        Assert.AreEqual(0, p1.GetProperty("stepsInProgress").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, p1.GetProperty("currentStepId").ValueKind);

        // case 2：绑定存在、plan run 存在，但没有 depth1 叶子。
        var noLeaves = $"g-noleaves-{Guid.NewGuid():N}";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = GetDb(scope);
            await SeedGoalAsync(db, noLeaves);
            await SeedBindingAsync(db, noLeaves, $"plan-{noLeaves}");
            await SeedPlanAsync(db, $"plan-{noLeaves}");
        }

        var r2 = await _client.GetAsync($"/api/v1/goals/{noLeaves}/steps");
        Assert.AreEqual(HttpStatusCode.OK, r2.StatusCode);
        var b2 = await r2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsFalse(b2.GetProperty("hasPlan").GetBoolean());
        Assert.AreEqual(0, b2.GetProperty("steps").GetArrayLength());
        Assert.AreEqual(JsonValueKind.Null, b2.GetProperty("progress").GetProperty("currentStepId").ValueKind);
    }

    [TestMethod]
    public async Task Unknown_Goal_Returns_404_GoalNotFound()
    {
        var response = await _client.GetAsync("/api/v1/goals/g-does-not-exist/steps");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(body.Contains("goal_not_found"), $"Unexpected body: {body}");
    }

    [TestMethod]
    public async Task Checks_Are_Goal_Level_Only_Current_Epoch_With_Report_Facts()
    {
        var goalId = $"g-chk-{Guid.NewGuid():N}";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = GetDb(scope);
            await SeedGoalAsync(db, goalId, activationEpoch: 1);

            var passedReport = JsonSerializer.Serialize(new GoalCheckReport
            {
                CheckId = "chk-pass",
                CriterionId = "crit-1",
                Status = GoalCriterionResultStatuses.Passed,
                ExitCode = 0,
                Message = "w2 checks projection test passed",
                EvidenceRefs = ["Docs/w2-evidence.md", "temp/w2-run.txt"],
            }, JsonOpts);

            await SeedCheckRecordAsync(db, goalId, activationEpoch: 1, "chk-pass", "crit-1",
                GoalCheckRecordStatuses.Finished, passedReport);
            await SeedCheckRecordAsync(db, goalId, activationEpoch: 1, "chk-pending", "crit-2",
                GoalCheckRecordStatuses.Pending, reportJson: null);
            // 旧 epoch 的记录是旧进程证据（ADR-092 §6.2），不得回吐。
            await SeedCheckRecordAsync(db, goalId, activationEpoch: 2, "chk-old-epoch", "crit-3",
                GoalCheckRecordStatuses.Finished, passedReport);
            // 其它 goal 的记录不得串台。
            await SeedCheckRecordAsync(db, "g-other-goal-not-exist", activationEpoch: 1, "chk-other-goal", "crit-9",
                GoalCheckRecordStatuses.Finished, passedReport);
        }

        var response = await _client.GetAsync($"/api/v1/goals/{goalId}/steps");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var checks = body.GetProperty("checks");
        Assert.AreEqual(2, checks.GetArrayLength());

        var passed = checks.EnumerateArray()
            .First(c => c.GetProperty("checkId").GetString() == "chk-pass");
        Assert.AreEqual("passed", passed.GetProperty("status").GetString());
        Assert.AreEqual(0, passed.GetProperty("exitCode").GetInt32());
        Assert.AreEqual("w2 checks projection test passed", passed.GetProperty("summary").GetString());
        Assert.AreEqual(2, passed.GetProperty("evidenceRefs").GetArrayLength());
        Assert.AreEqual("Docs/w2-evidence.md", passed.GetProperty("evidenceRefs")[0].GetString());

        var pending = checks.EnumerateArray()
            .First(c => c.GetProperty("checkId").GetString() == "chk-pending");
        Assert.AreEqual("pending", pending.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, pending.GetProperty("exitCode").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, pending.GetProperty("summary").ValueKind);
        Assert.AreEqual(0, pending.GetProperty("evidenceRefs").GetArrayLength());
    }

    private static PlatformDbContext GetDb(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static async Task SeedGoalAsync(PlatformDbContext db, string goalRunId, int activationEpoch = 1)
    {
        var now = DateTimeOffset.UtcNow;
        db.GoalRuns.Add(new GoalRunEntity
        {
            GoalRunId = goalRunId,
            WorkspaceId = WorkspaceId,
            CurrentConversationId = $"conv-{goalRunId}",
            AgentInstanceId = "default-agent",
            Objective = "w2 steps projection test",
            Status = GoalPhase.Active,
            ActivationEpoch = activationEpoch,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedBindingAsync(PlatformDbContext db, string goalRunId, string planId)
    {
        db.TaskGoalBindings.Add(new TaskGoalBindingEntity
        {
            BindingId = $"bind-{goalRunId}",
            WorkspaceId = WorkspaceId,
            TaskId = $"task-{goalRunId}",
            GoalRunId = goalRunId,
            AgentInstanceId = "default-agent",
            TaskPlanId = planId,
            Status = "active",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedPlanAsync(PlatformDbContext db, string planId, int planVersion = 1)
    {
        db.TaskPlanRuns.Add(new TaskPlanRunEntity
        {
            PlanId = planId,
            WorkspaceId = WorkspaceId,
            RootSessionId = $"sess-{planId}",
            LeaderAgentId = "default-agent",
            PlanVersion = planVersion,
            CreatedAt = NowMs,
            UpdatedAt = NowMs,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedNodeAsync(PlatformDbContext db, TaskNodeEntity node)
    {
        db.TaskNodes.Add(node);
        await db.SaveChangesAsync();
    }

    private static TaskNodeEntity MakeNode(
        string planId,
        string nodeId,
        int sequenceNo,
        string status,
        string? kind = "Change",
        string? artifactRef = null,
        long? startedMs = null,
        long? completedMs = null,
        int depth = 1)
        => new()
        {
            TaskNodeId = nodeId,
            PlanId = planId,
            Depth = depth,
            SequenceNo = sequenceNo,
            WorkUnitKind = kind,
            Title = $"step {nodeId}",
            Status = status,
            AssignedToKind = "Unassigned",
            ResultArtifactRef = artifactRef,
            StartedAt = startedMs,
            CompletedAt = completedMs,
            CreatedAt = NowMs,
            UpdatedAt = NowMs,
        };

    private static async Task SeedCheckRecordAsync(
        PlatformDbContext db,
        string goalRunId,
        int activationEpoch,
        string checkId,
        string criterionId,
        string status,
        string? reportJson)
    {
        db.GoalCheckRecords.Add(new GoalCheckRecordEntity
        {
            CheckRecordId = $"gchk-{goalRunId}-{activationEpoch}-1-{checkId}",
            // 生产去重键含 definitionHash/inputFingerprint，不同检查天然不同；测试播种同样必须逐检查唯一。
            DedupKey = $"{goalRunId}|{activationEpoch}|goal|1|def:{checkId}|fp:{checkId}",
            GoalRunId = goalRunId,
            ActivationEpoch = activationEpoch,
            IterationNo = 1,
            CheckId = checkId,
            CriterionId = criterionId,
            CriterionRevision = 1,
            Status = status,
            Attempt = 0,
            Priority = 0,
            ReportJson = reportJson,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
