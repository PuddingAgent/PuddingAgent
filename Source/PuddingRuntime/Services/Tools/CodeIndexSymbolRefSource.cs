using PuddingCodeIndex.Contracts;
using PuddingRetrieval.Contracts;
using PuddingCodeIndex.Services.Retrieval;
using PuddingRetrieval;

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
/// <b>两种路径形态都要认</b>：ref 里存的是**项目内相对路径**（见 <see cref="SymbolRefV1"/>），
/// 而 manifest 在 SQLite 里落的是**绝对路径**（实测 <c>CodeSourceManifest.FilePath</c>）。
/// 只比相对路径会让每一个 ref 都判成 Stale —— 那不是"保守"，而是让整条 ref 通道死掉，
/// 而且它不报错、只返回 <c>null</c>，看起来完全正常。所以两种形态都构造出来逐一比对：
/// 命中任一即为命中，都没命中才是真的不知道。
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

        var keys = await BuildLookupKeysAsync(workspaceId, projectId, filePath, cancellationToken)
            .ConfigureAwait(false);

        var snapshot = await maintenanceStore
            .LoadSourceMaintenanceAsync(workspaceId, projectId, cancellationToken)
            .ConfigureAwait(false);

        foreach (var (path, entry) in snapshot.Manifest)
        {
            if (!keys.Contains(SymbolIdentity.NormalizePath(path)))
            {
                continue;
            }

            var hash = entry.Fingerprint.ContentHash;
            return string.IsNullOrWhiteSpace(hash) ? null : hash;
        }

        // 两种形态都没命中 ⇒ 无法证明与 ref 记录的版本一致，必须如实返回 null。
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

    /// <inheritdoc />
    public async Task<string?> GetProjectRootAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        var project = await symbolStore
            .GetProjectAsync(workspaceId, projectId, cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(project?.ProjectPath) ? null : project.ProjectPath;
    }

    /// <summary>
    /// 构造 manifest 比对用的键集合：ref 给的相对路径本身，以及拼上项目根后的绝对路径。
    /// <para>
    /// 项目记录缺失时**不猜**：只保留原样键，让调用方得到 <c>null</c>（判 Stale），
    /// 而不是拿一个可疑路径去撞运气。拼不出绝对路径也不报错 —— 相对形态可能本来就命中。
    /// </para>
    /// </summary>
    private async Task<HashSet<string>> BuildLookupKeysAsync(
        string workspaceId,
        string projectId,
        string filePath,
        CancellationToken cancellationToken)
    {
        var normalized = SymbolIdentity.NormalizePath(filePath);
        var keys = new HashSet<string>(StringComparer.Ordinal) { normalized };

        if (Path.IsPathRooted(normalized))
        {
            return keys;
        }

        var project = await symbolStore
            .GetProjectAsync(workspaceId, projectId, cancellationToken)
            .ConfigureAwait(false);

        if (project is null || string.IsNullOrWhiteSpace(project.ProjectPath))
        {
            return keys;
        }

        keys.Add(SymbolIdentity.NormalizePath(Path.Combine(project.ProjectPath, normalized)));
        return keys;
    }
}
