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

    // ── L0 静态层缓存键（P0-5 step 4c）──────────────────────

    /// <summary>
    /// L0 静态层固定顺序（CONTEXT-LAYER 标记名）。
    /// 语义：这些层不随会话动态变化（STATIC/ENVIRONMENT/AGENTS-ROSTER/TOOLS/SKILLS/WORKSPACE-ENVIRONMENT），
    /// 其稳定序列化的 SHA-256 作为 <see cref="SessionCompositionRecord.CanonicalSystemPrefixHash"/> 缓存键。
    /// </summary>
    public static readonly IReadOnlyList<string> CanonicalStaticLayerNames =
    [
        "L0-STATIC",
        "L0-ENVIRONMENT",
        "L0-AGENTS-ROSTER",
        "L1-TOOLS",
        "L2-SKILLS",
        "L3-WORKSPACE-ENVIRONMENT",
    ];

    /// <summary>
    /// 计算 L0 静态层缓存键（P0-5 step 4c）。
    /// 只取 <see cref="CanonicalStaticLayerNames"/> 中存在的层，按固定层序拼接
    /// <c>layerName + '\u001f' + content</c>（分隔符不与 hex 冲突）后 SHA-256。
    /// 相同层集合无论构造顺序如何都得到相同 hash（内部按固定层序遍历）；
    /// 没有任何静态层时返回 null。
    /// </summary>
    public static string? ComputeCanonicalSystemPrefixHash(IReadOnlyDictionary<string, string> staticLayers)
    {
        if (staticLayers is null || staticLayers.Count == 0)
            return null;

        var canonical = new List<string>(CanonicalStaticLayerNames.Count);
        foreach (var layerName in CanonicalStaticLayerNames)
        {
            if (staticLayers.TryGetValue(layerName, out var content))
                canonical.Add(layerName + "\u001f" + (content ?? string.Empty));
        }

        if (canonical.Count == 0)
            return null;

        return Sha256Hex(string.Join('\n', canonical));
    }

    /// <summary>
    /// 从完整系统提示词（含 <c>--- CONTEXT-LAYER: xxx ---</c> 标记）提取 L0 静态层内容并计算缓存键。
    /// 找不到任何静态层标记时返回 null。
    /// </summary>
    public static string? ComputeCanonicalSystemPrefixHashFromPrompt(string? fullSystemPrompt)
    {
        if (string.IsNullOrWhiteSpace(fullSystemPrompt))
            return null;

        var layers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var layerName in CanonicalStaticLayerNames)
        {
            var content = ExtractLayerContent(fullSystemPrompt, layerName);
            if (content is not null)
                layers[layerName] = content;
        }

        return ComputeCanonicalSystemPrefixHash(layers);
    }

    /// <summary>
    /// 计算 L2 SKILLS 层的 Skill Manifest 版本指纹（P0-5 step 6b）。
    /// 对完整 system prompt 中 <c>--- CONTEXT-LAYER: L2-SKILLS ---</c> 层的文本内容做 SHA-256（小写 hex），
    /// 作为 skill_manifest_hash 遥测维度与 Skill Manifest 版本归因依据；
    /// 提取不到该层（或 prompt 为 null/空白）时返回 null。
    /// </summary>
    public static string? ComputeSkillManifestHashFromPrompt(string? fullSystemPrompt)
    {
        if (string.IsNullOrWhiteSpace(fullSystemPrompt))
            return null;

        var content = ExtractLayerContent(fullSystemPrompt, "L2-SKILLS");
        return content is null ? null : Sha256Hex(content);
    }

    /// <summary>从完整组装字符串中提取指定 CONTEXT-LAYER 层的内容（与 ContextPipeline 语义一致）。</summary>
    private static string? ExtractLayerContent(string fullAssembly, string layerName)
    {
        var marker = $"--- CONTEXT-LAYER: {layerName} ---";
        var idx = fullAssembly.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return null;

        var nextMarker = "--- CONTEXT-LAYER:";
        var nextIdx = fullAssembly.IndexOf(nextMarker, idx + marker.Length, StringComparison.Ordinal);
        if (nextIdx < 0)
            nextIdx = fullAssembly.Length;

        return fullAssembly[idx..nextIdx].TrimEnd();
    }

    /// <summary>
    /// 计算权限集合的稳定指纹（P0-5 step 4c）。
    /// 输入为当前生效的工具 ID 集合（授权/能力过滤后的可见投影，append-only 会话集合取当前授权可见集），
    /// 有序去重后以 '\u001f' 拼接 SHA-256；集合顺序不影响结果。用于注册表检测权限/工具授权变化。
    /// </summary>
    public static string? ComputePermissionFingerprint(IEnumerable<string>? toolIds)
    {
        if (toolIds is null)
            return null;

        var ordered = toolIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        if (ordered.Length == 0)
            return null;

        return Sha256Hex(string.Join('\u001f', ordered));
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
        string? skillManifestHash = null,
        string? permissionFingerprint = null,
        string? canonicalSystemPrefixHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var state = _sessions.GetOrAdd(sessionId, static _ => new SessionState());
        return state.Observe(systemPromptHash, toolSpecHash, permissionEpoch, permissionFingerprint);
    }

    private sealed class SessionState
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _versions = new(StringComparer.Ordinal);
        private int _nextVersion = 1;
        private string _lastSystemPromptHash = string.Empty;
        private string _lastToolSpecHash = string.Empty;
        private string? _lastPermissionFingerprint;
        private int _permissionEpoch;
        private bool _hasLast;

        public CompositionObservation Observe(
            string systemPromptHash,
            string toolSpecHash,
            int permissionEpoch,
            string? permissionFingerprint)
        {
            lock (_gate)
            {
                var changeReason = DetectChangeReason(systemPromptHash, toolSpecHash);

                // P0-5 step 4c：权限/工具授权指纹检测。
                // 指纹非空且与上次不同 → 权限纪元 +1（显式传入 epoch 作为下限），并上报 permission_changed。
                var permissionChanged = permissionFingerprint is not null
                    && _hasLast
                    && !string.Equals(_lastPermissionFingerprint, permissionFingerprint, StringComparison.Ordinal);
                if (permissionChanged)
                {
                    _permissionEpoch = Math.Max(_permissionEpoch + 1, permissionEpoch);
                    changeReason = AppendChangeReason(changeReason, "permission_changed");
                }
                else
                {
                    // 无指纹或指纹未变：显式传入 epoch 作为基准（保持向后兼容）。
                    _permissionEpoch = Math.Max(_permissionEpoch, permissionEpoch);
                }
                if (permissionFingerprint is not null)
                    _lastPermissionFingerprint = permissionFingerprint;

                var key = systemPromptHash + "\u001f" + toolSpecHash;
                int version;
                if (permissionChanged && _versions.TryGetValue(key, out var existing))
                {
                    // P0-5 step 4c：权限变化必须开新版本（即使 hash 组合复用），
                    // 否则写穿因版本号不变被抑制，PermissionEpoch 无法持久化。
                    version = _nextVersion++;
                    _versions[key] = version;
                }
                else if (!_versions.TryGetValue(key, out version))
                {
                    version = _nextVersion++;
                    _versions[key] = version;
                }

                _lastSystemPromptHash = systemPromptHash;
                _lastToolSpecHash = toolSpecHash;
                _hasLast = true;

                return new CompositionObservation(version, changeReason, _permissionEpoch);
            }
        }

        private static string AppendChangeReason(string current, string extra)
            => current is "none" or "initial" ? extra : current + "," + extra;

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
