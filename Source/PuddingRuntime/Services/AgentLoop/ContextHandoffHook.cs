using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using PuddingCode.Platform;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.AgentLoop;

/// <summary>Advisory only. Uses a frozen request measurement; no database or auxiliary LLM calls.</summary>
public sealed class ContextHandoffHook(ContextUsageSnapshotStore? snapshots = null, TimeProvider? clock = null) : IAgentLoopHook
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<(string Workspace, string Session, string Agent), DateTimeOffset> _reminders = new();
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
        { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private const string Advice = "上下文占用已过半。请在下一个合适的工作边界用 context_handoff(action=save, summary=...) " +
        "为下一轮保留短交接：有效要求/纠正/约束、已完成与未完成状态、关键决定及理由、下一步、已有证据的标题/结论/索引。" +
        "先 read 已有摘要并合并仍有效事项；不要堆日志或秘密，不编造 Turn ID。工具未加载时可用 search_tools 查找；" +
        "不可用则继续当前工作。此提示不要求立即压缩或停止任务。";

    public Task<SkillResult> OnToolResultTransformingAsync(AgentLoopContext context, int round,
        string toolName, string argsJson, SkillResult result, CancellationToken ct = default)
    {
        if (!result.Success || ct.IsCancellationRequested) return Task.FromResult(result);
        var key = (context.WorkspaceId, context.SessionId, context.AgentInstanceId);
        if (toolName == "context_handoff")
        {
            try
            {
                if (JsonNode.Parse(result.Output) is JsonObject saved && saved["status"]?.ToString() == "saved")
                    lock (_reminders) Remember(key);
            }
            catch (JsonException) { }
            return Task.FromResult(result);
        }
        // Never change catalog, protocol/PID or control-tool payloads.
        if (toolName is not ("file_read" or "terminal_start" or "terminal_wait" or "terminal_status" or "search_grep" or "shell")
            || result.Output.Length > 16 * 1024
            || context.EffectiveInputLimit is not > 0 || snapshots is null
            || !snapshots.TryGet(context.SessionId, out var usage) || usage is null
            || usage.UsedTokens < context.EffectiveInputLimit.Value * 0.5) return Task.FromResult(result);
        lock (_reminders)
        {
            if (_reminders.TryGetValue(key, out var last) && _clock.GetUtcNow() - last < Cooldown)
                return Task.FromResult(result);
            var output = AddAdvice(result.Output);
            if (output is null) return Task.FromResult(result);
            Remember(key);
            return Task.FromResult(result with { Output = output });
        }
    }

    private void Remember((string Workspace, string Session, string Agent) key)
    {
        if (_reminders.Count >= 256 && !_reminders.ContainsKey(key))
            _reminders.Remove(_reminders.MinBy(p => p.Value).Key);
        _reminders[key] = _clock.GetUtcNow();
    }

    private static string? AddAdvice(string output)
    {
        try
        {
            // Keep structured observation results valid JSON, retaining every existing field.
            if (JsonNode.Parse(output) is not JsonObject json) return null;
            json["_context_handoff_advice"] = Advice;
            return json.ToJsonString(JsonOptions);
        }
        catch (JsonException)
        {
            return output + "\n\n<context-handoff-advice>" + Advice + "</context-handoff-advice>";
        }
    }
}
