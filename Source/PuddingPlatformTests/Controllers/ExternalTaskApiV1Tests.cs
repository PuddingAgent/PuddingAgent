using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Security;
using PuddingCode.Tasks;
using PuddingPlatform.Controllers.External.V1;
using PuddingPlatform.Services.ExternalApi;
using PuddingPlatform.Services.Security;
using PuddingPlatform.Services.Tasks;

using PuddingPlatformTests.Security;

namespace PuddingPlatformTests.Controllers;

/// <summary>
/// ADR-075 §15.2 External Task API v1 基本功能矩阵：actor/origin 注入、ETag/428/412、
/// 幂等 replay/409、评价不改状态、命令走状态机。Policy 矩阵在
/// ExternalAccessTokenAuthorizationHandlerTests 覆盖；本文件直测 Controller 协议适配层。
/// </summary>
[TestClass]
public sealed class ExternalTaskApiV1Tests
{
    private sealed class ApiHarness
    {
        public required ExternalAccessTokenTestHarness Db { get; init; }
        public required ExternalTaskController Controller { get; init; }
        public required string TokenId { get; init; }
        public required string ActorId { get; init; }
    }

    private static async Task<ApiHarness> CreateAsync(IReadOnlyList<string> scopes)
    {
        var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        await harness.SeedOwnerAsync("admin");
        var tokenService = harness.CreateService();
        var created = await tokenService.CreateAsync(new ExternalAccessTokenCreateCommand
        {
            Name = "external-test",
            Scopes = scopes,
            WorkspaceIds = ["default"],
            OwnerUserId = "admin",
        });
        Assert.IsTrue(created.IsOk, $"token create failed: {created.Error}");
        var tokenId = created.Value!.Item.TokenId;

        var taskStore = new SqliteWorkspaceTaskStore(harness.Factory);
        var controller = new ExternalTaskController(
            taskStore,
            new TaskCommandService(taskStore, harness.Factory),
            new TaskEvaluationStore(harness.Factory),
            new ExternalApiIdempotencyStore(harness.Factory),
            new ExternalTaskApiOptionsProvider(
                PuddingCode.Configuration.PuddingDataPaths.FromRoot(Path.GetTempPath()),
                NullLogger<ExternalTaskApiOptionsProvider>.Instance));

        var identity = new ClaimsIdentity(authenticationType: ExternalAccessTokenDefaults.Scheme);
        identity.AddClaim(new Claim(ExternalAccessTokenClaimNames.TokenId, tokenId));
        identity.AddClaim(new Claim(ClaimTypes.Name, "external-test"));
        foreach (var scope in scopes)
            identity.AddClaim(new Claim(ExternalAccessTokenClaimNames.Scope, scope));
        identity.AddClaim(new Claim(ExternalAccessTokenClaimNames.Workspace, "default"));

        var http = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        http.Request.Method = "POST";
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor(),
        };

        return new ApiHarness
        {
            Db = harness,
            Controller = controller,
            TokenId = tokenId,
            ActorId = $"{ExternalAccessTokenDefaults.ActorIdPrefix}{tokenId}",
        };
    }

    private static void SetPath(ApiHarness api, string path)
        => api.Controller.HttpContext.Request.Path = path;

    private static void SetIdempotencyKey(ApiHarness api, string key)
        => api.Controller.HttpContext.Request.Headers["Idempotency-Key"] = key;

    private static void SetIfMatch(ApiHarness api, int version)
        => api.Controller.HttpContext.Request.Headers.IfMatch = $"\"task-v{version}\"";

    private static async Task<ExternalTaskDto> CreateTaskAsync(
        ApiHarness api,
        string title,
        string key = "key-create")
    {
        SetPath(api, "/api/external/v1/workspaces/default/tasks");
        SetIdempotencyKey(api, key);
        var result = await api.Controller.Create("default", new ExternalCreateTaskRequest { Title = title },
            CancellationToken.None);
        var objectResult = (ObjectResult)result;
        Assert.AreEqual(201, objectResult.StatusCode, $"create failed: {objectResult.StatusCode}");
        return (ExternalTaskDto)objectResult.Value!;
    }

    private static T Body<T>(IActionResult result, int expectedStatus)
    {
        var objectResult = (ObjectResult)result;
        Assert.AreEqual(expectedStatus, objectResult.StatusCode,
            $"expected {expectedStatus}, got {objectResult.StatusCode}");
        return (T)objectResult.Value!;
    }

    [TestMethod]
    public async Task Create_InjectsActorAndOrigin_Returns201WithETagAndLocation()
    {
        var api = await CreateAsync(["tasks.read", "tasks.write"]);
        await using (api.Db)
        {
            var created = await CreateTaskAsync(api, "外部任务");

            Assert.AreEqual("external.api", created.Origin);
            Assert.AreEqual(api.ActorId, created.CreatedBy);
            Assert.AreEqual("Backlog", created.Status);
            Assert.AreEqual(1, created.Version);
            Assert.AreEqual("\"task-v1\"", api.Controller.Response.Headers.ETag.ToString());
            Assert.IsTrue(
                api.Controller.Response.Headers.Location.ToString().Contains(created.TaskId),
                "Location 指向 External V1 resource");

            SetPath(api, $"/api/external/v1/workspaces/default/tasks/{created.TaskId}");
            var task = Body<ExternalTaskDto>(
                await api.Controller.Get("default", created.TaskId, CancellationToken.None), 200);
            Assert.AreEqual(created.TaskId, task.TaskId);

            var listResult = await api.Controller.List("default", null, null, null, null, 100, null, CancellationToken.None);
            var list = Body<ExternalTaskPageDto>(listResult.Result!, 200);
            Assert.AreEqual(1, list.Items.Count);
        }
    }

    [TestMethod]
    public async Task Create_WithoutIdempotencyKey_Rejected400()
    {
        var api = await CreateAsync(["tasks.write"]);
        await using (api.Db)
        {
            SetPath(api, "/api/external/v1/workspaces/default/tasks");
            var result = await api.Controller.Create("default", new ExternalCreateTaskRequest { Title = "x" },
                CancellationToken.None);
            var error = Body<ExternalErrorResponse>(result, 400);
            Assert.AreEqual("external.invalid_request", error.Code);
        }
    }

    [TestMethod]
    public async Task Create_SameKeySameBody_Replays_SameKeyDifferentBody_409()
    {
        var api = await CreateAsync(["tasks.read", "tasks.write"]);
        await using (api.Db)
        {
            var first = await CreateTaskAsync(api, "重试任务");

            // 网络重试：同 key 同 body → replay 原资源，不重复创建。
            var replay = await CreateTaskAsync(api, "重试任务");
            Assert.AreEqual(first.TaskId, replay.TaskId);

            var listResult = await api.Controller.List("default", null, null, null, null, 100, null, CancellationToken.None);
            var list = Body<ExternalTaskPageDto>(listResult.Result!, 200);
            Assert.AreEqual(1, list.Items.Count, "幂等 replay 不得重复创建 Task");

            // 同 key 不同 body → 409。
            SetPath(api, "/api/external/v1/workspaces/default/tasks");
            SetIdempotencyKey(api, "key-create");
            var conflict = await api.Controller.Create("default",
                new ExternalCreateTaskRequest { Title = "另一个任务" }, CancellationToken.None);
            Assert.AreEqual(409, ((ObjectResult)conflict).StatusCode);
        }
    }

    [TestMethod]
    public async Task Patch_MissingIfMatch_428_StaleVersion_412WithSnapshot_Correct_200()
    {
        var api = await CreateAsync(["tasks.read", "tasks.write"]);
        await using (api.Db)
        {
            var created = await CreateTaskAsync(api, "原标题");
            SetPath(api, $"/api/external/v1/workspaces/default/tasks/{created.TaskId}");

            // 缺 If-Match → 428。
            var noMatch = await api.Controller.Patch("default", created.TaskId,
                new ExternalPatchTaskRequest { Title = "新标题" }, CancellationToken.None);
            Assert.AreEqual(428, ((ObjectResult)noMatch).StatusCode);

            // 旧版本 → 412 + 当前 ETag + currentTask 快照。
            SetIfMatch(api, 99);
            var staleResult = await api.Controller.Patch("default", created.TaskId,
                new ExternalPatchTaskRequest { Title = "新标题" }, CancellationToken.None);
            Assert.AreEqual(412, ((ObjectResult)staleResult).StatusCode);
            Assert.AreEqual("\"task-v1\"", api.Controller.Response.Headers.ETag.ToString());

            // 正确版本 → 200，版本递增。
            SetIfMatch(api, 1);
            var ok = Body<ExternalTaskDto>(await api.Controller.Patch("default", created.TaskId,
                new ExternalPatchTaskRequest { Title = "新标题" }, CancellationToken.None), 200);
            Assert.AreEqual("新标题", ok.Title);
            Assert.AreEqual(2, ok.Version);
            Assert.AreEqual(api.ActorId, ok.UpdatedBy);
        }
    }

    [TestMethod]
    public async Task Command_Whitelisted_GoesThroughStateMachine_UnknownCommand_400()
    {
        var api = await CreateAsync(["tasks.read", "tasks.command"]);
        await using (api.Db)
        {
            var created = await CreateTaskAsync(api, "命令任务");
            var commandPath = $"/api/external/v1/workspaces/default/tasks/{created.TaskId}/commands/cancel";
            SetPath(api, commandPath);

            // 未知命令 → 400。
            SetPath(api, $"/api/external/v1/workspaces/default/tasks/{created.TaskId}/commands/explode");
            var unknown = await api.Controller.ApplyCommand("default", created.TaskId, "explode",
                new ExternalCommandRequest(), CancellationToken.None);
            Assert.AreEqual(400, ((ObjectResult)unknown).StatusCode);

            // Backlog 无法直接 Cancel（状态机限制）：先把任务置为 Ready，再走命令。
            await using (var db = await api.Db.Factory.CreateDbContextAsync())
            {
                var seed = await db.WorkspaceTasks.SingleAsync(t => t.TaskId == created.TaskId);
                seed.Status = WorkspaceTaskStatus.Ready;
                await db.SaveChangesAsync();
            }

            // cancel 走状态机。
            SetPath(api, commandPath);
            SetIfMatch(api, 1);
            SetIdempotencyKey(api, "k-cmd-1");
            var cancelled = Body<ExternalTaskDto>(await api.Controller.ApplyCommand(
                "default", created.TaskId, "cancel",
                new ExternalCommandRequest { Reason = "不再需要" }, CancellationToken.None), 200);
            Assert.AreEqual("Cancelled", cancelled.Status);
            Assert.AreEqual("\"task-v2\"", api.Controller.Response.Headers.ETag.ToString());

            // 命令 replay：返回当前 Task，不重复执行。
            SetIfMatch(api, 1);
            SetIdempotencyKey(api, "k-cmd-1");
            var replay = Body<ExternalTaskDto>(await api.Controller.ApplyCommand(
                "default", created.TaskId, "cancel",
                new ExternalCommandRequest { Reason = "不再需要" }, CancellationToken.None), 200);
            Assert.AreEqual("Cancelled", replay.Status);
            Assert.AreEqual(2, replay.Version, "replay 不重复推进状态");
        }
    }

    [TestMethod]
    public async Task Comments_AppendWithActor_ReplayNoDuplicate()
    {
        var api = await CreateAsync(["tasks.read", "tasks.comment"]);
        await using (api.Db)
        {
            var created = await CreateTaskAsync(api, "评论宿主");
            var commentPath = $"/api/external/v1/workspaces/default/tasks/{created.TaskId}/comments";
            SetPath(api, commentPath);

            SetIdempotencyKey(api, "c1");
            var comment = Body<ExternalTaskCommentDto>(await api.Controller.AddComment(
                "default", created.TaskId,
                new ExternalCreateCommentRequest { Content = "外部进展：已核对数据" }, CancellationToken.None), 201);
            Assert.AreEqual("agent", comment.AuthorKind);
            Assert.AreEqual(api.ActorId, comment.AuthorId);

            // replay → 同一条评论。
            SetIdempotencyKey(api, "c1");
            var replayed = Body<ExternalTaskCommentDto>(await api.Controller.AddComment(
                "default", created.TaskId,
                new ExternalCreateCommentRequest { Content = "外部进展：已核对数据" }, CancellationToken.None), 201);
            Assert.AreEqual(comment.CommentId, replayed.CommentId);

            var list = Body<IReadOnlyList<ExternalTaskCommentDto>>(
                await api.Controller.ListComments("default", created.TaskId, CancellationToken.None), 200);
            Assert.AreEqual(1, list.Count);
        }
    }

    [TestMethod]
    public async Task Evaluations_AppendWithoutTaskMutation_VersionMismatch_422()
    {
        var api = await CreateAsync(["tasks.read", "tasks.evaluate"]);
        await using (api.Db)
        {
            var created = await CreateTaskAsync(api, "评价宿主");
            var evaluationPath = $"/api/external/v1/workspaces/default/tasks/{created.TaskId}/evaluations";
            SetPath(api, evaluationPath);

            // taskVersionObserved 错误 → 422。
            SetIdempotencyKey(api, "e1");
            var mismatch = await api.Controller.AddEvaluation("default", created.TaskId,
                new ExternalCreateEvaluationRequest
                {
                    Verdict = "accepted",
                    Score = 5,
                    Comment = "好",
                    TaskVersionObserved = 42,
                }, CancellationToken.None);
            Assert.AreEqual(422, ((ObjectResult)mismatch).StatusCode);

            // 正确版本 → 201；Task version/状态不变。
            SetIdempotencyKey(api, "e2");
            var evaluation = Body<ExternalTaskEvaluationDto>(await api.Controller.AddEvaluation(
                "default", created.TaskId,
                new ExternalCreateEvaluationRequest
                {
                    Verdict = "accepted",
                    Score = 5,
                    Comment = "验收证据完整",
                    TaskVersionObserved = 1,
                }, CancellationToken.None), 201);
            Assert.AreEqual("accepted", evaluation.Verdict);
            Assert.AreEqual(api.ActorId, evaluation.Evaluator.Id);
            Assert.AreEqual("external_access_token", evaluation.Evaluator.Type);

            SetPath(api, $"/api/external/v1/workspaces/default/tasks/{created.TaskId}");
            var task = Body<ExternalTaskDto>(
                await api.Controller.Get("default", created.TaskId, CancellationToken.None), 200);
            Assert.AreEqual(1, task.Version, "评价不得增加 Task aggregate version");
            Assert.AreEqual("Backlog", task.Status, "评价不得改变 Task 状态");
        }
    }

    [TestMethod]
    public async Task UnknownTask_Returns404()
    {
        var api = await CreateAsync(["tasks.read"]);
        await using (api.Db)
        {
            SetPath(api, "/api/external/v1/workspaces/default/tasks/no-such-task");
            var result = await api.Controller.Get("default", "no-such-task", CancellationToken.None);
            var error = Body<ExternalErrorResponse>(result, 404);
            Assert.AreEqual("task.not_found", error.Code);

            var comments = await api.Controller.ListComments("default", "no-such-task", CancellationToken.None);
            Assert.AreEqual(404, ((ObjectResult)comments).StatusCode);
        }
    }
}
