using Microsoft.EntityFrameworkCore;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Security;

namespace PuddingPlatform.Services.ExternalApi;

/// <summary>
/// ADR-075 §8.7: 追加式评价 Store。评价行 + task.evaluated 事件同一事务提交；
/// 评价不修改 Task status/version。更正走 supersedes_evaluation_id，不 UPDATE/DELETE 历史。
/// </summary>
public sealed class TaskEvaluationStore(IDbContextFactory<PlatformDbContext> dbFactory)
{
    public async Task<Result<TaskEvaluation, TaskEvaluationError>> AppendAsync(
        AppendTaskEvaluationRequest request,
        CancellationToken ct = default)
    {
        if (request.Score is < 1 or > 5)
            return Result<TaskEvaluation, TaskEvaluationError>.Failure(TaskEvaluationError.InvalidScore);

        var comment = request.Comment?.Trim() ?? string.Empty;
        if (comment.Length is < 1 or > 4000)
            return Result<TaskEvaluation, TaskEvaluationError>.Failure(TaskEvaluationError.InvalidComment);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var task = await db.WorkspaceTasks
            .FirstOrDefaultAsync(t => t.WorkspaceId == request.WorkspaceId && t.TaskId == request.TaskId, ct);
        if (task is null)
            return Result<TaskEvaluation, TaskEvaluationError>.Failure(TaskEvaluationError.TaskNotFound);

        if (task.Status == WorkspaceTaskStatus.Archived)
            return Result<TaskEvaluation, TaskEvaluationError>.Failure(TaskEvaluationError.TaskArchived);

        if (task.Version != request.TaskVersionObserved)
            return Result<TaskEvaluation, TaskEvaluationError>.Failure(TaskEvaluationError.VersionMismatch);

        if (!string.IsNullOrWhiteSpace(request.SupersedesEvaluationId))
        {
            var superseded = await db.TaskEvaluations.AnyAsync(
                e => e.EvaluationId == request.SupersedesEvaluationId
                    && e.TaskId == request.TaskId
                    && e.EvaluatorId == request.EvaluatorId,
                ct);
            if (!superseded)
                return Result<TaskEvaluation, TaskEvaluationError>.Failure(TaskEvaluationError.InvalidSupersedes);
        }

        var evaluationId = $"tev_{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        // Sequence 为 long（可翻译）；?? 分支覆盖该 Task 尚无事件的情形。
        var lastSequence = await db.TaskEvents.AsNoTracking()
            .Where(e => e.TaskId == request.TaskId)
            .MaxAsync(e => (long?)e.Sequence, ct) ?? 0;
        var sequence = lastSequence + 1;

        var entity = new TaskEvaluationEntity
        {
            EvaluationId = evaluationId,
            TaskId = request.TaskId,
            WorkspaceId = request.WorkspaceId,
            Verdict = VerdictToString(request.Verdict),
            Score = request.Score,
            Comment = comment,
            TaskVersionObserved = request.TaskVersionObserved,
            SupersedesEvaluationId = string.IsNullOrWhiteSpace(request.SupersedesEvaluationId)
                ? null
                : request.SupersedesEvaluationId,
            EvaluatorType = request.EvaluatorType,
            EvaluatorId = request.EvaluatorId,
            EvaluatorDisplayName = request.EvaluatorDisplayName,
            CreatedAtUtc = now,
        };
        db.TaskEvaluations.Add(entity);

        db.TaskEvents.Add(new TaskEventEntity
        {
            EventId = Guid.NewGuid().ToString("N"),
            TaskId = request.TaskId,
            WorkspaceId = request.WorkspaceId,
            Sequence = sequence,
            EventType = TaskEventType.TaskEvaluated,
            Origin = TaskOrigin.ExternalApi,
            CreatedAtUtc = now,
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Result<TaskEvaluation, TaskEvaluationError>.Success(ToContract(entity));
    }

    public async Task<IReadOnlyList<TaskEvaluation>> ListAsync(
        string workspaceId,
        string taskId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // SQLite EF 不支持 DateTimeOffset ORDER BY：取回后内存排序。
        var rows = await db.TaskEvaluations.AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId && e.TaskId == taskId)
            .ToListAsync(ct);
        return rows
            .OrderBy(e => e.CreatedAtUtc)
            .Select(ToContract)
            .ToList();
    }

    public static string VerdictToString(TaskEvaluationVerdict verdict) => verdict switch
    {
        TaskEvaluationVerdict.Accepted => "accepted",
        TaskEvaluationVerdict.NeedsChanges => "needs_changes",
        TaskEvaluationVerdict.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "未知评价结论。"),
    };

    public static TaskEvaluationVerdict VerdictFromString(string? value) => value switch
    {
        "accepted" => TaskEvaluationVerdict.Accepted,
        "needs_changes" => TaskEvaluationVerdict.NeedsChanges,
        "rejected" => TaskEvaluationVerdict.Rejected,
        _ => throw new ArgumentException($"未知评价结论 wire: {value}", nameof(value)),
    };

    private static TaskEvaluation ToContract(TaskEvaluationEntity e) => new()
    {
        EvaluationId = e.EvaluationId,
        TaskId = e.TaskId,
        WorkspaceId = e.WorkspaceId,
        Verdict = VerdictFromString(e.Verdict),
        Score = e.Score,
        Comment = e.Comment,
        TaskVersionObserved = e.TaskVersionObserved,
        SupersedesEvaluationId = e.SupersedesEvaluationId,
        EvaluatorType = e.EvaluatorType,
        EvaluatorId = e.EvaluatorId,
        EvaluatorDisplayName = e.EvaluatorDisplayName,
        CreatedAtUtc = e.CreatedAtUtc,
    };
}
