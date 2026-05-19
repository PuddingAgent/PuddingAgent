using System.Collections.Concurrent;
using PuddingCode.SubAgents;

namespace PuddingCode.Agents;

/// <summary>
/// IAgentWorkspaceGuard 实现 — 基于 SubAgentPermissions 的 glob 规则进行路径和工具权限检查。
/// 使用 AgentProfileProvider 加载 agent 特定权限，结合默认规则进行判定。
/// 规则优先级：Deny > Allow > default Deny。
/// </summary>
public sealed class AgentWorkspaceGuard : IAgentWorkspaceGuard
{
    private readonly AgentProfileProvider _profileProvider;
    private readonly ConcurrentDictionary<string, SubAgentPermissions> _cache = new();

    /// <summary>默认拒绝的 glob 模式（优先级最高，相对 workspace root）</summary>
    private static readonly string[] s_defaultDeny = ["../**", "data/config/**", "data/databases/**"];

    /// <summary>默认允许写入的 glob 模式（相对 workspace root）</summary>
    private static readonly string[] s_defaultAllowWrite = ["**"];

    /// <summary>默认允许读取的 glob 模式（相对 workspace root）</summary>
    private static readonly string[] s_defaultAllowRead = ["**"];

    public AgentWorkspaceGuard(AgentProfileProvider profileProvider)
    {
        _profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
    }

    /// <inheritdoc/>
    public WorkspaceGuardDecision CanRead(string agentInstanceId, string workspaceRoot, string path)
    {
        var permissions = GetPermissions(agentInstanceId);
        var relativePath = GetRelativePath(workspaceRoot, path);

        // 路径穿越检查
        if (relativePath.Contains(".."))
            return WorkspaceGuardDecision.Deny("Path traversal detected (contains '..')", "../**");

        // Deny 规则优先
        var denyPatterns = permissions.Filesystem.Deny.Count > 0
            ? NormalizePatterns(permissions.Filesystem.Deny)
            : new List<string>(s_defaultDeny);

        foreach (var pattern in denyPatterns)
        {
            if (MatchGlob(relativePath, pattern))
                return WorkspaceGuardDecision.Deny(
                    $"Path '{relativePath}' matches deny pattern '{pattern}'", pattern);
        }

        // Allow 规则
        var allowPatterns = permissions.Filesystem.Read.Count > 0
            ? NormalizePatterns(permissions.Filesystem.Read)
            : new List<string>(s_defaultAllowRead);

        foreach (var pattern in allowPatterns)
        {
            if (MatchGlob(relativePath, pattern))
                return WorkspaceGuardDecision.Allow();
        }

        return WorkspaceGuardDecision.Deny(
            $"Path '{relativePath}' does not match any allow pattern");
    }

    /// <inheritdoc/>
    public WorkspaceGuardDecision CanWrite(string agentInstanceId, string workspaceRoot, string path)
    {
        var permissions = GetPermissions(agentInstanceId);
        var relativePath = GetRelativePath(workspaceRoot, path);

        // 路径穿越检查
        if (relativePath.Contains(".."))
            return WorkspaceGuardDecision.Deny("Path traversal detected (contains '..')", "../**");

        // Deny 规则优先
        var denyPatterns = permissions.Filesystem.Deny.Count > 0
            ? NormalizePatterns(permissions.Filesystem.Deny)
            : new List<string>(s_defaultDeny);

        foreach (var pattern in denyPatterns)
        {
            if (MatchGlob(relativePath, pattern))
                return WorkspaceGuardDecision.Deny(
                    $"Path '{relativePath}' matches deny pattern '{pattern}'", pattern);
        }

        // Allow 规则
        var allowPatterns = permissions.Filesystem.Write.Count > 0
            ? NormalizePatterns(permissions.Filesystem.Write)
            : new List<string>(s_defaultAllowWrite);

        foreach (var pattern in allowPatterns)
        {
            if (MatchGlob(relativePath, pattern))
                return WorkspaceGuardDecision.Allow();
        }

        return WorkspaceGuardDecision.Deny(
            $"Path '{relativePath}' does not match any allow write pattern");
    }

    /// <inheritdoc/>
    public WorkspaceGuardDecision CanExecuteTool(string agentInstanceId, string toolId)
    {
        var permissions = GetPermissions(agentInstanceId);

        // Deny 优先
        if (permissions.Tools.Deny.Count > 0)
        {
            foreach (var pattern in permissions.Tools.Deny)
            {
                if (MatchGlob(toolId, pattern))
                    return WorkspaceGuardDecision.Deny(
                        $"Tool '{toolId}' is denied by pattern '{pattern}'", pattern);
            }
        }

        // Allow 规则
        if (permissions.Tools.Allow.Count > 0)
        {
            foreach (var pattern in permissions.Tools.Allow)
            {
                if (MatchGlob(toolId, pattern))
                    return WorkspaceGuardDecision.Allow();
            }
        }

        // 没有配置 Allow 规则时默认允许所有工具
        if (permissions.Tools.Allow.Count == 0)
            return WorkspaceGuardDecision.Allow();

        return WorkspaceGuardDecision.Deny(
            $"Tool '{toolId}' is not in the allow list");
    }

    /// <summary>
    /// 获取 agent 的权限配置，优先从 AgentProfileProvider 加载，失败时返回默认权限。
    /// 结果会被缓存。
    /// </summary>
    private SubAgentPermissions GetPermissions(string agentInstanceId)
    {
        if (_cache.TryGetValue(agentInstanceId, out var cached))
            return cached;

        // 尝试从 AgentProfileProvider 加载完整 profile 并提取权限
        // 使用同步等待 — permissions.json 是小文件，加载很快
        try
        {
            var profile = _profileProvider.LoadAsync(agentInstanceId).GetAwaiter().GetResult();
            _cache[agentInstanceId] = profile.Permissions;
            return profile.Permissions;
        }
        catch
        {
            // 加载失败时使用默认权限（保守策略：默认 deny config/databases）
            var defaults = new SubAgentPermissions();
            _cache[agentInstanceId] = defaults;
            return defaults;
        }
    }

    /// <summary>
    /// 将 SubAgentPermissions 中的模式（相对 agent data root）转换为相对 workspace root 的模式。
    /// 例如 "workspace/**" → "**"，"workspace/subdir/**" → "subdir/**"。
    /// 不以 "workspace/" 开头的模式保持不变（如 "../**"、"data/config/**"）。
    /// </summary>
    private static List<string> NormalizePatterns(List<string> patterns)
    {
        var normalized = new List<string>(patterns.Count);
        foreach (var pattern in patterns)
        {
            if (pattern.StartsWith("workspace/", StringComparison.OrdinalIgnoreCase))
                normalized.Add(pattern["workspace/".Length..]);
            else
                normalized.Add(pattern);
        }
        return normalized;
    }

    /// <summary>
    /// 计算相对于 workspace 根目录的路径。
    /// </summary>
    private static string GetRelativePath(string workspaceRoot, string path)
    {
        var normalizedRoot = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 如果路径在 workspace 外，返回以 ../ 开头的相对路径
        if (!normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return $"../{Path.GetFileName(normalizedPath)}";
        }

        if (normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return ".";

        return normalizedPath[(normalizedRoot.Length + 1)..]
            .Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// 简单的 wildcard glob 匹配：** 匹配任意深度，* 匹配单层内任意字符（不含 /），? 匹配单个字符（不含 /）。
    /// </summary>
    public static bool MatchGlob(string input, string pattern)
    {
        return MatchGlobSpan(input.AsSpan(), pattern.AsSpan());
    }

    private static bool MatchGlobSpan(ReadOnlySpan<char> input, ReadOnlySpan<char> pattern)
    {
        // 使用迭代栈避免递归深度问题
        var inputIdx = 0;
        var patternIdx = 0;
        var starIdx = -1;
        var matchIdx = 0;

        while (inputIdx < input.Length)
        {
            if (patternIdx < pattern.Length &&
                (pattern[patternIdx] == '?' ||
                 char.ToLowerInvariant(input[inputIdx]) == char.ToLowerInvariant(pattern[patternIdx])))
            {
                inputIdx++;
                patternIdx++;
            }
            // ** 匹配：匹配 0 个或多个目录层级
            else if (patternIdx < pattern.Length - 1 &&
                     pattern[patternIdx] == '*' && pattern[patternIdx + 1] == '*')
            {
                // 跳过 ** 和可能紧随的 /
                patternIdx += 2;
                if (patternIdx < pattern.Length && pattern[patternIdx] == '/')
                    patternIdx++;

                if (patternIdx >= pattern.Length)
                    return true; // ** 在末尾，匹配所有剩余路径

                starIdx = patternIdx;
                matchIdx = inputIdx;
            }
            // * 匹配：匹配单层内任意字符（不含 /）
            else if (patternIdx < pattern.Length && pattern[patternIdx] == '*')
            {
                // 跳过 *
                patternIdx++;
                // 如果 * 后是 / 或结束，匹配当前层级到下一个 / 或结束
                if (patternIdx >= pattern.Length || pattern[patternIdx] == '/')
                {
                    // 在当前层级内匹配任意字符直到 / 或结束
                    while (inputIdx < input.Length && input[inputIdx] != '/')
                        inputIdx++;
                    if (patternIdx < pattern.Length && pattern[patternIdx] == '/')
                        patternIdx++;
                }
                else
                {
                    // * 后面跟着其他字符，需要推进匹配
                    starIdx = patternIdx - 1;
                    matchIdx = inputIdx;
                }
            }
            else if (starIdx >= 0)
            {
                // 回溯到上一个 ** 位置
                patternIdx = starIdx;
                matchIdx++;
                inputIdx = matchIdx;
            }
            else
            {
                return false;
            }
        }

        // 跳过末尾的 ** 和 *
        while (patternIdx < pattern.Length)
        {
            if (patternIdx < pattern.Length - 1 &&
                pattern[patternIdx] == '*' && pattern[patternIdx + 1] == '*')
            {
                patternIdx += 2;
                if (patternIdx < pattern.Length && pattern[patternIdx] == '/')
                    patternIdx++;
            }
            else if (patternIdx < pattern.Length && pattern[patternIdx] == '*')
            {
                patternIdx++;
            }
            else
            {
                break;
            }
        }

        return patternIdx >= pattern.Length;
    }
}
