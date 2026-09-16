using System.Text.Json;

namespace PuddingCode.Tasks;

/// <summary>
/// Todo 拆解工具（设计 2026-09-16 §3/§4，TD-1；TD-1b 对齐用户裁决）的跨层契约。
/// <para>
/// 与 <see cref="ITaskAgentCommandService"/> 同模式：接口定义在 PuddingCore，由 PuddingPlatform 的
/// <c>TodoStore</c> 实现；PuddingRuntime 的 todo_* 工具只透传/序列化 wire 值，不依赖 Platform 类型。
/// </para>
/// <para>
/// TD-1b（2026-09-16 裁决）：① 所有读写按 <c>AgentId</c>（ToolExecutionContext.AgentInstanceId）过滤，
/// 跨 Agent 互不可见；② 约束分级——硬拒绝（slug 列表内唯一 / scope_kind+scope_id 必填 /
/// CAS 冲突明确报错 / 跨 Agent 隔离），软警告（&gt;20 项 / &gt;1 in_progress / blocked 无 reason ⇒
/// 接受写入并返回 warnings）；③ 归档语义移除（工具面无归档入口、不写 archived_at_utc）。
/// </para>
/// </summary>
public interface ITodoStore
{
    /// <summary>全量替换某作用域的拆解表（agent 隔离 + expected_revision CAS + 幂等 by slug + 返回 diff/warnings）。</summary>
    Task<TodoWriteResult> WriteAsync(TodoWriteRequest request, CancellationToken ct = default);

    /// <summary>读取某作用域的拆解表 + 汇总；列表不存在返回 null（不视为错误）。按 AgentId 过滤。</summary>
    Task<TodoReadResult?> ReadAsync(TodoReadQuery query, CancellationToken ct = default);

    /// <summary>勾选单项状态（agent 隔离 + CAS；软约束产出 warnings，不再硬拒）。</summary>
    Task<TodoCheckResult> CheckAsync(TodoCheckRequest request, CancellationToken ct = default);
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

    /// <summary>建议上限（TD-1b 软约束：超过返回 warning，不拒绝；设计 §3「它是交流工具，不是流程闸门」）。</summary>
    public const int MaxItemsPerList = 20;

    /// <summary>服务端强制约束（设计 §3）：item title 上限。</summary>
    public const int MaxTitleLength = 120;

    // ── 软约束 warning 稳定前缀（TD-1b：供 UI/Agent 程序化识别；设计 §3「返回 warning 供 UI 提示」）──

    /// <summary>todo.too_many_items：列表超过建议上限。</summary>
    public const string WarnTooManyItems = "todo.too_many_items";

    /// <summary>todo.multiple_in_progress：同时多于 1 个 in_progress。</summary>
    public const string WarnMultipleInProgress = "todo.multiple_in_progress";

    /// <summary>todo.blocked_reason_required：blocked 项未提供 blocked_reason。</summary>
    public const string WarnBlockedReasonRequired = "todo.blocked_reason_required";
}

// ── todo_write ──────────────────────────────────────────────

public sealed record TodoWriteRequest
{
    /// <summary>
    /// 归属 Agent 身份（工具侧取 ToolExecutionContext.AgentInstanceId，平台注入、非空）。
    /// 读写均按 agent_id 过滤（设计 §3）：跨 Agent 即使 scope_id 完全相同也互不可见、互不可写。
    /// </summary>
    public required string AgentId { get; init; }

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

    /// <summary>全量目标状态（建议 ≤20 项；同 slug 覆盖，缺席即移除）。</summary>
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

    /// <summary>
    /// 软约束提示（TD-1b：&gt;20 项 / &gt;1 in_progress / blocked 无 reason ⇒ 接受写入 + warning）。
    /// 每条以 <see cref="TodoWireMaps"/> 的 Warn* 稳定前缀开头；无警告为 null（wire 省略，向后兼容）。
    /// </summary>
    public IReadOnlyList<string>? Warnings { get; init; }
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
    /// <summary>归属 Agent 身份（读写按 agent_id 过滤，跨 Agent 隔离）。</summary>
    public required string AgentId { get; init; }

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
    /// <summary>归属 Agent 身份（读写按 agent_id 过滤，跨 Agent 隔离）。</summary>
    public required string AgentId { get; init; }

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

    /// <summary>软约束提示（&gt;1 in_progress / blocked 无 reason）；每条带 Warn* 稳定前缀；无警告为 null。</summary>
    public IReadOnlyList<string>? Warnings { get; init; }
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

    /// <summary>todo.invalid_agent（agent_id 缺失/空白；由平台注入，出现即管道缺陷）</summary>
    TodoInvalidAgent,

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
        TodoErrorCode.TodoInvalidAgent => "todo.invalid_agent",
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
