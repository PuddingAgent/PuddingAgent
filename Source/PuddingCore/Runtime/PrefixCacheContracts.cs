using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingCode.Runtime;

/// <summary>
/// Prefix 缓存快照。只保存工程诊断哈希，不保存原始 prompt 内容。
/// </summary>
public sealed record PromptPrefixSnapshot
{
    /// <summary>快照算法版本；改变 canonicalization 规则时递增。</summary>
    public string Version { get; init; } = PrefixCacheSnapshotBuilder.Version;

    /// <summary>system prompt 与工具规格组成的稳定前缀哈希。</summary>
    public required string PrefixHash { get; init; }

    /// <summary>system prompt 单独哈希，用于判断是否由系统提示词导致 churn。</summary>
    public required string SystemPromptHash { get; init; }

    /// <summary>工具规格哈希；工具增删、重排、描述或 schema 改变都会反映在这里。</summary>
    public required string ToolSpecHash { get; init; }

    /// <summary>
    /// system 之后第一条历史消息的规范哈希。正常尾部 append 不改变该值；
    /// 头部裁剪、重水合重排或 checkpoint 替换会改变，用于补足旧版仅看 system/tool 的盲区。
    /// </summary>
    public string? HistoryAnchorHash { get; init; }

    /// <summary>Provider 请求 envelope 的稳定序列化版本。</summary>
    public string SerializationVersion { get; init; } = PrefixCacheSnapshotBuilder.SerializationVersion;

    /// <summary>长期记忆哈希；当前版本尚未从 system prompt 中拆分时可为空。</summary>
    public string? MemoryHash { get; init; }

    /// <summary>few-shot 示例哈希；当前版本未启用时可为空。</summary>
    public string? FewShotHash { get; init; }

    /// <summary>本次 prefix 改变原因；为空表示没有显式声明，诊断时按 unexpected churn 处理。</summary>
    public string? PrefixChangeReason { get; init; }

    /// <summary>生成快照时的消息数量，用于辅助排查请求形状。</summary>
    public int MessageCount { get; init; }

    /// <summary>生成快照时的工具数量。</summary>
    public int ToolCount { get; init; }

    /// <summary>快照创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Prefix 变化原因常量；与 TokenUsageEvents.PrefixChangeReason、缓存报表 SQL 共用。</summary>
public static class PrefixChangeReasons
{
    public const string SystemPromptChanged = "system_prompt_changed";

    public const string ToolSpecChanged = "tool_spec_changed";

    /// <summary>进程重启或内存会话过期后从持久层重水合历史，provider 前缀缓存大概率已过期。</summary>
    public const string SessionRehydrated = "session_rehydrated";

    /// <summary>辅助压缩请求原样重放当前 warm prefix，只在尾部追加固定摘要指令。</summary>
    public const string CompactionReplay = "compaction_replay";

    /// <summary>成功摘要后用单个 checkpoint 原子替换旧历史区间，显式开启新的历史 epoch。</summary>
    public const string CompactionCheckpoint = "compaction_checkpoint";

    /// <summary>首条稳定历史锚点变化；通常表示历史头被替换、驱逐或重排。</summary>
    public const string HistoryAnchorChanged = "history_anchor_changed";

    /// <summary>prefix 快照或 provider envelope 序列化版本变化。</summary>
    public const string SerializationVersionChanged = "serialization_version_changed";
}

/// <summary>
/// Prefix 缓存快照构建器。目标是提供稳定、可测试、可归因的 prefix 指纹。
/// </summary>
public static class PrefixCacheSnapshotBuilder
{
    public const string Version = "prefix-v2";
    public const string SerializationVersion = "pudding-request-envelope-v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    /// <summary>
    /// 从 LLM 请求消息和工具定义构建 prefix 快照。用户消息与 assistant/tool 日志不会进入 prefix hash。
    /// </summary>
    public static PromptPrefixSnapshot Build(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<LlmToolDefinition> tools,
        string? prefixChangeReason = null)
    {
        var systemPrompt = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Content ?? string.Empty;
        var canonicalTools = tools.Select(t => new
        {
            t.Name,
            t.Description,
            Parameters = new
            {
                Properties = t.Parameters.Properties.Select(p => new
                {
                    p.Name,
                    p.Type,
                    p.Description,
                }).ToArray(),
                Required = t.Parameters.Required.ToArray(),
                RawJsonSchema = t.Parameters.RawJsonSchema?.GetRawText(),
            },
        }).ToArray();

        var toolSpecHash = HashCanonical(canonicalTools);
        var stableSystemPrompt = ExtractStableSystemPrompt(systemPrompt);
        var systemPromptHash = HashText(stableSystemPrompt);
        var historyAnchor = messages.FirstOrDefault(message => message.Role != ChatRole.System);
        var historyAnchorHash = historyAnchor is null
            ? null
            : HashCanonical(new
            {
                Role = historyAnchor.Role.ToString(),
                historyAnchor.Content,
                historyAnchor.ToolCallId,
                historyAnchor.ToolName,
                historyAnchor.ToolCalls,
                historyAnchor.ReasoningContent,
                historyAnchor.VisualArtifactIds,
                historyAnchor.AudioArtifactIds,
                historyAnchor.ContinuationState,
                historyAnchor.ContentParts,
            });
        var prefixHash = HashCanonical(new
        {
            Version,
            SerializationVersion,
            SystemPrompt = stableSystemPrompt,
            Tools = canonicalTools,
            HistoryAnchorHash = historyAnchorHash,
        });

        return new PromptPrefixSnapshot
        {
            Version = Version,
            PrefixHash = prefixHash,
            SystemPromptHash = systemPromptHash,
            ToolSpecHash = toolSpecHash,
            HistoryAnchorHash = historyAnchorHash,
            SerializationVersion = SerializationVersion,
            PrefixChangeReason = NormalizeReason(prefixChangeReason),
            MessageCount = messages.Count,
            ToolCount = tools.Count,
        };
    }

    /// <summary>
    /// 从完整系统提示词中截取稳定层（L0-Static 到 L4-Pinned），
    /// 排除动态层（L6-CONTEXT-AUGMENT/RECALLED 和 L9-CURRENT）。
    /// 遇到 "--- LAYER: " 开头且包含 RECALLED/CONTEXT-AUGMENT/CURRENT 的行时截断。
    /// </summary>
    internal static string ExtractStableSystemPrompt(string fullSystemPrompt)
    {
        if (string.IsNullOrEmpty(fullSystemPrompt))
            return string.Empty;

        var lines = fullSystemPrompt.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("--- LAYER:") is false
                && trimmed.StartsWith("--- CONTEXT-LAYER:") is false)
                continue;
            if (trimmed.Contains("RECALLED") || trimmed.Contains("CONTEXT-AUGMENT") || trimmed.Contains("CURRENT"))
                return string.Join('\n', lines, 0, i).TrimEnd();
        }

        // 没有找到动态层标记 → 整个 prompt 都是稳定的
        return fullSystemPrompt;
    }

    private static string? NormalizeReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

    private static string HashCanonical<T>(T value) =>
        HashText(JsonSerializer.Serialize(value, JsonOptions));

    private static string HashText(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
