using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// Composition Snapshot：逐请求计算 system prompt 与工具 schema 的稳定 SHA-256 hash，
/// 以及组合 prefix hash，用于前缀缓存命中率归因（方案 12.4 观测闭环）。
/// 只产出 hash 与元数据，绝不保存/上报 prompt 正文、工具输出或工具 schema 全文。
///
/// 注意：与 <see cref="PuddingCode.Runtime.PrefixCacheSnapshotBuilder"/> 的语义不同——
/// 前者只对「稳定层」做指纹（用于 KV-cache 复用），本组件对「完整 composition」
/// （全部 system 消息 + 全部工具定义）做观测，用于定位任何导致前缀漂移的变更。
/// </summary>
public static class CompositionSnapshot
{
    /// <summary>规范化 JSON 序列化选项：camelCase、无缩进、字节稳定。</summary>
    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    /// <summary>
    /// 计算 system prompt 的 SHA-256 hash（小写 hex）。
    /// 取所有 role==System 的 content 按消息顺序以 '\n' 拼接；null content 视为空串。
    /// </summary>
    public static string ComputeSystemPromptHash(IReadOnlyList<ChatMessage> messages)
    {
        var parts = messages
            .Where(m => m.Role == ChatRole.System)
            .Select(m => m.Content ?? string.Empty);
        return Sha256Hex(string.Join('\n', parts));
    }

    /// <summary>
    /// 计算工具 schema 的 SHA-256 hash（小写 hex）。
    /// 规范化：仅取模型可见的 Name/Description/Parameters（Properties 按 Name/Type/Description 投影，
    /// Required 顺序保留，RawJsonSchema 取原文），忽略运行期暴露策略 SubAgentExposure。
    /// null 或空工具列表 hash 空串。
    /// </summary>
    public static string ComputeToolSpecHash(IReadOnlyList<LlmToolDefinition>? tools)
    {
        if (tools is null || tools.Count == 0)
            return Sha256Hex(string.Empty);

        var canonical = tools.Select(t => new
        {
            t.Name,
            t.Description,
            Parameters = new
            {
                Properties = t.Parameters.Properties
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new
                    {
                        p.Name,
                        p.Type,
                        p.Description,
                    }).ToArray(),
                Required = t.Parameters.Required.ToArray(),
                RawJsonSchema = t.Parameters.RawJsonSchema?.GetRawText(),
            },
        }).ToArray();

        return Sha256Hex(JsonSerializer.Serialize(canonical, CanonicalJson));
    }

    /// <summary>组合 system/tool 两个 hash 得到 prefix hash（分隔符 '\u001f' 不与 hex 冲突）。</summary>
    public static string ComputePrefixHash(string systemPromptHash, string toolSpecHash)
        => Sha256Hex(systemPromptHash + "\u001f" + toolSpecHash);

    /// <summary>SHA-256，输出小写 hex。</summary>
    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>
/// 进程内 composition 版本登记表（纯内存版）：按 (sessionId, systemPromptHash, toolSpecHash) 递增版本，
/// 相同组合复用同一版本；并给出本次相对上次的变化原因。线程安全（外层 ConcurrentDictionary，
/// 每会话内部互斥）。不持久化，进程重启归零；供无 <see cref="ICompositionStore"/> 场景/测试使用。
/// </summary>
public sealed class CompositionVersionRegistry : ICompositionVersionRegistry
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public CompositionObservation Observe(
        string sessionId,
        string systemPromptHash,
        string toolSpecHash,
        IReadOnlyList<string>? toolIds = null,
        int permissionEpoch = 0,
        string? skillManifestHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var state = _sessions.GetOrAdd(sessionId, static _ => new SessionState());
        return state.Observe(systemPromptHash, toolSpecHash);
    }

    private sealed class SessionState
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _versions = new(StringComparer.Ordinal);
        private int _nextVersion = 1;
        private string _lastSystemPromptHash = string.Empty;
        private string _lastToolSpecHash = string.Empty;
        private bool _hasLast;

        public CompositionObservation Observe(string systemPromptHash, string toolSpecHash)
        {
            lock (_gate)
            {
                var changeReason = DetectChangeReason(systemPromptHash, toolSpecHash);

                var key = systemPromptHash + "\u001f" + toolSpecHash;
                if (!_versions.TryGetValue(key, out var version))
                {
                    version = _nextVersion++;
                    _versions[key] = version;
                }

                _lastSystemPromptHash = systemPromptHash;
                _lastToolSpecHash = toolSpecHash;
                _hasLast = true;

                return new CompositionObservation(version, changeReason);
            }
        }

        private string DetectChangeReason(string systemPromptHash, string toolSpecHash)
        {
            if (!_hasLast)
                return "initial";

            var systemChanged = !string.Equals(_lastSystemPromptHash, systemPromptHash, StringComparison.Ordinal);
            var toolChanged = !string.Equals(_lastToolSpecHash, toolSpecHash, StringComparison.Ordinal);

            if (systemChanged && toolChanged)
                return "system_prompt_changed,tool_spec_changed";
            if (systemChanged)
                return "system_prompt_changed";
            if (toolChanged)
                return "tool_spec_changed";
            return "none";
        }
    }
}
