using PuddingRetrieval.Contracts;
using PuddingRetrieval;

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

    /// <summary>
    /// 取该项目的根目录（注册表里的 <c>ProjectPath</c>）；项目无记录时返回 <c>null</c>。
    /// <para>
    /// <b>为什么需要它</b>：<see cref="SymbolRefV1.FilePath"/> 存的是**项目内相对路径**，
    /// 而工具拿到的是**工作区根**。用工作区根去拼，会得到一个仍在工作区内、
    /// 但指向不存在文件的路径 —— 边界检查照样通过，只在 <c>File.Exists</c> 处失败并报
    /// 「文件不存在」，让人以为是文件被删了。实测就踩过这一条。
    /// </para>
    /// <para>
    /// 实现方同样**不得猜**：查不到就返回 <c>null</c>，由调用方显式失败；
    /// 不要回落到工作区根 —— 那等于把另一个同名文件当成 ref 指向的文件。
    /// </para>
    /// </summary>
    Task<string?> GetProjectRootAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken);
}
