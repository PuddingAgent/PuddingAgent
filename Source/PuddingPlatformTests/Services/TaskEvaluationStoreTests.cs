using Microsoft.EntityFrameworkCore;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Services.ExternalApi;
using PuddingPlatform.Services.Tasks;

using PuddingPlatformTests.Security;

namespace PuddingPlatformTests.Services;

/// <summary>ADR-075 §8.7 评价合同：追加 + 同事务 task.evaluated 事件 + 不改 Task 状态/version。</summary>
[TestClass]
public sealed class TaskEvaluationStoreTests
{
    private static async Task<(ExternalAccessTokenTestHarness Harness, TaskEvaluationStore Evals, TaskCommandService Commands, string TaskId)> CreateWithTaskAsync()
    {
        var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        var taskStore = new SqliteWorkspaceTaskStore(harness.Factory);
        var commands = new TaskCommandService(taskStore, harness.Factory);
        var evals = new TaskEvaluationStore(harness.Factory);

        var task = await taskStore.CreateTaskAsync(new CreateTaskRequest
        {
            WorkspaceId = "default",
            Title = "被评价的任务",
            Origin = TaskOrigin.Manual,
        });
        return (harness, evals, commands, task.TaskId);
    }

    private static AppendTaskEvaluationRequest Evaluation(
        string taskId,
        int version,
        string? supersedes = null,
        string evaluatorId = "access-token:pat_x",
        TaskEvaluationVerdict verdict = TaskEvaluationVerdict.Accepted) => new()
    {
        WorkspaceId = "default",
        TaskId = taskId,
        Verdict = verdict,
        Score = 5,
        Comment = "验收通过",
        TaskVersionObserved = version,
        SupersedesEvaluationId = supersedes,
        EvaluatorType = "external_access_token",
        EvaluatorId = evaluatorId,
        EvaluatorDisplayName = "reviewer",
    };

    [TestMethod]
    public async Task Append_WritesEvaluationAndEvent_DoesNotChangeTaskVersionOrStatus()
    {
        var (harness, evals, _, taskId) = await CreateWithTaskAsync();
        await using (harness)
        {
            var result = await evals.AppendAsync(Evaluation(taskId, version: 1));
            Assert.IsTrue(result.IsOk, $"append failed: {result.Error}");

            // Task version/status 不变。
            await using var db = await harness.Factory.CreateDbContextAsync();
            var task = await db.WorkspaceTasks.AsNoTracking().SingleAsync(t => t.TaskId == taskId);
            Assert.AreEqual(1, task.Version);
            Assert.AreEqual(WorkspaceTaskStatus.Backlog, task.Status);

            // 同事务写入 task.evaluated 事件。
            var evt = await db.TaskEvents.AsNoTracking()
                .SingleAsync(e => e.TaskId == taskId && e.EventType == TaskEventType.TaskEvaluated);
            Assert.AreEqual(TaskOrigin.ExternalApi, evt.Origin);

            // 列表可见。
            var list = await evals.ListAsync("default", taskId);
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual(TaskEvaluationVerdict.Accepted, list[0].Verdict);
        }
    }

    [TestMethod]
    public async Task Append_VersionMismatch_Rejected()
    {
        var (harness, evals, _, taskId) = await CreateWithTaskAsync();
        await using (harness)
        {
            var result = await evals.AppendAsync(Evaluation(taskId, version: 99));
            Assert.AreEqual(TaskEvaluationError.VersionMismatch, result.Error);
            Assert.AreEqual(0, (await evals.ListAsync("default", taskId)).Count);
        }
    }

    [TestMethod]
    public async Task Append_ArchivedTask_Rejected()
    {
        var (harness, evals, _, taskId) = await CreateWithTaskAsync();
        await using (harness)
        {
            // 直接置为 Archived（评价只关心终态，不经命令链构造）。
            await using var db = await harness.Factory.CreateDbContextAsync();
            var task = await db.WorkspaceTasks.SingleAsync(x => x.TaskId == taskId);
            task.Status = WorkspaceTaskStatus.Archived;
            await db.SaveChangesAsync();

            var result = await evals.AppendAsync(Evaluation(taskId, version: 1));
            Assert.AreEqual(TaskEvaluationError.TaskArchived, result.Error);
        }
    }

    [TestMethod]
    public async Task Append_Supersedes_OnlySameTaskSameActor()
    {
        var (harness, evals, commands, taskId) = await CreateWithTaskAsync();
        await using (harness)
        {
            var first = await evals.AppendAsync(Evaluation(taskId, version: 1));
            Assert.IsTrue(first.IsOk);
            var firstId = first.Value!.EvaluationId;

            // 不同 actor 的 supersedes → 拒绝。
            var wrongActor = await evals.AppendAsync(Evaluation(taskId, version: 1, supersedes: firstId,
                evaluatorId: "access-token:pat_other"));
            Assert.AreEqual(TaskEvaluationError.InvalidSupersedes, wrongActor.Error);

            // 同 actor 同 task → 通过并保留历史（追加不覆盖）。
            var correction = await evals.AppendAsync(Evaluation(taskId, version: 1, supersedes: firstId,
                verdict: TaskEvaluationVerdict.NeedsChanges));
            Assert.IsTrue(correction.IsOk, $"correction failed: {correction.Error}");

            var list = await evals.ListAsync("default", taskId);
            Assert.AreEqual(2, list.Count, "评价只追加，历史不删除");
            Assert.AreEqual(firstId, list[1].SupersedesEvaluationId);
        }
    }

    [TestMethod]
    public async Task Append_UnknownTaskOrSupersedes_Rejected()
    {
        var (harness, evals, _, taskId) = await CreateWithTaskAsync();
        await using (harness)
        {
            var notFound = await evals.AppendAsync(Evaluation("no-such-task", version: 1));
            Assert.AreEqual(TaskEvaluationError.TaskNotFound, notFound.Error);

            var badSupersedes = await evals.AppendAsync(Evaluation(taskId, version: 1, supersedes: "tev_missing"));
            Assert.AreEqual(TaskEvaluationError.InvalidSupersedes, badSupersedes.Error);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(6)]
    public async Task Append_ScoreOutOfRange_Rejected(int score)
    {
        var (harness, evals, _, taskId) = await CreateWithTaskAsync();
        await using (harness)
        {
            var request = Evaluation(taskId, version: 1) with { Score = score };
            var result = await evals.AppendAsync(request);
            Assert.AreEqual(TaskEvaluationError.InvalidScore, result.Error);
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task Append_EmptyComment_Rejected(string comment)
    {
        var (harness, evals, _, taskId) = await CreateWithTaskAsync();
        await using (harness)
        {
            var request = Evaluation(taskId, version: 1) with { Comment = comment };
            var result = await evals.AppendAsync(request);
            Assert.AreEqual(TaskEvaluationError.InvalidComment, result.Error);
        }
    }
}
