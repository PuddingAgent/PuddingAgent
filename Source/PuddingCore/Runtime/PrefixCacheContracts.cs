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

/// <summary>
/// Prefix 缓存快照构建器。目标是提供稳定、可测试、可归因的 prefix 指纹。
/// </summary>
public static class PrefixCacheSnapshotBuilder
{
    public const string Version = "prefix-v1";

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
            },
        }).ToArray();

        var toolSpecHash = HashCanonical(canonicalTools);
        var systemPromptHash = HashText(systemPrompt);
        var prefixHash = HashCanonical(new
        {
            Version,
            SystemPrompt = systemPrompt,
            Tools = canonicalTools,
        });

        return new PromptPrefixSnapshot
        {
            Version = Version,
            PrefixHash = prefixHash,
            SystemPromptHash = systemPromptHash,
            ToolSpecHash = toolSpecHash,
            PrefixChangeReason = NormalizeReason(prefixChangeReason),
            MessageCount = messages.Count,
            ToolCount = tools.Count,
        };
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
