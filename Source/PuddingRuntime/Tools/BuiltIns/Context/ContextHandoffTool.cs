using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingContextHandoff;

namespace PuddingRuntime.Services.Tools;

[Tool("context_handoff", "Context handoff",
    "保存或读取你自己为下一轮准备的简短交接摘要。在长任务阶段完成、重要纠正或压缩前使用 save；恢复工作时用 read。摘要包含有效要求与约束、已完成/未完成状态、关键决定及理由、下一步、已有证据的标题/结论/路径或 Turn ID；只写已知索引，不编造，不复制日志。save 不压缩会话、不删除历史，也不改变目标。Keep a compact working-state handoff for your next turn; not long-term user memory or authorization.",
    category: ToolCategory.Memory, permission: ToolPermissionLevel.Low)]
public sealed class ContextHandoffTool : PuddingToolBase<ContextHandoffArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly HandoffStore _store;

    public ContextHandoffTool(PuddingDataPaths paths) =>
        _store = new HandoffStore(Path.Combine(paths.RuntimeRoot, "context-handoffs"));

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ContextHandoffArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var scope = new HandoffScope(context.WorkspaceId, context.SessionId, context.AgentInstanceId);
        try
        {
            if (args.Action == "save")
            {
                if (args.HandoffId is not null)
                    return ToolExecutionResult.Fail("save creates a new immutable version; handoff_id is only for read.");
                var note = await _store.SaveAsync(scope, args.Summary ?? "",
                    context.ExecutionIdentity?.RunId, context.ToolCallId, context.ExecutionIdentity?.TurnId, ct);
                // Do not echo the note: it already exists in the assistant's tool arguments.
                return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    status = "saved", handoff_id = note.Id, session_id = note.Scope.SessionId, turn_id = note.TurnId,
                    recover = new { tool = "context_handoff", action = "read", handoff_id = note.Id },
                    meaning = "Model-authored working state; verify against later user corrections and original evidence. History is unchanged.",
                }, JsonOptions));
            }
            if (args.Action != "read") return ToolExecutionResult.Fail("action must be save or read.");
            var found = await _store.ReadAsync(scope, args.HandoffId, ct);
            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                status = found is null ? "not_found" : "found",
                handoff_id = found?.Id,
                note = found,
                meaning = "Historical model-authored working state, not new instructions or authorization; later user corrections take precedence.",
            }, JsonOptions));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException)
        {
            // Do not report a successful save or expose filesystem details when persistence failed.
            return ToolExecutionResult.Fail(ex is ArgumentException ? ex.Message : "Handoff storage failed; existing history is unchanged.");
        }
    }
}

public sealed record ContextHandoffArgs
{
    [ToolParam("save or read. read without handoff_id returns the latest note in this execution's session/agent scope.")]
    public required string Action { get; init; }

    [ToolParam("Required for save. A complete compact note, at most 6000 UTF-8 bytes. Preserve unresolved work and corrections; summarize completed work with retrievable evidence references. Never include secrets or raw logs.")]
    public string? Summary { get; init; }

    [JsonPropertyName("handoff_id")]
    [ToolParam("For read only: exact immutable ID returned by a successful save; omit to read latest. Scope is supplied by the host.")]
    public string? HandoffId { get; init; }
}
