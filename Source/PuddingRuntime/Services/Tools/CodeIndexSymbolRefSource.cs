using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Contracts.Retrieval;
using PuddingCodeIndex.Services.Retrieval;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// <see cref="ISymbolRefSource"/> 的索引侧实现：content_hash 取自源清单（manifest），
/// 候选位置取自索引中的符号行。
/// <para>
/// <b>为什么 hash 来自 manifest 而不是磁盘</b>：§8.3 要求 ref 绑定的是**索引时**的源版本。
/// 用当前磁盘 hash 去比，会把"索引落后于磁盘"的窗口误判成 Fresh，进而按旧行号打开新代码。
/// manifest 里没有该文件 ⇒ 返回 <c>null</c>（无法证明一致），由
/// <see cref="SymbolRefFreshness"/> 判成 Stale。
/// </para>
/// <para>
/// <b>性能已知代价</b>：<see cref="ICodeSourceMaintenanceStore.LoadSourceMaintenanceAsync"/>
/// 每次返回整个 scope 的 manifest，单点查询也要走 O(文件数)。v1 接受（ref 打开不是热路径），
/// 后续若成为瓶颈，应在存储侧加"按路径取指纹"的定向查询，而不是在调用方缓存
/// —— 缓存会引入新的陈旧窗口。
/// </para>
/// </summary>
public sealed class CodeIndexSymbolRefSource(
    ICodeIndexStore symbolStore,
    ICodeSourceMaintenanceStore? maintenanceStore) : ISymbolRefSource
{
    /// <inheritdoc />
    public async Task<string?> GetFileContentHashAsync(
        string workspaceId,
        string projectId,
        string filePath,
        CancellationToken cancellationToken)
    {
        if (maintenanceStore is null || string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var snapshot = await maintenanceStore
            .LoadSourceMaintenanceAsync(workspaceId, projectId, cancellationToken)
            .ConfigureAwait(false);

        var wanted = SymbolIdentity.NormalizePath(filePath);
        foreach (var (path, entry) in snapshot.Manifest)
        {
            if (string.Equals(SymbolIdentity.NormalizePath(path), wanted, StringComparison.Ordinal))
            {
                var hash = entry.Fingerprint.ContentHash;
                return string.IsNullOrWhiteSpace(hash) ? null : hash;
            }
        }

        // 文件不在 manifest 中 ⇒ 无法证明与 ref 记录的版本一致，必须如实返回 null。
        return null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SymbolRefRelocation>> FindSymbolCandidatesAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbolId))
        {
            return Array.Empty<SymbolRefRelocation>();
        }

        var symbol = await symbolStore
            .GetSymbolAsync(workspaceId, projectId, symbolId, cancellationToken)
            .ConfigureAwait(false);

        return symbol is null
            ? Array.Empty<SymbolRefRelocation>()
            : new[] { new SymbolRefRelocation(symbol.FilePath, symbol.StartLine, symbol.EndLine) };
    }
}
