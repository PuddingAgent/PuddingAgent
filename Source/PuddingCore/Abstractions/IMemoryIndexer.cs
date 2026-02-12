namespace PuddingCode.Abstractions;

/// <summary>
/// 记忆树索引器。
/// 将 Tag 层级路径（如 "preference/editor/font"）拆解为可检索的索引结构。
/// </summary>
public interface IMemoryIndexer
{
    /// <summary>
    /// 按层级 Tag 前缀搜索记忆。
    /// 输入 "preference/editor" 时，匹配该路径及其子路径。
    /// </summary>
    Task<IReadOnlyList<MemoryHit>> SearchByTagPrefixAsync(
        string workspaceId,
        string agentId,
        string tagPrefix,
        int topK = 20,
        CancellationToken ct = default);

    /// <summary>
    /// 获取 Tag 树某个父节点的直接子节点。
    /// parentTag 为 null 时，返回根节点下一级分类。
    /// </summary>
    Task<IReadOnlyList<TagTreeNode>> GetTagChildrenAsync(
        string workspaceId,
        string agentId,
        string? parentTag = null,
        CancellationToken ct = default);
}

/// <summary>Tag 树节点。</summary>
public sealed record TagTreeNode(string Tag, string Label, int Count, bool HasChildren);

/// <summary>记忆命中项。</summary>
public sealed record MemoryHit(string MemoryId, string Tag, string Content, double Score);
