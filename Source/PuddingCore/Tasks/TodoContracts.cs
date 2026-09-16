using System.Text.Json;

namespace PuddingCode.Tasks;

/// <summary>
/// Todo 拆解工具（设计 2026-09-16 §3/§4，TD-1）的跨层契约。
/// <para>
/// 与 <see cref="ITaskAgentCommandService"/> 同模式：接口定义在 PuddingCore，由 PuddingPlatform 的
/// <c>TodoStore</c> 实现；PuddingRuntime 的 todo_* 工具只透传/序列化 wire 值，不依赖 Platform 类型。
/// </para>
/// <para>
/// 语义边界（设计 §1）：TODO 是 Agent 的「拆解与自述层」（可临时、可勾选），
/// 与 task_nodes 的「调度与验收层」分离；默认不同步，promote 是唯一升级通道（TD-3，未在本批实现）。
/// </para>
/// </summary>
public interface ITodoStore
{
    /// <summary>全量替换某作用域的拆解表（expected_revision CAS + 幂等 by slug + 返回 diff）。</summary>
    Task<TodoWriteResult> WriteAsync(TodoWriteRequest request, CancellationToken ct = default);

    /// <summary>读取某作用域的拆解表 + 汇总；列表不存在返回 null（不视为错误）。</summary>
    Task<TodoReadResult?> ReadAsync(TodoReadQuery query, CancellationToken ct = default);

    /// <summary>勾选单项状态（CAS + 服务端约束：≤1 in_progress / blocked 必填 reason）。</summary>
    Task<TodoCheckResult> CheckAsync(TodoCheckRequest request, CancellationToken ct = default);

    /// <summary>归档列表（临时性收尾；TD-4 的 Goal 结束自动归档复用本方法）。</summary>
    Task<TodoArchiveResult> ArchiveAsync(TodoArchiveRequest request, CancellationToken ct = default);
}

// ── wire 常量与校验 ─────────────────────────────────────────

/// <summary>todo 域的 wire 字面量（scope_kind / status），Runtime 工具与 Platform store 共用。</summary>
public static class TodoWireMaps
{
    public const string ScopeGoal = "goal";
    public const string ScopeTask = "task";
    public const string ScopeSession = "session";

    public const string StatusPending = "pending";
    public const string StatusInProgress = "in_progress";
    public const string StatusCompleted = "completed";
    public const string StatusBlocked = "blocked";

    public static bool IsValidScopeKind(string scopeKind) =>
        scopeKind is ScopeGoal or ScopeTask or ScopeSession;

    public static bool IsValidStatus(string status) =>
        status is StatusPending or StatusInProgress or StatusCompleted or StatusBlocked;

    /// <summary>服务端强制约束（设计 §3 约束行）：单个列表最多项数。</summary>
    public const int MaxItemsPerList = 20;

    /// <summary>服务端强制约束（设计 §3）：item title 上限。</summary>
    public const int MaxTitleLength = 120;
}

// ── todo_write ──────────────────────────────────────────────

public sealed record TodoWriteRequest
{
    /// <summary>goal | task | session。</summary>
    public required string ScopeKind { get; init; }

    /// <summary>goalRunId | taskId | sessionId。</summary>
    public required string ScopeId { get; init; }

    /// <summary>可选列表标题（如「看板梳理拆解」）。</summary>
    public string? Title { get; init; }

    /// <summary>
    /// CAS 期望版本：0 表示列表尚不存在（首写）；否则必须等于服务端当前 revision，
    /// 不符返回 <see cref="TodoErrorCode.TodoVersionConflict"/>（不静默覆盖）。
    /// </summary>
    public required int ExpectedRevision { get; init; }

    /// <summary>全量目标状态（≤20 项；同 slug 覆盖，缺席即移除）。</summary>
    public required IReadOnlyList<TodoItemInput> Items { get; init; }
}

public sealed record TodoItemInput
{
    public required string Slug { get; init; }
    public required string Title { get; init; }

    /// <summary>wire：pending | in_progress | completed | blocked。</summary>
    public required string Status { get; init; }

    public string? Note { get; init; }

    /// <summary>可选：commit sha / 文件路径 / 报告 id。</summary>
    public string? EvidenceRef { get; init; }

    /// <summary>status=blocked 时必填。</summary>
    public string? BlockedReason { get; init; }

    /// <summary>展示顺序；缺省按数组位置。</summary>
    public int? OrderIndex { get; init; }
}

public sealed record TodoWriteResult
{
    public required string ListId { get; init; }
    public required string ScopeKind { get; init; }
    public required string ScopeId { get; init; }
    public required int Revision { get; init; }
    public required TodoDiff Diff { get; init; }
    public required TodoSummary Summary { get; init; }
}

/// <summary>全量替换产生的 diff（设计 §4：added/completed/blocked/removed，均按 slug）。</summary>
public sealed record TodoDiff
{
    public required IReadOnlyList<string> Added { get; init; }
    public required IReadOnlyList<string> Completed { get; init; }
    public required IReadOnlyList<string> Blocked { get; init; }
    public required IReadOnlyList<string> Removed { get; init; }
}

/// <summary>拆解进度汇总（面板与 Agent 同一视图，设计 §4 todo_read 行）。</summary>
public sealed record TodoSummary
{
    public required int Total { get; init; }
    public required int Pending { get; init; }
    public required int InProgress { get; init; }
    public required int Completed { get; init; }
    public required int Blocked { get; init; }

    /// <summary>当前唯一 in_progress 项的 slug（无则为 null）。</summary>
    public string? CurrentSlug { get; init; }

    /// <summary>受阻项 slug 列表（面板置顶展开用）。</summary>
    public required IReadOnlyList<string> BlockedSlugs { get; init; }
}

// ── todo_read ───────────────────────────────────────────────

public sealed record TodoReadQuery
{
    public required string ScopeKind { get; init; }
    public required string ScopeId { get; init; }
}

public sealed record TodoReadResult
{
    public required string ListId { get; init; }
    public required string ScopeKind { get; init; }
    public required string ScopeId { get; init; }
    public string? Title { get; init; }
    public required int Revision { get; init; }
    public DateTimeOffset? ArchivedAtUtc { get; init; }
    public required IReadOnlyList<TodoItemView> Items { get; init; }
    public required TodoSummary Summary { get; init; }
}

public sealed record TodoItemView
{
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
    public string? Note { get; init; }
    public string? EvidenceRef { get; init; }
    public string? BlockedReason { get; init; }
    public required int OrderIndex { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
}

// ── todo_check ──────────────────────────────────────────────

public sealed record TodoCheckRequest
{
    public required string ScopeKind { get; init; }
    public required string ScopeId { get; init; }
    public required string Slug { get; init; }
    public required string Status { get; init; }
    public string? BlockedReason { get; init; }
    public string? EvidenceRef { get; init; }
    public string? Note { get; init; }
    public required int ExpectedRevision { get; init; }
}

public sealed record TodoCheckResult
{
    public required string ListId { get; init; }
    public required int Revision { get; init; }
    public required TodoItemView Item { get; init; }
    public required TodoSummary Summary { get; init; }
}

// ── archive ─────────────────────────────────────────────────

public sealed record TodoArchiveRequest
{
    public required string ScopeKind { get; init; }
    public required string ScopeId { get; init; }
}

public sealed record TodoArchiveResult
{
    public required string ListId { get; init; }
    public required int Revision { get; init; }
    public required DateTimeOffset ArchivedAtUtc { get; init; }
}

// ── 错误契约 ────────────────────────────────────────────────

/// <summary>todo 错误码（稳定 wire code 由 <see cref="TodoToolErrors"/> 映射）。</summary>
public enum TodoErrorCode
{
    /// <summary>todo.list_not_found</summary>
    TodoListNotFound,

    /// <summary>todo.item_not_found</summary>
    TodoItemNotFound,

    /// <summary>todo.version_conflict（CAS 不符，不静默覆盖）</summary>
    TodoVersionConflict,

    /// <summary>todo.too_many_items（单列表 &gt; 20 项）</summary>
    TodoTooManyItems,

    /// <summary>todo.multiple_in_progress（同时最多 1 个 in_progress）</summary>
    TodoMultipleInProgress,

    /// <summary>todo.blocked_reason_required（blocked 必填 blocked_reason）</summary>
    TodoBlockedReasonRequired,

    /// <summary>todo.duplicate_slug（列表内 slug 唯一）</summary>
    TodoDuplicateSlug,

    /// <summary>todo.invalid_status</summary>
    TodoInvalidStatus,

    /// <summary>todo.invalid_scope_kind</summary>
    TodoInvalidScopeKind,

    /// <summary>todo.invalid_item（slug/title 等字段非法）</summary>
    TodoInvalidItem,
}

/// <summary>Todo Store 操作失败的契约异常（CAS/约束冲突），与 <see cref="TaskStoreException"/> 同风格。</summary>
public sealed class TodoStoreException : Exception
{
    public TodoErrorCode ErrorCode { get; }
    public string? ScopeKind { get; }
    public string? ScopeId { get; }
    public int? CurrentRevision { get; }

    public TodoStoreException(
        TodoErrorCode code,
        string message,
        string? scopeKind = null,
        string? scopeId = null,
        int? currentRevision = null)
        : base(message)
    {
        ErrorCode = code;
        ScopeKind = scopeKind;
        ScopeId = scopeId;
        CurrentRevision = currentRevision;
    }
}

/// <summary>
/// 工具侧错误体构造（与 <see cref="TaskToolErrors"/> 同风格：code/message/可选 scope 与 current_revision）。
/// Runtime 工具不引用 PuddingPlatform，故 wire 映射在 Core 提供。
/// </summary>
public static class TodoToolErrors
{
    public static string ErrorCodeToString(TodoErrorCode code) => code switch
    {
        TodoErrorCode.TodoListNotFound => "todo.list_not_found",
        TodoErrorCode.TodoItemNotFound => "todo.item_not_found",
        TodoErrorCode.TodoVersionConflict => "todo.version_conflict",
        TodoErrorCode.TodoTooManyItems => "todo.too_many_items",
        TodoErrorCode.TodoMultipleInProgress => "todo.multiple_in_progress",
        TodoErrorCode.TodoBlockedReasonRequired => "todo.blocked_reason_required",
        TodoErrorCode.TodoDuplicateSlug => "todo.duplicate_slug",
        TodoErrorCode.TodoInvalidStatus => "todo.invalid_status",
        TodoErrorCode.TodoInvalidScopeKind => "todo.invalid_scope_kind",
        TodoErrorCode.TodoInvalidItem => "todo.invalid_item",
        _ => code.ToString(),
    };

    private static readonly JsonSerializerOptions ErrorJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>构造统一错误体 JSON（code/message/可选 scope_kind/scope_id/current_revision）。</summary>
    public static string BuildErrorJson(
        TodoErrorCode code,
        string message,
        string? scopeKind = null,
        string? scopeId = null,
        int? currentRevision = null)
    {
        return JsonSerializer.Serialize(new
        {
            error = new
            {
                code = ErrorCodeToString(code),
                message,
                scope_kind = scopeKind,
                scope_id = scopeId,
                current_revision = currentRevision,
            },
        }, ErrorJsonOptions);
    }

    /// <summary>由 <see cref="TodoStoreException"/> 构造统一错误体 JSON。</summary>
    public static string BuildErrorJson(TodoStoreException ex)
        => BuildErrorJson(ex.ErrorCode, ex.Message, ex.ScopeKind, ex.ScopeId, ex.CurrentRevision);
}
