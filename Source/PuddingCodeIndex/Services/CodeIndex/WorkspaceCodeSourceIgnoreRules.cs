using System.Collections.Concurrent;
using PuddingCodeIndex.Contracts;
using PuddingPathFiltering;

namespace PuddingCodeIndex.Services.CodeIndex;

/// <summary>
/// 把路径忽略的**唯一真源**（叶子组件 <c>PuddingPathFiltering</c>）适配成
/// <see cref="ICodeSourceIgnoreRules"/>（D4，2026-10-02）：名字级噪声名单 + 真实 .gitignore 忽略栈。
/// <para>
/// 为什么要适配而不是在组件里另写一份：忽略名单（52 个噪声目录名 / 6 个文件名）与 .gitignore 语义
/// （负向规则、目录尾斜杠、大小写折叠）都与 <c>git check-ignore</c> 逐条对齐过，复刻一份必然会漂移。
/// </para>
/// <para>
/// 缓存：忽略栈按**仓库根**缓存（同一仓库的多个 scope 不重复读盘），并按
/// <see cref="DefaultRefreshInterval"/> 过期重建 —— 用户新写的 .gitignore 不需要重启就能生效。
/// 名字级噪声名单是纯逻辑，不进缓存。
/// </para>
/// </summary>
public sealed class WorkspaceCodeSourceIgnoreRules : ICodeSourceIgnoreRules
{
    /// <summary>忽略栈的缓存有效期。</summary>
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, CachedFilter> _filters =
        new(CodePathIdentity.PathComparer);

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _refreshInterval;

    /// <summary>创建适配器。</summary>
    /// <param name="timeProvider">可选时钟（确定性测试）。</param>
    /// <param name="refreshInterval">忽略栈缓存有效期覆盖。</param>
    public WorkspaceCodeSourceIgnoreRules(TimeProvider? timeProvider = null, TimeSpan? refreshInterval = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _refreshInterval = refreshInterval ?? DefaultRefreshInterval;

        if (_refreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(refreshInterval), _refreshInterval, "Refresh interval must be positive.");
        }
    }

    /// <summary>当前缓存了几个仓库根的忽略栈（诊断用）。</summary>
    public int CachedRepositoryCount => _filters.Count;

    /// <inheritdoc />
    public bool IsIgnored(string absolutePath, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return false;

        var repositoryRoot = ResolveRepositoryRoot(absolutePath, isDirectory);
        if (repositoryRoot is null)
            return false;

        // ⚠️ 必须用「相对仓库根」的判定：名字级噪声是**按路径段**匹配的，
        // 拿绝对路径去判会把工作区自己的祖先目录名当成噪声 —— 例如仓库检出在
        // <c>…\Temp\…</c>、<c>…\build\…</c>、<c>…\Debug\…</c> 之下时，
        // 整棵工作区都会被判成噪声（等于索引悄悄变空）。这条规则由叶子组件提供。
        if (IndexExcludePatterns.IsNoisePathBelow(repositoryRoot, absolutePath))
            return true;

        var filter = GetFilter(repositoryRoot);
        var relativePath = Path.GetRelativePath(repositoryRoot, absolutePath);

        return filter.IsExcluded(relativePath, isDirectory);
    }

    private static string? ResolveRepositoryRoot(string absolutePath, bool isDirectory)
    {
        try
        {
            var startDirectory = isDirectory
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolutePath))
                : Path.GetDirectoryName(Path.GetFullPath(absolutePath));

            return string.IsNullOrEmpty(startDirectory)
                ? null
                : IndexExcludePatterns.ResolveRepositoryRoot(startDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private WorkspacePathFilter GetFilter(string repositoryRoot)
    {
        var now = _timeProvider.GetUtcNow();

        if (_filters.TryGetValue(repositoryRoot, out var cached)
            && now - cached.LoadedAtUtc < _refreshInterval)
        {
            return cached.Filter;
        }

        // 仓库范围内的 .gitignore 全部生效：以仓库根为基准加载并遍历到根（walkScopeDirectory = 根）。
        var filter = new WorkspacePathFilter(
            GitIgnoreFileLoader.Load(repositoryRoot, IndexExcludePatterns.GitIgnoreCaseFolding, repositoryRoot));

        _filters[repositoryRoot] = new CachedFilter(filter, now);
        return filter;
    }

    private sealed record CachedFilter(WorkspacePathFilter Filter, DateTimeOffset LoadedAtUtc);
}
