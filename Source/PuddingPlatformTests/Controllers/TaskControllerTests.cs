using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Tasks;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatformTests.Controllers;

/// <summary>
/// TB-03: TaskController 单元测试（SQLite 文件临时库 + EnsureCreated，真实 Store + Service + Controller）。
/// 覆盖契约§八 11 项（Create/Get/PATCH CAS/PATCH 非法转换/Assign/RunNow/Reopen/Archive/Cancel/错误协议）。
/// </summary>
[TestClass]
public sealed class TaskControllerTests
{
    private const string WorkspaceId = "ws-1";

    private string _testRoot = null!;
    private PlatformDbContextFactory _dbFactory = null!;
    private SqliteWorkspaceTaskStore _store = null!;
    private TaskCommandService _service = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "task-controller-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_testRoot, "platform.db")};Default Timeout=10")
            .Options;
        _dbFactory = new PlatformDbContextFactory(options);
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        _store = new SqliteWorkspaceTaskStore(_dbFactory);
        _service = new TaskCommandService(_store, _dbFactory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    // ── 1. Create → 201 + Backlog + Backlog + version=1 ──

    [TestMethod]
    public async Task Create_Returns201BacklogVersion1()
    {
        var controller = CreateController();

        var result = await controller.Create(WorkspaceId, new CreateTaskDto
        {
            Title = "My Task",
            Priority = "p1",
        }, CancellationToken.None);

        var created = Assert.IsInstanceOfType<CreatedAtActionResult>(result.Result);
        Assert.AreEqual(201, created.StatusCode);
        var dto = Assert.IsInstanceOfType<TaskDto>(created.Value);
        Assert.AreEqual(WorkspaceId, dto.WorkspaceId);
        Assert.AreEqual("Backlog", dto.Status);
        Assert.AreEqual("Backlog", dto.BoardColumn);
        Assert.AreEqual("p1", dto.Priority);
        Assert.AreEqual("inherit", dto.ExecutionWindow);
        Assert.AreEqual(1, dto.Version);
    }

    // ── 2. Get 命中/未命中（404 + task.not_found）──

    [TestMethod]
    public async Task Get_ReturnsTaskOr404NotFound()
    {
        var task = await CreateTaskAsync();
        var controller = CreateController();

        var hit = await controller.Get(WorkspaceId, task.TaskId, CancellationToken.None);
        var ok = Assert.IsInstanceOfType<OkObjectResult>(hit.Result);
        Assert.AreEqual(task.TaskId, Assert.IsInstanceOfType<TaskDto>(ok.Value).TaskId);

        var miss = await controller.Get(WorkspaceId, "missing", CancellationToken.None);
        var err = AssertError(miss, 404);
        Assert.AreEqual("task.not_found", err.Code);
    }

    // ── 3. PATCH CAS 成功（version+1）/ 冲突（409 + actualVersion）──

    [TestMethod]
    public async Task Patch_Succeeds_IncrementsVersion()
    {
        var task = await CreateTaskAsync();
        var controller = CreateController();

        var result = await controller.Patch(WorkspaceId, task.TaskId, new PatchTaskDto
        {
            ExpectedVersion = 1,
            Title = "Renamed",
            Priority = "p0",
        }, CancellationToken.None);

        var dto = AssertOkDto(result);
        Assert.AreEqual(2, dto.Version);
        Assert.AreEqual("Renamed", dto.Title);
        Assert.AreEqual("p0", dto.Priority);
    }

    [TestMethod]
    public async Task Patch_VersionConflict_Returns409WithActualVersion()
    {
        var task = await CreateTaskAsync();
        await PatchToVersion2Async(task.TaskId);
        var controller = CreateController();

        var result = await controller.Patch(WorkspaceId, task.TaskId, new PatchTaskDto
        {
            ExpectedVersion = 1,
            Title = "stale",
        }, CancellationToken.None);

        var err = AssertError(result, 409);
        Assert.AreEqual("task.version_conflict", err.Code);
        Assert.AreEqual(1, err.ExpectedVersion);
        Assert.AreEqual(2, err.ActualVersion);
    }

    // ── 4. PATCH 非法转换（终态更新）→ 422 + task.invalid_transition ──

    [TestMethod]
    public async Task Patch_TerminalUpdate_Returns422InvalidTransition()
    {
        var task = await CreateTaskAsync();
        await SetStatusAsync(task.TaskId, WorkspaceTaskStatus.Completed);
        var controller = CreateController();

        var result = await controller.Patch(WorkspaceId, task.TaskId, new PatchTaskDto
        {
            ExpectedVersion = 1,
            Title = "cannot update terminal",
        }, CancellationToken.None);

        var err = AssertError(result, 422);
        Assert.AreEqual("task.invalid_transition", err.Code);
    }

    // ── 5. Assign：Ready→Reserved + 建 Assignment + active_assignment_id ──

    [TestMethod]
    public async Task Assign_ReadyToReserved_ViaController()
    {
        var task = await CreateTaskAsync();
        await SetStatusAsync(task.TaskId, WorkspaceTaskStatus.Ready);
        var controller = CreateController();

        var result = await controller.Assign(WorkspaceId, task.TaskId, new AssignDto
        {
            AgentId = "agent-1",
            ExpectedVersion = 1,
        }, CancellationToken.None);

        var dto = AssertOkDto(result);
        Assert.AreEqual("Reserved", dto.Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(dto.ActiveAssignmentId));

        var attempt = (await GetAttemptsAsync(task.TaskId)).Single();
        Assert.AreEqual(AssignmentAttemptStatus.Reserved, attempt.Status);
        Assert.AreEqual(dto.ActiveAssignmentId, attempt.AttemptId);
    }

    // ── 6. RunNow：Deferred→Reserved + windowDecision 记录 ──

    [TestMethod]
    public async Task RunNow_DeferredToReserved_ViaController()
    {
        var task = await CreateTaskAsync();
        await SetStatusAsync(task.TaskId, WorkspaceTaskStatus.Deferred);
        var controller = CreateController();

        var result = await controller.RunNow(WorkspaceId, task.TaskId, new RunNowDto
        {
            AgentId = "agent-2",
            ExpectedVersion = 1,
            WindowDecision = "allowed_user_direct",
        }, CancellationToken.None);

        var dto = AssertOkDto(result);
        Assert.AreEqual("Reserved", dto.Status);

        var attempt = (await GetAttemptsAsync(task.TaskId)).Single();
        Assert.AreEqual("allowed_user_direct", attempt.WindowDecision);
    }

    // ── 7. Reopen：Failed→Ready + version 递增 + task.reopened 事件 ──

    [TestMethod]
    public async Task Reopen_FailedToReady_ViaController()
    {
        var task = await CreateTaskAsync();
        await SetStatusAsync(task.TaskId, WorkspaceTaskStatus.Failed);
        var controller = CreateController();

        var result = await controller.Reopen(WorkspaceId, task.TaskId, new CommandDto
        {
            ExpectedVersion = 1,
        }, CancellationToken.None);

        var dto = AssertOkDto(result);
        Assert.AreEqual("Ready", dto.Status);
        Assert.AreEqual(2, dto.Version);

        var events = await GetEventsAsync(task.TaskId);
        Assert.AreEqual(TaskEventType.TaskReopened, events[^1].EventType);
    }

    // ── 8. Archive：Completed→Archived ──

    [TestMethod]
    public async Task Archive_CompletedToArchived_ViaController()
    {
        var task = await CreateTaskAsync();
        await SetStatusAsync(task.TaskId, WorkspaceTaskStatus.Completed);
        var controller = CreateController();

        var result = await controller.Archive(WorkspaceId, task.TaskId, new CommandDto
        {
            ExpectedVersion = 1,
        }, CancellationToken.None);

        var dto = AssertOkDto(result);
        Assert.AreEqual("Archived", dto.Status);
        Assert.IsNotNull(dto.ArchivedAtUtc);
    }

    // ── 9. Cancel：InProgress→Cancelled + 释放 active Assignment ──

    [TestMethod]
    public async Task Cancel_InProgressToCancelled_ViaController()
    {
        var task = await CreateTaskAsync();
        await SeedActiveAssignmentAsync(task.TaskId, "agent-3");
        var controller = CreateController();

        var result = await controller.Cancel(WorkspaceId, task.TaskId, new CommandDto
        {
            ExpectedVersion = 1,
        }, CancellationToken.None);

        var dto = AssertOkDto(result);
        Assert.AreEqual("Cancelled", dto.Status);
        Assert.IsNull(dto.ActiveAssignmentId);

        var attempt = (await GetAttemptsAsync(task.TaskId)).Single();
        Assert.IsNotNull(attempt.ReleasedAtUtc);
    }

    // ── 10. Delete：硬删（404/204/422）──

    [TestMethod]
    public async Task Delete_HardDeletesBacklog_Returns204Or422Or404()
    {
        var controller = CreateController();

        var miss = await controller.Delete(WorkspaceId, "missing", CancellationToken.None);
        var missObj = Assert.IsInstanceOfType<ObjectResult>(miss);
        Assert.AreEqual(404, missObj.StatusCode);

        var nonBacklog = await CreateTaskAsync();
        await SetStatusAsync(nonBacklog.TaskId, WorkspaceTaskStatus.Ready);
        var cannotDelete = await controller.Delete(WorkspaceId, nonBacklog.TaskId, CancellationToken.None);
        var cannotObj = Assert.IsInstanceOfType<ObjectResult>(cannotDelete);
        Assert.AreEqual(422, cannotObj.StatusCode);

        var fresh = await CreateTaskAsync();
        var deleted = await controller.Delete(WorkspaceId, fresh.TaskId, CancellationToken.None);
        Assert.IsInstanceOfType<NoContentResult>(deleted);
    }

    // ── 11. 错误协议：每种 TaskErrorCode → HTTP 状态 + 稳定 code + traceId ──

    [TestMethod]
    public void ErrorProtocol_MapsEveryCodeToHttpStatusAndStableCode()
    {
        var statusMap = new Dictionary<TaskErrorCode, int>
        {
            [TaskErrorCode.TaskNotFound] = 404,
            [TaskErrorCode.AssignmentNotFound] = 404,
            [TaskErrorCode.AgentNotFound] = 404,
            [TaskErrorCode.TaskVersionConflict] = 409,
            [TaskErrorCode.TaskStateConflict] = 409,
            [TaskErrorCode.AssignmentAlreadyActive] = 409,
            [TaskErrorCode.AssignmentStale] = 409,
            [TaskErrorCode.AgentUnavailable] = 409,
            [TaskErrorCode.PolicyVersionConflict] = 409,
            [TaskErrorCode.TaskInvalidTransition] = 422,
            [TaskErrorCode.TaskInvalidDisposition] = 422,
            [TaskErrorCode.TaskReasonRequired] = 422,
            [TaskErrorCode.TaskResultRequired] = 422,
            [TaskErrorCode.TaskArtifactRequired] = 422,
            [TaskErrorCode.TaskNotReopenable] = 422,
            [TaskErrorCode.TaskCannotHardDelete] = 422,
            [TaskErrorCode.PolicyInvalid] = 422,
            [TaskErrorCode.TaskActiveContextMissing] = 422,
            [TaskErrorCode.TaskInvalidCursor] = 422,
            [TaskErrorCode.CapabilityMissing] = 403,
        };

        var codeMap = new Dictionary<TaskErrorCode, string>
        {
            [TaskErrorCode.TaskNotFound] = "task.not_found",
            [TaskErrorCode.TaskVersionConflict] = "task.version_conflict",
            [TaskErrorCode.TaskStateConflict] = "task.state_conflict",
            [TaskErrorCode.TaskInvalidTransition] = "task.invalid_transition",
            [TaskErrorCode.TaskInvalidDisposition] = "task.invalid_disposition",
            [TaskErrorCode.TaskReasonRequired] = "task.reason_required",
            [TaskErrorCode.TaskResultRequired] = "task.result_required",
            [TaskErrorCode.TaskArtifactRequired] = "task.artifact_required",
            [TaskErrorCode.TaskNotReopenable] = "task.not_reopenable",
            [TaskErrorCode.TaskCannotHardDelete] = "task.cannot_hard_delete",
            [TaskErrorCode.AssignmentNotFound] = "assignment.not_found",
            [TaskErrorCode.AssignmentAlreadyActive] = "assignment.already_active",
            [TaskErrorCode.AssignmentStale] = "assignment.stale",
            [TaskErrorCode.AgentNotFound] = "agent.not_found",
            [TaskErrorCode.AgentUnavailable] = "agent.unavailable",
            [TaskErrorCode.CapabilityMissing] = "capability.missing",
            [TaskErrorCode.PolicyInvalid] = "policy.invalid",
            [TaskErrorCode.PolicyVersionConflict] = "policy.version_conflict",
            [TaskErrorCode.TaskActiveContextMissing] = "task.active_context_missing",
            [TaskErrorCode.TaskInvalidCursor] = "task.invalid_cursor",
        };

        foreach (TaskErrorCode code in Enum.GetValues<TaskErrorCode>())
        {
            Assert.AreEqual(statusMap[code], TaskWireMaps.ErrorCodeToHttpStatus(code), code.ToString());
            Assert.AreEqual(codeMap[code], TaskWireMaps.ErrorCodeToString(code), code.ToString());
        }
    }

    [TestMethod]
    public async Task ErrorProtocol_RealError_ReturnsCodeMessageAndTraceId()
    {
        var controller = CreateController(traceId: "trace-abc");

        var miss = await controller.Get(WorkspaceId, "missing", CancellationToken.None);
        var err = AssertError(miss, 404);
        Assert.AreEqual("task.not_found", err.Code);
        Assert.AreEqual("trace-abc", err.TraceId);
        Assert.IsFalse(string.IsNullOrEmpty(err.Message));
    }

    // ── 12. B1：PATCH 可选 status 字段（显式状态迁移）──

    [TestMethod]
    public async Task Patch_BacklogToReady_ReturnsReadyTodoAndReadyEvent()
    {
        var task = await CreateTaskAsync();
        var controller = CreateController();

        var result = await controller.Patch(WorkspaceId, task.TaskId, new PatchTaskDto
        {
            ExpectedVersion = 1,
            Status = "Ready",
        }, CancellationToken.None);

        var dto = AssertOkDto(result);
        Assert.AreEqual("Ready", dto.Status);
        Assert.AreEqual("Todo", dto.BoardColumn);
        Assert.AreEqual(2, dto.Version);

        var events = await GetEventsAsync(task.TaskId);
        Assert.AreEqual(TaskEventType.TaskReady, events[^1].EventType);
        Assert.AreEqual(2, events[^1].Sequence);
    }

    [TestMethod]
    public async Task Patch_BacklogToInProgress_Returns422InvalidTransition()
    {
        var task = await CreateTaskAsync();
        var controller = CreateController();

        var result = await controller.Patch(WorkspaceId, task.TaskId, new PatchTaskDto
        {
            ExpectedVersion = 1,
            Status = "InProgress",
        }, CancellationToken.None);

        var err = AssertError(result, 422);
        Assert.AreEqual("task.invalid_transition", err.Code);
    }

    [TestMethod]
    public async Task Patch_FailedToReady_Returns422InvalidTransition()
    {
        var task = await CreateTaskAsync();
        await SetStatusAsync(task.TaskId, WorkspaceTaskStatus.Failed);
        var controller = CreateController();

        var result = await controller.Patch(WorkspaceId, task.TaskId, new PatchTaskDto
        {
            ExpectedVersion = 1,
            Status = "Ready",
        }, CancellationToken.None);

        var err = AssertError(result, 422);
        Assert.AreEqual("task.invalid_transition", err.Code);
    }

    [TestMethod]
    public async Task Patch_CompletedToArchived_ReturnsArchived()
    {
        var task = await CreateTaskAsync();
        await SetStatusAsync(task.TaskId, WorkspaceTaskStatus.Completed);
        var controller = CreateController();

        var result = await controller.Patch(WorkspaceId, task.TaskId, new PatchTaskDto
        {
            ExpectedVersion = 1,
            Status = "Archived",
        }, CancellationToken.None);

        var dto = AssertOkDto(result);
        Assert.AreEqual("Archived", dto.Status);
        Assert.IsNotNull(dto.ArchivedAtUtc);
    }

    [TestMethod]
    public async Task Patch_WithoutStatus_KeepsStatus()
    {
        var task = await CreateTaskAsync();
        var controller = CreateController();

        var result = await controller.Patch(WorkspaceId, task.TaskId, new PatchTaskDto
        {
            ExpectedVersion = 1,
            Title = "renamed",
        }, CancellationToken.None);

        var dto = AssertOkDto(result);
        Assert.AreEqual("Backlog", dto.Status);
        Assert.AreEqual("renamed", dto.Title);
        Assert.AreEqual(2, dto.Version);
    }

    // ── 13. B2：boardColumn 过滤 ──

    [TestMethod]
    public async Task List_BoardColumnTodo_ReturnsTodoStatusTasks()
    {
        var ready = await CreateTaskAsync("ready");
        await SetStatusAsync(ready.TaskId, WorkspaceTaskStatus.Ready);
        var inProgress = await CreateTaskAsync("in-progress");
        await SetStatusAsync(inProgress.TaskId, WorkspaceTaskStatus.InProgress);
        var controller = CreateController();

        var result = await controller.List(WorkspaceId, null, "Todo", null, null, 100, null, CancellationToken.None);

        var page = AssertPage(result);
        CollectionAssert.AreEqual(new[] { ready.TaskId }, page.Items.Select(i => i.TaskId).ToArray());
    }

    [TestMethod]
    public async Task List_BoardColumnDoneAndStatusCompleted_Intersects()
    {
        var completed = await CreateTaskAsync("completed");
        await SetStatusAsync(completed.TaskId, WorkspaceTaskStatus.Completed);
        var inProgress = await CreateTaskAsync("in-progress");
        await SetStatusAsync(inProgress.TaskId, WorkspaceTaskStatus.InProgress);
        var controller = CreateController();

        var result = await controller.List(WorkspaceId, "Completed", "Done", null, null, 100, null, CancellationToken.None);

        var page = AssertPage(result);
        CollectionAssert.AreEqual(new[] { completed.TaskId }, page.Items.Select(i => i.TaskId).ToArray());
    }

    [TestMethod]
    public async Task List_UnknownBoardColumn_Returns422()
    {
        var controller = CreateController();

        var result = await controller.List(WorkspaceId, null, "Bogus", null, null, 100, null, CancellationToken.None);

        var obj = Assert.IsInstanceOfType<ObjectResult>(result.Result);
        Assert.AreEqual(422, obj.StatusCode);
        Assert.AreEqual("task.invalid_transition", Assert.IsInstanceOfType<TaskErrorResponse>(obj.Value).Code);
    }

    // ── 14. B2：Watch SSE（游标 + Last-Event-ID 续传）──

    [TestMethod]
    public async Task Watch_ResumesFromCursor_EmitsSnapshotAndEventsAfterCursor()
    {
        var task = await CreateTaskAsync();
        await _store.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = task.TaskId,
            ExpectedVersion = 1,
            Title = "v2",
        });

        var createdEventId = await GetFirstEventIdAsync(task.TaskId);

        var controller = CreateController();
        using var body = new MemoryStream();
        controller.ControllerContext.HttpContext.Response.Body = body;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await controller.Watch(WorkspaceId, afterId: createdEventId, cts.Token);

        body.Position = 0;
        var text = await new StreamReader(body).ReadToEndAsync();

        StringAssert.Contains(text, "task.snapshot");
        StringAssert.Contains(text, "task.updated");
        Assert.IsFalse(text.Contains("task.created"), "游标之后不应重发 task.created。");
    }

    // ── 15. B1：ToDto allowedTransitions（TB-11）────────────────

    [TestMethod]
    public async Task ToDto_AllowedTransitions_DerivedFromStateMachine()
    {
        var controller = CreateController();

        var backlog = await CreateTaskAsync("backlog");
        var backlogDto = AssertOkDto(await controller.Get(WorkspaceId, backlog.TaskId, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { "Ready" }, backlogDto.AllowedTransitions.ToArray());

        var failed = await CreateTaskAsync("failed");
        await SetStatusAsync(failed.TaskId, WorkspaceTaskStatus.Failed);
        var failedDto = AssertOkDto(await controller.Get(WorkspaceId, failed.TaskId, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { "Archived" }, failedDto.AllowedTransitions.ToArray());

        var archived = await CreateTaskAsync("archived");
        await SetStatusAsync(archived.TaskId, WorkspaceTaskStatus.Archived);
        var archivedDto = AssertOkDto(await controller.Get(WorkspaceId, archived.TaskId, CancellationToken.None));
        Assert.AreEqual(0, archivedDto.AllowedTransitions.Count);
    }

    // ── 16. TB-11：评论端点（GET/POST {taskId}/comments）────────

    [TestMethod]
    public async Task Comments_AddAndList_ViaController()
    {
        var task = await CreateTaskAsync();
        var controller = CreateController();

        var addResult = await controller.AddComment(WorkspaceId, task.TaskId, new CreateTaskCommentDto
        {
            Content = "hello",
            AuthorKind = "agent",
        }, CancellationToken.None);
        var added = Assert.IsInstanceOfType<OkObjectResult>(addResult.Result);
        var addedDto = Assert.IsInstanceOfType<TaskCommentDto>(added.Value);
        Assert.AreEqual("agent", addedDto.AuthorKind);
        Assert.AreEqual("hello", addedDto.Content);
        Assert.AreEqual(task.TaskId, addedDto.TaskId);
        Assert.AreEqual(WorkspaceId, addedDto.WorkspaceId);

        var listResult = await controller.ListComments(WorkspaceId, task.TaskId, CancellationToken.None);
        var ok = Assert.IsInstanceOfType<OkObjectResult>(listResult.Result);
        var items = Assert.IsInstanceOfType<IReadOnlyList<TaskCommentDto>>(ok.Value);
        Assert.AreEqual(1, items.Count);
        Assert.AreEqual("hello", items[0].Content);

        // 不存在的任务 → 404 task.not_found
        var miss = await controller.AddComment(WorkspaceId, "missing", new CreateTaskCommentDto { Content = "x" }, CancellationToken.None);
        Assert.AreEqual(404, Assert.IsInstanceOfType<ObjectResult>(miss.Result).StatusCode);
    }

    // ── helpers ─────────────────────────────────────────────

    private TaskController CreateController(string traceId = "trace-test")
        => new(_store, _service, _dbFactory)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { TraceIdentifier = traceId },
            },
        };

    private async Task<WorkspaceTask> CreateTaskAsync(string title = "Task")
        => await _store.CreateTaskAsync(new CreateTaskRequest
        {
            WorkspaceId = WorkspaceId,
            Title = title,
        });

    private async Task PatchToVersion2Async(string taskId)
    {
        await _store.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = taskId,
            ExpectedVersion = 1,
            Title = "v2",
        });
    }

    private async Task SetStatusAsync(string taskId, WorkspaceTaskStatus status)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.WorkspaceTasks.SingleAsync(t => t.TaskId == taskId);
        entity.Status = status;
        await db.SaveChangesAsync();
    }

    private async Task SeedActiveAssignmentAsync(string taskId, string agentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var attempt = new TaskAssignmentAttemptEntity
        {
            AttemptId = "attempt-seed",
            TaskId = taskId,
            WorkspaceId = WorkspaceId,
            AgentId = agentId,
            AttemptNumber = 1,
            Status = AssignmentAttemptStatus.InProgress,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ActiveAtUtc = DateTimeOffset.UtcNow,
            ReleasedAtUtc = null,
        };
        db.TaskAssignmentAttempts.Add(attempt);
        var entity = await db.WorkspaceTasks.SingleAsync(t => t.TaskId == taskId);
        entity.Status = WorkspaceTaskStatus.InProgress;
        entity.ActiveAssignmentId = attempt.AttemptId;
        await db.SaveChangesAsync();
    }

    private async Task<List<TaskEventEntity>> GetEventsAsync(string taskId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.TaskEvents
            .Where(e => e.TaskId == taskId)
            .OrderBy(e => e.Sequence)
            .ToListAsync();
    }

    private async Task<List<TaskAssignmentAttemptEntity>> GetAttemptsAsync(string taskId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.TaskAssignmentAttempts
            .Where(a => a.TaskId == taskId)
            .OrderBy(a => a.AttemptNumber)
            .ToListAsync();
    }

    private static TaskDto AssertOkDto(ActionResult<TaskDto> result)
    {
        var ok = Assert.IsInstanceOfType<OkObjectResult>(result.Result);
        return Assert.IsInstanceOfType<TaskDto>(ok.Value);
    }

    private static TaskPageDto AssertPage(ActionResult<TaskPageDto> result)
    {
        var ok = Assert.IsInstanceOfType<OkObjectResult>(result.Result);
        return Assert.IsInstanceOfType<TaskPageDto>(ok.Value);
    }

    private async Task<long> GetFirstEventIdAsync(string taskId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.TaskEvents
            .Where(e => e.TaskId == taskId)
            .OrderBy(e => e.Id)
            .Select(e => e.Id)
            .FirstAsync();
    }

    private static TaskErrorResponse AssertError(ActionResult<TaskDto> result, int expectedStatus)
    {
        var obj = Assert.IsInstanceOfType<ObjectResult>(result.Result);
        Assert.AreEqual(expectedStatus, obj.StatusCode);
        return Assert.IsInstanceOfType<TaskErrorResponse>(obj.Value);
    }
}
