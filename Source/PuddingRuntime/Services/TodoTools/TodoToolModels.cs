using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.TodoTools;

/// <summary>
/// todo_* 三工具（设计 2026-09-16 §4，TD-1）共享的参数模型与序列化帮助。
/// <para>
/// 与 <see cref="PuddingRuntime.Services.TaskTools.TaskToolJson"/> 同风格：
/// snake_case + 忽略 null；Core 契约 DTO（<see cref="PuddingCode.Tasks.TodoWriteResult"/> 等）
/// 即 wire 形状，直接序列化。
/// </para>
/// </summary>
internal static class TodoToolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = new SnakeCaseNamingPolicy(),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(object? value)
        => JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), Options);

    private sealed class SnakeCaseNamingPolicy : JsonNamingPolicy
    {
        public override string ConvertName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }

            var sb = new StringBuilder(name.Length + 8);
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0)
                    {
                        sb.Append('_');
                    }

                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }
    }
}

// ── todo_write ──────────────────────────────────────────────

public sealed record TodoWriteArgs
{
    [ToolParam("挂载对象类型：goal | task | session。")]
    public required string ScopeKind { get; init; }

    [ToolParam("挂载对象 ID：goalRunId | taskId | sessionId。")]
    public required string ScopeId { get; init; }

    [ToolParam("可选列表标题（如「看板梳理拆解」）；省略保持原值。")]
    public string? Title { get; init; }

    [ToolParam("CAS 期望版本：首写传 0；否则传 todo_read/todo_write 返回的最新 revision，不符返回 todo.version_conflict（不静默覆盖）。")]
    public required int ExpectedRevision { get; init; }

    [ToolParam("全量目标状态（≤20 项；同 slug 覆盖、缺席即移除）。")]
    public required TodoItemArgs[] Items { get; init; }
}

public sealed record TodoItemArgs
{
    [ToolParam("稳定标识，列表内唯一；全量替换 diff 的键。")]
    public required string Slug { get; init; }

    [ToolParam("≤120 字。")]
    public required string Title { get; init; }

    [ToolParam("pending | in_progress | completed | blocked；同一时刻最多 1 项 in_progress。")]
    public required string Status { get; init; }

    [ToolParam("可选说明。")]
    public string? Note { get; init; }

    [ToolParam("可选证据引用：commit sha / 文件路径 / 报告 id。")]
    public string? EvidenceRef { get; init; }

    [ToolParam("status=blocked 时必填。")]
    public string? BlockedReason { get; init; }

    [ToolParam("展示顺序；缺省按数组位置。")]
    public int? OrderIndex { get; init; }
}

// ── todo_read ───────────────────────────────────────────────

public sealed record TodoReadArgs
{
    [ToolParam("挂载对象类型：goal | task | session。")]
    public required string ScopeKind { get; init; }

    [ToolParam("挂载对象 ID：goalRunId | taskId | sessionId。")]
    public required string ScopeId { get; init; }
}

// ── todo_check ──────────────────────────────────────────────

public sealed record TodoCheckArgs
{
    [ToolParam("挂载对象类型：goal | task | session。")]
    public required string ScopeKind { get; init; }

    [ToolParam("挂载对象 ID：goalRunId | taskId | sessionId。")]
    public required string ScopeId { get; init; }

    [ToolParam("目标单项的 slug。")]
    public required string Slug { get; init; }

    [ToolParam("目标状态：completed | in_progress | blocked | pending；blocked 必填 blocked_reason。")]
    public required string Status { get; init; }

    [ToolParam("status=blocked 时必填的结构化受阻原因。")]
    public string? BlockedReason { get; init; }

    [ToolParam("completed 建议携带的证据引用（commit sha / 文件路径 / 报告 id）。")]
    public string? EvidenceRef { get; init; }

    [ToolParam("可选说明（补充受阻细节或进展备注）。")]
    public string? Note { get; init; }

    [ToolParam("CAS 期望版本：传 todo_read/todo_write 返回的最新 revision，不符返回 todo.version_conflict。")]
    public required int ExpectedRevision { get; init; }
}

// ── todo_read 未找到时的 wire 形状 ──────────────────────────

/// <summary>列表不存在时 todo_read 的成功响应（不是错误；面板显示空拆解区）。</summary>
public sealed record TodoReadNotFoundResult
{
    public required string ScopeKind { get; init; }
    public required string ScopeId { get; init; }
    public bool Found => false;
}
