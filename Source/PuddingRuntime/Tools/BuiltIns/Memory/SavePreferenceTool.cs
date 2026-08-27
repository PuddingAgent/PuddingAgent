// ═══════════════════════════════════════════════════════════════
// save_preference — 用户偏好存储工具（Sync）
// ═══════════════════════════════════════════════════════════════

using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 用户偏好存储 Tool：Agent 在对话中感知到用户的明确偏好（语言、风格、习惯、约束等）时，
/// 调用本工具写入记忆图书馆的「用户偏好」Book。同一 key 重复写入为幂等覆盖。
/// 会话启动时 ContextPipeline 会把这些偏好自动注入 System Prompt（Prefetch）。
/// Agent 自有 Memory 库写入属低风险自治操作，经用户指示（2026-08-27）归类 AutoAllowed 免运行时审批。
/// </summary>
[Tool(
    id: "save_preference",
    name: "save_preference",
    description: "存储用户偏好。当用户在对话中明确表达偏好（语言、风格、习惯、沟通方式、禁忌等）时使用。同一 key 重复保存会覆盖旧值。参数：key（偏好名称）、value（偏好值）。",
    category: ToolCategory.Memory,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe)]
public sealed class SavePreferenceTool : PuddingToolBase<SavePreferenceArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly IUserPreferenceService _preferences;
    private readonly ILogger<SavePreferenceTool> _logger;

    public SavePreferenceTool(
        IUserPreferenceService preferences,
        ILogger<SavePreferenceTool> logger)
    {
        _preferences = preferences;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SavePreferenceArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var workspaceId = context.WorkspaceId;
        var key = args.Key?.Trim();
        var value = args.Value?.Trim();
        var action = (args.Action ?? "upsert").Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(workspaceId))
            return ToolExecutionResult.Fail("workspace_id is required (tool context missing workspace).");

        try
        {
            if (action == "delete")
            {
                if (string.IsNullOrWhiteSpace(key))
                    return ToolExecutionResult.Fail("key is required for delete.");

                var deleted = await _preferences.DeletePreferenceAsync(workspaceId, key, ct);
                return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    status = deleted ? "ok" : "not_found",
                    action = "delete",
                    key,
                    deleted,
                }, JsonOptions));
            }

            if (action != "upsert")
                return ToolExecutionResult.Fail($"Unsupported action '{action}'. Use 'upsert' or 'delete'.");

            if (string.IsNullOrWhiteSpace(key))
                return ToolExecutionResult.Fail("key is required (e.g. key=\"language\").");
            if (string.IsNullOrWhiteSpace(value))
                return ToolExecutionResult.Fail("value is required (e.g. value=\"中文\").");

            var result = await _preferences.SavePreferenceAsync(
                workspaceId,
                key,
                value,
                sourceSessionId: context.SessionId,
                agentInstanceId: context.AgentInstanceId,
                ct);

            _logger.LogInformation(
                "[SavePreference] {Op} key={Key} book={Book} chapter={Chapter}",
                result.Updated ? "updated" : "created",
                result.Key, result.BookId, result.ChapterId);

            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                status = "ok",
                action = "upsert",
                key = result.Key,
                value = result.Value,
                updated = result.Updated,
                bookId = result.BookId,
                chapterId = result.ChapterId,
                message = result.Updated
                    ? $"已更新用户偏好 {result.Key} = {result.Value}"
                    : $"已保存用户偏好 {result.Key} = {result.Value}",
            }, JsonOptions));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SavePreference] Failed key={Key} workspace={Workspace}", key, workspaceId);
            return ToolExecutionResult.Fail($"Failed to save preference: {ex.Message}");
        }
    }
}

/// <summary>save_preference 工具参数。</summary>
public sealed record SavePreferenceArgs
{
    [ToolParam("Preference key, e.g. language / 语言 / 回复风格.")]
    public string? Key { get; init; }

    [ToolParam("Preference value, e.g. 中文 / 简洁 / Markdown.")]
    public string? Value { get; init; }

    [ToolParam("Operation: upsert (default) or delete.")]
    public string? Action { get; init; }

    [ToolParam("Optional source reference such as session id or URL.")]
    public string? SourceReference { get; init; }
}
