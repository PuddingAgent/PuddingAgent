namespace PuddingRetrieval;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PuddingCodeIndex.Contracts;
using PuddingRetrieval.Contracts;

/// <summary>
/// 检索端口需要的最小数据面（ADR-089 §2.1 第 1 条：单次调用、多路融合、一次交付）。
/// <para>
/// <b>为什么单独抽这一层</b>：<see cref="IRetrievalPort"/> 的实现要做意图路由、去重、排序、
/// 预算与空原因裁决；这些逻辑都能用"两个方法"喂饱。把它直接绑在
/// <see cref="ICodeIndexStore"/>（30+ 成员）上，测试就得实现一整个 store，
/// 于是测试会退化成"能编译就算过"。窄端口让测试可写、让依赖方向显式。
/// </para>
/// </summary>
public interface IRetrievalSymbolSource
{
    /// <summary>列出该 scope 下的项目（用于判定索引可用性 + 生成索引版本）。</summary>
    Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(
        string scopeId,
        CancellationToken cancellationToken);

    /// <summary>按符号检索请求取符号。</summary>
    Task<IReadOnlyList<CodeSymbolRecord>> SearchSymbolsAsync(
        CodeSymbolSearchRequest request,
        CancellationToken cancellationToken);

    /// <summary>取单个符号（关系面用：把关系边上的 symbolId 解析成可描述的对端符号）。</summary>
    Task<CodeSymbolRecord?> GetSymbolAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CancellationToken cancellationToken);

    /// <summary>列出该项目下已索引的文件（文件面用：查询词匹配路径）。</summary>
    Task<IReadOnlyList<CodeFileRecord>> ListFilesAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken);

    /// <summary>指向该符号的关系边（谁调用了它 / 谁引用了它 / 谁实现了它）。</summary>
    Task<IReadOnlyList<CodeRelationRecord>> ListIncomingRelationsAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        IReadOnlyList<CodeRelationKind> relationKinds,
        CancellationToken cancellationToken);

    /// <summary>从该符号出发的关系边（它调用了谁）。</summary>
    Task<IReadOnlyList<CodeRelationRecord>> ListOutgoingRelationsAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        IReadOnlyList<CodeRelationKind> relationKinds,
        CancellationToken cancellationToken);
}

/// <summary>
/// 文本面数据源（注释面用）：把查询词匹配到文件行。
/// <para>
/// <b>为什么不直接依赖 PuddingFullTextIndex</b>：依赖方向必须由编译期强制 ——
/// 代码索引不该知道全文索引用的是 Lucene 还是别的。本接口只有“给定 scope + 查询词，
/// 返回文件行”，把 scopeId → 目录 的解析留给 Host。
/// </para>
/// </summary>
public interface IRetrievalTextSource
{
    /// <summary>在指定 scope 内检索文本，最多返回 <paramref name="maxMatches"/> 条文件行匹配。</summary>
    Task<IReadOnlyList<RetrievalTextMatch>> SearchAsync(
        string scopeId,
        string query,
        int maxMatches,
        CancellationToken cancellationToken);
}

/// <summary>文本面的一条匹配：文件 + 行号 + 该行文本。</summary>
public sealed record RetrievalTextMatch(string FilePath, int Line, string LineText);

/// <summary>
/// 文本数据面<b>不可用</b>（未建索引 / 引擎自身报错）。
/// <para>
/// <b>为什么需要这个异常</b>：文本面返回空列表有且只有一种含义——“查了，没匹配”。
/// “索引没建”必须能被区分出来，否则会被端口当成 <see cref="RetrievalEmptyReason.GenuinelyAbsent"/>，
/// 把“没法查”读成“确实没有”。端口捕获本异常后转成
/// <see cref="RetrievalEmptyReason.NotIndexed"/> + 重建建议；其他异常不捕获（不吞错）。
/// </para>
/// </summary>
public sealed class RetrievalTextSourceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
