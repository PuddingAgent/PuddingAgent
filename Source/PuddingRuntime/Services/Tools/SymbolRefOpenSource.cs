using PuddingCodeIndex.Contracts.Retrieval;
using PuddingCodeIndex.Services.Retrieval;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// `symbol_ref` 判定所需的两个事实来源（symbol_ref 提案 §5）。
/// <para>
/// <b>为什么拆成端口而不是直接查库</b>：<see cref="SymbolRefFreshness"/> 是纯判定函数，
/// 它需要的只有"当前文件的 content_hash"和"当前版本里同 id 的位置"。把这两件事做成端口，
/// 判定逻辑与索引实现解耦 —— 判定能在组件测试里穷举，索引侧换实现不影响语义。
/// </para>
/// <para>
/// <b>实现方必须如实回答</b>：文件未索引时 <see cref="GetFileContentHashAsync"/> 返回
/// <c>null</c>，不得用"空串"或"当前磁盘 hash"顶替 —— 后者会让陈旧 ref 被误判成 Fresh。
/// </para>
/// </summary>
public interface ISymbolRefSource
{
    /// <summary>取索引中该文件的 content_hash；文件不在索引内时返回 <c>null</c>。</summary>
    Task<string?> GetFileContentHashAsync(
        string workspaceId,
        string projectId,
        string filePath,
        CancellationToken cancellationToken);

    /// <summary>在当前版本中按 symbol_id 找位置；零命中返回空列表（不抛异常）。</summary>
    Task<IReadOnlyList<SymbolRefRelocation>> FindSymbolCandidatesAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CancellationToken cancellationToken);
}
