using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Goals;
using PuddingCode.Platform;
using PuddingCode.Tasks;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Goals;
using PuddingPlatform.Services.Todo;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// TD-2：GET /api/v1/goals/{goalId}/todo 只读链路（GoalQueryService.GetTodoAsync + 控制器映射）。
/// 覆盖 4 场景：① 有数据（items/summary/revision）② found=false（未写拆解 ⇒ 200，非错误）
/// ③ 归属 Agent 解析正确（按 goal.AgentInstanceId 过滤；scope_id 相同时其它 Agent 的列表不可见，
/// 客户端没有任何途径影响读取范围）④ goal 不存在 → 404 goal_not_found。
/// 附 wire 契约：Items/Summary 复用 TodoItemView/TodoSummary（面板与 todo_read 工具同一视图）。
/// </summary>
[TestClass]
public sealed class GoalTodoQueryTests
{
    private sealed class NoopSignal : ICommittedEventSignal
    {
        public ValueTask WaitForChangeAsync(string conversationId, long knownHead, CancellationToken ct)
            => ValueTask.FromCanceled(ct);

        public void Signal(string conversationId, long committedSequence)
        {
        }
    }

    private string _testRoot = null!;
    private PlatformDbContextFactory _dbFactory = null!;
    private PlatformDbContext _db = null!;
    private GoalRunStore _goalStore = null!;
    private TodoStore _todoStore = null!;
    private GoalQueryService _service = null!;
    private GoalQueriesController _controller = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "goal-todo-query-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_testRoot, "platform.db")};Default Timeout=10")
            .Options;
        _dbFactory = new PlatformDbContextFactory(options);
        _db = await _dbFactory.CreateDbContextAsync();
        await _db.Database.EnsureCreatedAsync();

        _goalStore = new GoalRunStore(_db, new NoopSignal(), NullLogger<GoalRunStore>.Instance);
        _todoStore = new TodoStore(_dbFactory);
        _service = new GoalQueryService(
            _goalStore,
            _db,
            new GoalCheckRecordStore(_dbFactory),
            _todoStore);
        _controller = new GoalQueriesController(_service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        _db.Dispose();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private static GoalRunEntity NewGoal(string id, string agentInstanceId) => new()
    {
        GoalRunId = id,
        WorkspaceId = "ws",
        CurrentConversationId = $"conv-{id}",
        AgentInstanceId = agentInstanceId,
        Objective = "TD-2 拆解面板验收",
        Status = GoalPhase.Active,
        MaxIterations = 8,
        SourceCommandId = $"cmd-{id}",
    };

    private async Task<GoalRunEntity> CreateGoalAsync(string id, string agentInstanceId)
        => await _goalStore.CreateAsync(NewGoal(id, agentInstanceId), $"trace-{id}", CancellationToken.None);

    private async Task<TodoWriteResult> WriteListAsync(string agentId, string goalRunId, string marker)
        => await _todoStore.WriteAsync(new TodoWriteRequest
        {
            AgentId = agentId,
            ScopeKind = TodoWireMaps.ScopeGoal,
            ScopeId = goalRunId,
            Title = $"{marker} 的拆解",
            ExpectedRevision = 0,
            Items =
            [
                new TodoItemInput
                {
                    Slug = "audit",
                    Title = "审计现状",
                    Status = TodoWireMaps.StatusInProgress,
                    Note = $"{marker} note",
                },
                new TodoItemInput
                {
                    Slug = "fix",
                    Title = "修复根因",
                    Status = TodoWireMaps.StatusBlocked,
                    BlockedReason = $"{marker} 等待上游修复",
                    EvidenceRef = "commit:deadbee",
                },
                new TodoItemInput
                {
                    Slug = "report",
                    Title = "写报告",
                    Status = TodoWireMaps.StatusPending,
                },
            ],
        });

    [TestMethod]
    public async Task GetSteps_SeparatesCompilerVersionFromSchedulingRevision()
    {
        var goal = await CreateGoalAsync("version-goal", "agent-a");
        _db.TaskPlanRuns.Add(new TaskPlanRunEntity
        {
            PlanId = "version-plan", WorkspaceId = "ws", PlanVersion = 2, PlanRevision = 9,
            RootSessionId = goal.CurrentConversationId, LeaderAgentId = goal.AgentInstanceId,
        });
        _db.TaskGoalBindings.Add(new TaskGoalBindingEntity
        {
            BindingId = "version-binding", WorkspaceId = "ws", TaskId = "task",
            GoalRunId = goal.GoalRunId, AgentInstanceId = goal.AgentInstanceId, TaskPlanId = "version-plan",
        });
        _db.TaskNodes.Add(new TaskNodeEntity
        {
            TaskNodeId = "version-node", PlanId = "version-plan", Depth = 1, Status = "Planned",
        });
        await _db.SaveChangesAsync();
        var response = Assert.IsInstanceOfType<OkObjectResult>(
            await _controller.GetGoalSteps(goal.GoalRunId, CancellationToken.None));
        var dto = Assert.IsInstanceOfType<GoalQueriesController.GoalStepsResponseDto>(response.Value);
        Assert.AreEqual(2, dto.PlanVersion);
        Assert.AreEqual(9, dto.PlanRevision);
    }

    [TestMethod]
    public async Task GetTodo_WithList_ReturnsItemsSummaryRevisionAndFoundTrue()
    {
        var goal = await CreateGoalAsync("goal-1", "agent-a");
        await WriteListAsync("agent-a", goal.GoalRunId, "agent-a");

        var result = await _controller.GetGoalTodo(goal.GoalRunId, CancellationToken.None);

        var ok = Assert.IsInstanceOfType<OkObjectResult>(result);
        var dto = Assert.IsInstanceOfType<GoalQueriesController.GoalTodoResponseDto>(ok.Value);
        Assert.IsTrue(dto.Found);
        Assert.AreEqual(goal.GoalRunId, dto.GoalRunId);
        Assert.AreEqual("agent-a", dto.Title!.Split(' ')[0]);
        Assert.AreEqual(1, dto.Revision);
        Assert.AreEqual(3, dto.Items.Count);
        Assert.IsNotNull(dto.Summary);
        Assert.AreEqual(3, dto.Summary.Total);
        Assert.AreEqual(1, dto.Summary.Pending);
        Assert.AreEqual(1, dto.Summary.InProgress);
        Assert.AreEqual(0, dto.Summary.Completed);
        Assert.AreEqual(1, dto.Summary.Blocked);
        Assert.AreEqual("audit", dto.Summary.CurrentSlug);
        CollectionAssert.AreEquivalent(
            new[] { "fix" }, dto.Summary.BlockedSlugs.ToList());

        // item 形状：blocked_reason / evidence_ref / note 逐项透出（camelCase wire 由序列化保证）。
        var fix = dto.Items.Single(i => i.Slug == "fix");
        Assert.AreEqual("agent-a 等待上游修复", fix.BlockedReason);
        Assert.AreEqual("commit:deadbee", fix.EvidenceRef);
        var audit = dto.Items.Single(i => i.Slug == "audit");
        Assert.AreEqual("agent-a note", audit.Note);
    }

    [TestMethod]
    public async Task GetTodo_GoalExistsWithoutList_Returns200WithFoundFalse()
    {
        var goal = await CreateGoalAsync("goal-2", "agent-a");

        var result = await _controller.GetGoalTodo(goal.GoalRunId, CancellationToken.None);

        // 未写拆解不是错误：200 + found=false（与 todo_read 工具同语义），前端显示「尚未写拆解」。
        var ok = Assert.IsInstanceOfType<OkObjectResult>(result);
        var dto = Assert.IsInstanceOfType<GoalQueriesController.GoalTodoResponseDto>(ok.Value);
        Assert.IsFalse(dto.Found);
        Assert.IsNull(dto.ListId);
        Assert.IsNull(dto.Summary);
        Assert.AreEqual(0, dto.Revision);
        Assert.AreEqual(0, dto.Items.Count);
    }

    /// <summary>
    /// 隔离硬约束：读取范围由 goal 行的 AgentInstanceId 服务端解析。
    /// 同一 scope_id（goalRunId）下两个 Agent 各写一份内容不同的列表 ⇒
    /// 只返回 goal 归属 Agent 的那份；response.AgentInstanceId 必须等于 goal.AgentInstanceId。
    /// </summary>
    [TestMethod]
    public async Task GetTodo_ResolvesAgentFromGoal_IgnoresOtherAgentsListOnSameScope()
    {
        var goalOfA = await CreateGoalAsync("goal-3", "agent-a");
        await CreateGoalAsync("goal-4", "agent-b");

        // 两个 Agent 对同一 scope_id 各写一份（内容不同：marker 区分）。
        await WriteListAsync("agent-b", goalOfA.GoalRunId, "agent-b");
        await WriteListAsync("agent-a", goalOfA.GoalRunId, "agent-a");

        var result = await _controller.GetGoalTodo(goalOfA.GoalRunId, CancellationToken.None);

        var ok = Assert.IsInstanceOfType<OkObjectResult>(result);
        var dto = Assert.IsInstanceOfType<GoalQueriesController.GoalTodoResponseDto>(ok.Value);
        // 断言用的是 goal.AgentInstanceId —— 客户端（query/header/body）没有任何可影响读取范围的入参。
        Assert.AreEqual("agent-a", goalOfA.AgentInstanceId);
        Assert.AreEqual(goalOfA.AgentInstanceId, dto.Title!.Split(' ')[0]);
        var audit = dto.Items.Single(i => i.Slug == "audit");
        Assert.AreEqual("agent-a note", audit.Note);
        var fix = dto.Items.Single(i => i.Slug == "fix");
        Assert.AreEqual("agent-a 等待上游修复", fix.BlockedReason);
    }

    [TestMethod]
    public async Task GetTodo_UnknownGoal_Returns404GoalNotFound()
    {
        var result = await _controller.GetGoalTodo("no-such-goal", CancellationToken.None);

        var notFound = Assert.IsInstanceOfType<ObjectResult>(result);
        Assert.AreEqual(StatusCodes.Status404NotFound, notFound.StatusCode);
        var problem = Assert.IsInstanceOfType<ProblemDetails>(notFound.Value);
        Assert.AreEqual("goal_not_found", problem.Title);
    }

    [TestMethod]
    public async Task GetTodo_WireContract_SerializesTodoViewWithCamelCaseFields()
    {
        // 面板与 todo_read 工具同一视图（设计 §4）：响应直接复用 TodoItemView/TodoSummary，
        // camelCase 序列化后字段名与前端契约一致（evidenceRef/blockedReason/inProgress/blockedSlugs...）。
        var goal = await CreateGoalAsync("goal-5", "agent-a");
        await WriteListAsync("agent-a", goal.GoalRunId, "agent-a");

        var result = await _controller.GetGoalTodo(goal.GoalRunId, CancellationToken.None);
        var ok = (OkObjectResult)result;
        var json = System.Text.Json.JsonSerializer.Serialize(
            ok.Value,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.IsTrue(json.Contains("\"evidenceRef\""), json);
        Assert.IsTrue(json.Contains("\"blockedReason\""), json);
        Assert.IsTrue(json.Contains("\"inProgress\""), json);
        Assert.IsTrue(json.Contains("\"currentSlug\""), json);
        Assert.IsTrue(json.Contains("\"blockedSlugs\""), json);
        Assert.IsTrue(json.Contains("\"found\":true"), json);
    }
}
