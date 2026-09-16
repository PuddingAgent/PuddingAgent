using Microsoft.EntityFrameworkCore;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Todo;

/// <summary>
/// 设计 2026-09-16 §3/§4（TD-1；TD-1b 对齐用户裁决）：Todo 拆解表的存储服务（<see cref="ITodoStore"/> 的 Platform 实现）。
/// <para>
/// 与 <see cref="Tasks.TaskAgentCommandService"/> 同模式：Singleton 托管（工具注册表按 Singleton 消费），
/// 每次调用创建并释放独立 DbContext；单次 SaveChanges 原子提交（不变量同 TB-03 #6）。
/// TD-1b 约束分级（设计 §3 约束行 / §8 验收 3/7/8）：
/// 硬拒绝——① 列表内 slug 唯一；② scope_kind/scope_id 必填且合法；③ expected_revision CAS 不符 ⇒ 明确报错
/// （不静默覆盖）；④ 读写均按 agent_id 过滤（跨 Agent 即使 scope_id 完全相同也互不可见、互不可写）。
/// 软警告（接受写入，返回 <c>Warnings</c>）——&gt;20 项 / &gt;1 in_progress / blocked 无 reason。
/// 幂等：全量替换按 slug 对齐 ⇒ 同内容重复写入不产生重复项。
/// 归档语义已移除（TD-1b）：无归档入口，不写 archived_at_utc。
/// </para>
/// </summary>
public sealed class TodoStore(
    IDbContextFactory<PlatformDbContext> dbFactory) : ITodoStore
{
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory = dbFactory;

    /// <inheritdoc />
    public async Task<TodoWriteResult> WriteAsync(TodoWriteRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAgent(request.AgentId);
        ValidateScope(request.ScopeKind, request.ScopeId);
        var warnings = ValidateItems(request.Items);

        var now = DateTimeOffset.UtcNow;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var list = await db.TodoLists
            .SingleOrDefaultAsync(
                l => l.AgentId == request.AgentId
                     && l.ScopeKind == request.ScopeKind
                     && l.ScopeId == request.ScopeId,
                ct);

        // CAS：0 = 首写（列表必须不存在）；否则必须等于当前 revision，不符即明确报错（不静默覆盖）。
        if (list is null)
        {
            if (request.ExpectedRevision != 0)
            {
                throw new TodoStoreException(
                    TodoErrorCode.TodoVersionConflict,
                    $"Todo list for scope '{request.ScopeKind}/{request.ScopeId}' does not exist; expected_revision must be 0 for the first write (got {request.ExpectedRevision}).",
                    request.ScopeKind,
                    request.ScopeId,
                    currentRevision: null);
            }

            list = new TodoListEntity
            {
                ListId = NewId("tdl"),
                AgentId = request.AgentId,
                ScopeKind = request.ScopeKind,
                ScopeId = request.ScopeId,
                Title = request.Title,
                Revision = 0,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.TodoLists.Add(list);
        }
        else if (list.Revision != request.ExpectedRevision)
        {
            throw new TodoStoreException(
                TodoErrorCode.TodoVersionConflict,
                $"Todo list '{request.ScopeKind}/{request.ScopeId}' revision conflict: expected {request.ExpectedRevision}, actual {list.Revision}. Re-read via todo_read and retry; the write was NOT applied.",
                request.ScopeKind,
                request.ScopeId,
                list.Revision);
        }

        if (request.Title is not null)
        {
            list.Title = request.Title;
        }

        var existing = await db.TodoItems
            .Where(i => i.ListId == list.ListId)
            .ToListAsync(ct);
        var existingBySlug = existing.ToDictionary(i => i.Slug, StringComparer.Ordinal);

        var diff = new TodoDiffAccumulator();
        foreach (var (input, index) in request.Items.Select((v, i) => (v, i)))
        {
            var orderIndex = input.OrderIndex ?? index;
            if (existingBySlug.TryGetValue(input.Slug, out var item))
            {
                var becameCompleted = item.Status != TodoWireMaps.StatusCompleted
                    && input.Status == TodoWireMaps.StatusCompleted;
                var becameBlocked = item.Status != TodoWireMaps.StatusBlocked
                    && input.Status == TodoWireMaps.StatusBlocked;

                item.Title = input.Title;
                item.Status = input.Status;
                item.Note = input.Note;
                item.EvidenceRef = input.EvidenceRef;
                item.BlockedReason = input.BlockedReason;
                item.OrderIndex = orderIndex;
                ApplyStatusTimestamps(item, input.Status, becameCompleted, now);
                if (becameCompleted)
                {
                    diff.Completed.Add(input.Slug);
                }

                if (becameBlocked)
                {
                    diff.Blocked.Add(input.Slug);
                }

                existingBySlug.Remove(input.Slug);
            }
            else
            {
                item = new TodoItemEntity
                {
                    ItemId = NewId("tdi"),
                    ListId = list.ListId,
                    Slug = input.Slug,
                    Title = input.Title,
                    Status = input.Status,
                    Note = input.Note,
                    EvidenceRef = input.EvidenceRef,
                    BlockedReason = input.BlockedReason,
                    OrderIndex = orderIndex,
                };
                ApplyStatusTimestamps(
                    item,
                    input.Status,
                    becameCompleted: input.Status == TodoWireMaps.StatusCompleted,
                    now);
                db.TodoItems.Add(item);
                diff.Added.Add(input.Slug);
            }
        }

        // 缺席即移除（全量替换语义：不累积僵尸项）。
        foreach (var stale in existingBySlug.Values)
        {
            db.TodoItems.Remove(stale);
            diff.Removed.Add(stale.Slug);
        }

        list.Revision += 1;
        list.UpdatedAtUtc = now;

        // 单次 SaveChanges 原子提交（与 TaskAgentCommandService 同不变量）。
        await db.SaveChangesAsync(ct);

        var items = await db.TodoItems
            .Where(i => i.ListId == list.ListId)
            .OrderBy(i => i.OrderIndex)
            .ThenBy(i => i.Slug)
            .ToListAsync(ct);

        return new TodoWriteResult
        {
            ListId = list.ListId,
            ScopeKind = list.ScopeKind,
            ScopeId = list.ScopeId,
            Revision = list.Revision,
            Diff = diff.Build(),
            Summary = BuildSummary(items),
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    /// <inheritdoc />
    public async Task<TodoReadResult?> ReadAsync(TodoReadQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateAgent(query.AgentId);
        ValidateScope(query.ScopeKind, query.ScopeId);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var list = await db.TodoLists
            .AsNoTracking()
            .SingleOrDefaultAsync(
                l => l.AgentId == query.AgentId
                     && l.ScopeKind == query.ScopeKind
                     && l.ScopeId == query.ScopeId,
                ct);
        if (list is null)
        {
            return null;
        }

        var items = await db.TodoItems
            .AsNoTracking()
            .Where(i => i.ListId == list.ListId)
            .OrderBy(i => i.OrderIndex)
            .ThenBy(i => i.Slug)
            .ToListAsync(ct);

        return new TodoReadResult
        {
            ListId = list.ListId,
            ScopeKind = list.ScopeKind,
            ScopeId = list.ScopeId,
            Title = list.Title,
            Revision = list.Revision,
            Items = items.Select(ToView).ToList(),
            Summary = BuildSummary(items),
        };
    }

    /// <inheritdoc />
    public async Task<TodoCheckResult> CheckAsync(TodoCheckRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAgent(request.AgentId);
        ValidateScope(request.ScopeKind, request.ScopeId);
        if (!TodoWireMaps.IsValidStatus(request.Status))
        {
            throw new TodoStoreException(
                TodoErrorCode.TodoInvalidStatus,
                $"Unknown status '{request.Status}'. Use pending/in_progress/completed/blocked.",
                request.ScopeKind,
                request.ScopeId);
        }

        // 软约束（TD-1b）收集器：>1 in_progress / blocked 无 reason ⇒ 接受写入 + warning（不再硬拒）。
        var warnings = new List<string>();

        var now = DateTimeOffset.UtcNow;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var list = await db.TodoLists
            .SingleOrDefaultAsync(
                l => l.AgentId == request.AgentId
                     && l.ScopeKind == request.ScopeKind
                     && l.ScopeId == request.ScopeId,
                ct);
        if (list is null)
        {
            throw new TodoStoreException(
                TodoErrorCode.TodoListNotFound,
                $"Todo list for scope '{request.ScopeKind}/{request.ScopeId}' does not exist; write it first via todo_write.",
                request.ScopeKind,
                request.ScopeId);
        }

        if (list.Revision != request.ExpectedRevision)
        {
            throw new TodoStoreException(
                TodoErrorCode.TodoVersionConflict,
                $"Todo list '{request.ScopeKind}/{request.ScopeId}' revision conflict: expected {request.ExpectedRevision}, actual {list.Revision}. Re-read via todo_read and retry; the check was NOT applied.",
                request.ScopeKind,
                request.ScopeId,
                list.Revision);
        }

        var item = await db.TodoItems
            .SingleOrDefaultAsync(i => i.ListId == list.ListId && i.Slug == request.Slug, ct);
        if (item is null)
        {
            throw new TodoStoreException(
                TodoErrorCode.TodoItemNotFound,
                $"Todo item '{request.Slug}' not found in list '{request.ScopeKind}/{request.ScopeId}'.",
                request.ScopeKind,
                request.ScopeId,
                list.Revision);
        }

        // 软约束（TD-1b）：另一个 in_progress 已存在 ⇒ 接受勾选 + warning（不再硬拒）。
        if (request.Status == TodoWireMaps.StatusInProgress
            && item.Status != TodoWireMaps.StatusInProgress)
        {
            var current = await db.TodoItems
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    i => i.ListId == list.ListId
                         && i.Status == TodoWireMaps.StatusInProgress
                         && i.Slug != request.Slug,
                    ct);
            if (current is not null)
            {
                warnings.Add(
                    $"{TodoWireMaps.WarnMultipleInProgress}: item '{current.Slug}' is already in_progress; " +
                    $"'{request.Slug}' is now in_progress too (recommended max 1 per list).");
            }
        }

        var wasCompleted = item.Status == TodoWireMaps.StatusCompleted;
        // 生效 reason = 请求提供值 ?? 项上已有值；blocked 且两者皆空 ⇒ 软警告（接受写入）。
        var effectiveBlockedReason = request.BlockedReason ?? item.BlockedReason;
        item.Status = request.Status;
        if (request.BlockedReason is not null)
        {
            item.BlockedReason = request.BlockedReason;
        }

        if (request.Status == TodoWireMaps.StatusBlocked
            && string.IsNullOrWhiteSpace(effectiveBlockedReason))
        {
            warnings.Add(
                $"{TodoWireMaps.WarnBlockedReasonRequired}: item '{request.Slug}' is blocked without a blocked_reason (soft constraint, write accepted).");
        }

        if (request.EvidenceRef is not null)
        {
            item.EvidenceRef = request.EvidenceRef;
        }

        if (request.Note is not null)
        {
            item.Note = request.Note;
        }

        ApplyStatusTimestamps(
            item,
            request.Status,
            becameCompleted: request.Status == TodoWireMaps.StatusCompleted && !wasCompleted,
            now);

        list.Revision += 1;
        list.UpdatedAtUtc = now;

        await db.SaveChangesAsync(ct);

        var items = await db.TodoItems
            .AsNoTracking()
            .Where(i => i.ListId == list.ListId)
            .OrderBy(i => i.OrderIndex)
            .ThenBy(i => i.Slug)
            .ToListAsync(ct);

        return new TodoCheckResult
        {
            ListId = list.ListId,
            Revision = list.Revision,
            Item = ToView(item),
            Summary = BuildSummary(items),
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    // ── 校验与映射 ──────────────────────────────────────────

    private static void ValidateAgent(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new TodoStoreException(
                TodoErrorCode.TodoInvalidAgent,
                "agent_id is required: the calling agent identity must be provided by the tool context (never synthesized).",
                scopeKind: null,
                scopeId: null);
        }
    }

    private static void ValidateScope(string scopeKind, string scopeId)
    {
        if (!TodoWireMaps.IsValidScopeKind(scopeKind))
        {
            throw new TodoStoreException(
                TodoErrorCode.TodoInvalidScopeKind,
                $"Unknown scope_kind '{scopeKind}'. Use goal/task/session.");
        }

        if (string.IsNullOrWhiteSpace(scopeId))
        {
            throw new TodoStoreException(
                TodoErrorCode.TodoInvalidScopeKind,
                "scope_id must be a non-empty identifier of the goal run / task / session.");
        }
    }

    /// <summary>
    /// 校验写入项：硬拒绝（slug 空/重复、title 空/超长、status 非法）抛 <see cref="TodoStoreException"/>；
    /// 软约束（TD-1b：&gt;20 项、&gt;1 in_progress、blocked 无 reason）收集为 warnings 返回（接受写入）。
    /// </summary>
    private static List<string> ValidateItems(IReadOnlyList<TodoItemInput> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var warnings = new List<string>();
        if (items.Count > TodoWireMaps.MaxItemsPerList)
        {
            warnings.Add(
                $"{TodoWireMaps.WarnTooManyItems}: a todo list should contain at most {TodoWireMaps.MaxItemsPerList} items " +
                $"(got {items.Count}); consider splitting the breakdown or removing finished items.");
        }

        var inProgressCount = 0;
        var seenSlugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (string.IsNullOrWhiteSpace(item.Slug))
            {
                throw new TodoStoreException(
                    TodoErrorCode.TodoInvalidItem,
                    "item slug must be a non-empty stable identifier.");
            }

            if (!seenSlugs.Add(item.Slug))
            {
                throw new TodoStoreException(
                    TodoErrorCode.TodoDuplicateSlug,
                    $"Duplicate slug '{item.Slug}' in the write payload; slugs must be unique within a list.");
            }

            if (string.IsNullOrWhiteSpace(item.Title))
            {
                throw new TodoStoreException(
                    TodoErrorCode.TodoInvalidItem,
                    $"Item '{item.Slug}' title must be non-empty.");
            }

            if (item.Title.Length > TodoWireMaps.MaxTitleLength)
            {
                throw new TodoStoreException(
                    TodoErrorCode.TodoInvalidItem,
                    $"Item '{item.Slug}' title exceeds {TodoWireMaps.MaxTitleLength} characters.");
            }

            if (!TodoWireMaps.IsValidStatus(item.Status))
            {
                throw new TodoStoreException(
                    TodoErrorCode.TodoInvalidStatus,
                    $"Item '{item.Slug}' has unknown status '{item.Status}'. Use pending/in_progress/completed/blocked.");
            }

            if (item.Status == TodoWireMaps.StatusBlocked
                && string.IsNullOrWhiteSpace(item.BlockedReason))
            {
                warnings.Add(
                    $"{TodoWireMaps.WarnBlockedReasonRequired}: item '{item.Slug}' is blocked without a blocked_reason (soft constraint, write accepted).");
            }

            if (item.Status == TodoWireMaps.StatusInProgress)
            {
                inProgressCount++;
            }
        }

        if (inProgressCount > 1)
        {
            warnings.Add(
                $"{TodoWireMaps.WarnMultipleInProgress}: at most 1 in_progress item is recommended per list " +
                $"(payload has {inProgressCount}).");
        }

        return warnings;
    }

    private static void ApplyStatusTimestamps(
        TodoItemEntity item,
        string status,
        bool becameCompleted,
        DateTimeOffset now)
    {
        switch (status)
        {
            case TodoWireMaps.StatusInProgress:
                item.StartedAtUtc ??= now;
                item.CompletedAtUtc = null;
                break;
            case TodoWireMaps.StatusCompleted:
                // 已是 completed 则保留原完成时间；重新完成才刷新。
                if (becameCompleted)
                {
                    item.CompletedAtUtc = now;
                }

                break;
            default:
                // pending / blocked：离开完成态即清空完成时间。
                item.CompletedAtUtc = null;
                break;
        }
    }

    private static TodoItemView ToView(TodoItemEntity item) => new()
    {
        Slug = item.Slug,
        Title = item.Title,
        Status = item.Status,
        Note = item.Note,
        EvidenceRef = item.EvidenceRef,
        BlockedReason = item.BlockedReason,
        OrderIndex = item.OrderIndex,
        StartedAtUtc = item.StartedAtUtc,
        CompletedAtUtc = item.CompletedAtUtc,
    };

    private static TodoSummary BuildSummary(IReadOnlyList<TodoItemEntity> items)
    {
        var pending = 0;
        var inProgress = 0;
        var completed = 0;
        var blocked = 0;
        string? currentSlug = null;
        var blockedSlugs = new List<string>();
        foreach (var item in items)
        {
            switch (item.Status)
            {
                case TodoWireMaps.StatusPending:
                    pending++;
                    break;
                case TodoWireMaps.StatusInProgress:
                    inProgress++;
                    currentSlug = item.Slug;
                    break;
                case TodoWireMaps.StatusCompleted:
                    completed++;
                    break;
                case TodoWireMaps.StatusBlocked:
                    blocked++;
                    blockedSlugs.Add(item.Slug);
                    break;
            }
        }

        return new TodoSummary
        {
            Total = items.Count,
            Pending = pending,
            InProgress = inProgress,
            Completed = completed,
            Blocked = blocked,
            CurrentSlug = currentSlug,
            BlockedSlugs = blockedSlugs,
        };
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>diff 收集器（设计 §4：added/completed/blocked/removed）。</summary>
    private sealed class TodoDiffAccumulator
    {
        public List<string> Added { get; } = [];
        public List<string> Completed { get; } = [];
        public List<string> Blocked { get; } = [];
        public List<string> Removed { get; } = [];

        public TodoDiff Build() => new()
        {
            Added = Added,
            Completed = Completed,
            Blocked = Blocked,
            Removed = Removed,
        };
    }
}
