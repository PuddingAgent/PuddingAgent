namespace PuddingMemoryEngine;

/// <summary>
/// 记忆边界策略——决定某来源是否有权写入 Workspace 级记忆。
/// V1：白名单机制，未列入白名单的来源只能写 Session 级记忆。
/// </summary>
public sealed class MemoryBoundaryService
{
    // 允许写入 Workspace 记忆的来源集合（agentInstanceId 或 "system"）
    private readonly HashSet<string> _trustedSources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>注册受信任来源。</summary>
    public void Trust(string source) => _trustedSources.Add(source);

    /// <summary>移除受信任来源。</summary>
    public void Revoke(string source) => _trustedSources.Remove(source);

    /// <summary>
    /// 判断指定来源是否能写入 Workspace 级记忆。
    /// "system" 始终被信任。
    /// </summary>
    public bool CanWriteWorkspace(string source) =>
        source.Equals("system", StringComparison.OrdinalIgnoreCase)
        || _trustedSources.Contains(source);
}
