using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PuddingCodeIndex.Contracts;
using PuddingRetrieval;
using PuddingRetrieval.Contracts;

namespace PuddingRetrieval.Adapters;

/// <summary>
/// 把既有 <see cref="ICodeIndexStore"/> 适配成窄端口（生产路径唯一实现）。
/// <para>
/// <b>这是全仓唯一同时看见「检索」与「存储」的类型</b>。适配器的职责只有一个：逐方法转发，
/// 不改语义、不加缓存、不吞异常。任何「顺手优化」都会让窄端口的含义与存储的真实行为脱节，
/// 而那种偏差在检索侧看起来像是索引数据错了。
/// </para>
/// <para>
/// 两个方法做了<b>参数名对齐</b>：<see cref="ICodeIndexStore"/> 上同时存在
/// <c>relationKind</c>（单个，旧）与 <c>relationKinds</c>（集合）两个可选参数，
/// 这里显式指定 <c>relationKind: null</c>，避免调用方语义被默认值意外覆盖。
/// </para>
/// </summary>
public sealed class CodeIndexStoreSymbolSource(ICodeIndexStore store) : IRetrievalSymbolSource
{
    private readonly ICodeIndexStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc />
    public Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(
        string scopeId,
        CancellationToken cancellationToken) =>
        _store.ListProjectsAsync(scopeId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<CodeSymbolRecord>> SearchSymbolsAsync(
        CodeSymbolSearchRequest request,
        CancellationToken cancellationToken) =>
        _store.SearchSymbolsAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<CodeSymbolRecord?> GetSymbolAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CancellationToken cancellationToken) =>
        _store.GetSymbolAsync(workspaceId, projectId, symbolId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<CodeFileRecord>> ListFilesAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken) =>
        _store.ListFilesAsync(workspaceId, projectId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<CodeRelationRecord>> ListIncomingRelationsAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        IReadOnlyList<CodeRelationKind> relationKinds,
        CancellationToken cancellationToken) =>
        _store.ListIncomingRelationsAsync(
            workspaceId,
            projectId,
            symbolId,
            relationKind: null,
            relationKinds: relationKinds,
            cancellationToken: cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<CodeRelationRecord>> ListOutgoingRelationsAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        IReadOnlyList<CodeRelationKind> relationKinds,
        CancellationToken cancellationToken) =>
        _store.ListRelationsAsync(
            workspaceId,
            projectId,
            symbolId,
            relationKind: null,
            relationKinds: relationKinds,
            cancellationToken: cancellationToken);
}
