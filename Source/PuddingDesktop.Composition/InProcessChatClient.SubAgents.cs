using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingChat;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

internal sealed partial class InProcessChatClient : ISubAgentInspectionClient
{
    public Task<SubAgentInspection> ReadAsync(SubAgentInspectionKey key, CancellationToken ct) => ExecuteAsync(async (services, token) =>
    {
        if (string.IsNullOrWhiteSpace(key.RunId) || key.RunId is "." or ".." || key.RunId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("无效的运行身份。", nameof(key));
        var agent = await services.GetRequiredService<WorkspaceAgentFileService>().GetAgentAsync(key.Role.WorkspaceId, key.Role.AgentId, token);
        var session = await services.GetRequiredService<ISessionRepository>().GetAsync(key.ParentSessionId, token);
        if (agent is null || session is null || session.WorkspaceId != key.Role.WorkspaceId
            || (session.PrincipalId ?? session.AgentInstanceId) != key.Role.AgentId)
            throw new InvalidOperationException("父会话与角色归属不匹配。");
        var archive = await services.GetRequiredService<ISubAgentRunStore>().GetRunArchiveAsync(key.RunId, token)
            ?? throw new FileNotFoundException("子代理运行归档不存在。");
        var manifest = archive.Manifest;
        if (manifest.RunId != key.RunId || manifest.ParentSessionId != key.ParentSessionId || manifest.WorkspaceId != key.Role.WorkspaceId)
            throw new InvalidOperationException("子代理归档与父会话归属不匹配。");
        var items = new List<ProcessItem>(); long sequence = 0;
        foreach (var raw in archive.Events)
        {
            token.ThrowIfCancellationRequested();
            var entry = raw is JsonElement json ? json : JsonSerializer.SerializeToElement(raw);
            var type = Read(entry, "eventType") ?? "unknown";
            var id = Read(entry, "eventId") ?? $"archive-{sequence}";
            var payload = entry.TryGetProperty("payload", out var value) && value.ValueKind == JsonValueKind.Object ? value : entry;
            var call = Read(payload, "tool_call_id");
            var name = Read(payload, "tool_name");
            var status = type[(type.LastIndexOf('.') + 1)..] switch
            {
                "failed" => "failed", "started" or "created" => "running", "completed" => "done",
                "cancelled" => "cancelled", "timed_out" => "timed_out", "interrupted" => "interrupted",
                "budget_exhausted" => "budget_exhausted", _ => "记录",
            };
            if (type == "subagent.llm.completed")
            {
                if (Preview(payload, "reasoning_preview", "reasoning_truncated") is { Length: > 0 } thinking)
                    items.Add(new(id + ":thinking", "thinking", "done", thinking, ++sequence));
                if (Preview(payload, "message_preview", "message_truncated") is { Length: > 0 } message)
                    items.Add(new(id + ":message", "text", "done", message, ++sequence));
            }
            else if (type == "subagent.tool.started")
                items.Add(new(id, "tool_call", status, name ?? "工具", ++sequence, name,
                    Arguments: Preview(payload, "arguments_preview", "arguments_truncated"), ToolCallId: call));
            else if (type is "subagent.tool.completed" or "subagent.tool.failed")
                items.Add(new(id, "tool_result", status, name ?? "工具", ++sequence, name,
                    Output: string.Join("\n", new[] { Preview(payload, "output_preview", "output_truncated"), Read(payload, "error") }.Where(s => !string.IsNullOrEmpty(s))),
                    Message: Read(payload, "error"), ToolCallId: call));
            else items.Add(new(id, "activity", status, payload.GetRawText(), ++sequence, type));
        }
        return new SubAgentInspection(key, manifest.Status, manifest.Task, archive.Output, items.ToArray(),
            manifest.StartedAt, manifest.CompletedAt, manifest.TotalRounds, manifest.TotalToolCalls,
            manifest.ErrorMessage ?? archive.ErrorOutput,
            archive.Degraded is { } degraded ? $"归档降级：缺失 {degraded.DroppedEventCount} 条事件。{degraded.LastError}" : null);
    }, ct);
    private static string? Read(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static string? Preview(JsonElement value, string name, string truncated)
    {
        var text = Read(value, name);
        return text is not null && value.TryGetProperty(truncated, out var flag) && flag.ValueKind == JsonValueKind.True
            ? text + "\n（归档仅保存截断预览）" : text;
    }
}
