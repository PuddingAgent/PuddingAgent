using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Services.CodeIndex;

/// <summary>
/// 默认的磁盘枚举实现（D2 三源里的「元数据扫描」）：只读元数据，不读正文、不写任何东西。
/// <para>
/// 设计要点：
/// <list type="bullet">
///   <item><description><b>根先探测</b>：根缺失/不可读时返回 <c>RootUsable=false</c>，
///     调用方据此绝不会把任何路径判成删除。</description></item>
///   <item><description><b>目录被忽略即整棵子树不枚举</b>：忽略规则由宿主的
///     <see cref="ICodeSourceIgnoreRules"/> 注入，组件内不另建白名单。</description></item>
///   <item><description><b>子树失败 ⇒ 本轮不完整</b>：某个目录读不到只把 <c>Complete</c> 置 false 并给出原因，
///     不会假装「那里没有文件」（后者会导致误删除）。</description></item>
///   <item><description><b>有界</b>：条目数上限只是安全阀，触顶同样把 <c>Complete</c> 置 false，
///     让下一轮继续而不是把未枚举的路径当删除。</description></item>
///   <item><description><b>stat 读不到就是 null</b>：不顶替、不猜测。</description></item>
///   <item><description><b>确定性顺序</b>：按组件路径身份排序，便于测试与稳定诊断。</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class FileSystemCodeSourceScanner : ICodeSourceScanner, ICodeSourcePathProbe
{
    /// <summary>默认条目上限：触顶即本轮不完整（不删任何东西）。</summary>
    public const int DefaultMaxEntries = 200_000;

    private readonly ICodeSourceIgnoreRules _ignoreRules;
    private readonly int _maxEntries;

    /// <summary>创建扫描器。</summary>
    /// <param name="ignoreRules">宿主注入的路径忽略规则（唯一真源在 PuddingPathFiltering）。</param>
    /// <param name="maxEntries">条目上限覆盖。</param>
    public FileSystemCodeSourceScanner(ICodeSourceIgnoreRules ignoreRules, int maxEntries = DefaultMaxEntries)
    {
        ArgumentNullException.ThrowIfNull(ignoreRules);

        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), maxEntries, "Entry limit must be positive.");

        _ignoreRules = ignoreRules;
        _maxEntries = maxEntries;
    }

    /// <inheritdoc />
    public Task<CodeSourceScanOutcome> ScanAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return Task.FromResult(new CodeSourceScanOutcome(
                [], RootUsable: false, Complete: false, CodeSourceScanReasons.RootPathMissing));
        }

        if (!Directory.Exists(rootPath))
        {
            return Task.FromResult(new CodeSourceScanOutcome(
                [], RootUsable: false, Complete: false, CodeSourceScanReasons.RootUnusable));
        }

        try
        {
            // 强制一次根枚举：未挂载的卷 / 改名后的共享 / 权限变化都可能「存在但读不了」。
            using var probe = Directory.EnumerateFileSystemEntries(rootPath).GetEnumerator();
            probe.MoveNext();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Task.FromResult(new CodeSourceScanOutcome(
                [], RootUsable: false, Complete: false, CodeSourceScanReasons.RootUnusable));
        }

        var entries = new List<CodeSourceDiskEntry>();
        var complete = true;
        string? incompleteReason = null;

        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = pending.Pop();

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                complete = false;
                incompleteReason ??= CodeSourceScanReasons.SubtreeUnreadable;
                continue;
            }

            var childList = new List<string>();
            try
            {
                foreach (var child in children)
                    childList.Add(child);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                complete = false;
                incompleteReason ??= CodeSourceScanReasons.SubtreeUnreadable;
            }

            childList.Sort(CodePathIdentity.PathComparer);

            foreach (var child in childList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var isDirectory = IsDirectory(child);

                if (SafeIsIgnored(child, isDirectory))
                {
                    // 被忽略的目录整棵子树不枚举；被忽略的文件不进候选。
                    continue;
                }

                if (isDirectory)
                {
                    pending.Push(child);
                    continue;
                }

                if (entries.Count >= _maxEntries)
                {
                    complete = false;
                    incompleteReason ??= CodeSourceScanReasons.EntryLimitReached;
                    break;
                }

                entries.Add(ReadEntry(child));
            }

            if (!complete && incompleteReason == CodeSourceScanReasons.EntryLimitReached)
                break;
        }

        entries.Sort((left, right) => CodePathIdentity.PathComparer.Compare(left.FilePath, right.FilePath));

        return Task.FromResult(new CodeSourceScanOutcome(entries, RootUsable: true, complete, incompleteReason));
    }

    /// <summary>
    /// 按路径观测（D4）：只给这批已知路径取元数据，**不遍历整棵树**。
    /// <para>
    /// 结果**必然不完整**（<c>Complete=false</c>）：没被问到的路径这一轮没被观测过，
    /// 因此永远不能据此得出「已删除」。目录提示会枚举其子树（目录变更可能影响它下面的文件）。
    /// </para>
    /// </summary>
    /// <inheritdoc />
    public Task<CodeSourceScanOutcome> ObserveAsync(
        IReadOnlyCollection<string> absolutePaths,
        CancellationToken cancellationToken = default)
    {
        var paths = (absolutePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(CodePathIdentity.PathComparer)
            .OrderBy(path => path, CodePathIdentity.PathComparer)
            .ToArray();

        if (paths.Length == 0)
            return Task.FromResult(new CodeSourceScanOutcome([], RootUsable: true, Complete: false, null));

        var entries = new List<CodeSourceDiskEntry>();
        string? incompleteReason = null;

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isDirectory = IsDirectory(path);

            if (SafeIsIgnored(path, isDirectory))
                continue;

            if (!isDirectory)
            {
                // 不存在的路径也要如实给出（元数据为 null）：判定层据此把它当候选并保守处理
                // （提示驱动的轮次里它只会被判成 Deferred，正式删除留给周期性完整扫描）。
                entries.Add(ReadEntry(path));
                continue;
            }

            // 目录提示：枚举其子树（目录变更可能影响批次从未提到的文件）。
            // 目录自己不是文件候选；它已经不在磁盘上时只记「本轮不完整」，删除由完整扫描确认。
            var pending = new Stack<string>();
            pending.Push(path);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var directory = pending.Pop();

                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateFileSystemEntries(directory);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    incompleteReason ??= CodeSourceScanReasons.SubtreeUnreadable;
                    continue;
                }

                var childList = new List<string>();
                try
                {
                    foreach (var child in children)
                        childList.Add(child);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    incompleteReason ??= CodeSourceScanReasons.SubtreeUnreadable;
                }

                childList.Sort(CodePathIdentity.PathComparer);

                foreach (var child in childList)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var childIsDirectory = IsDirectory(child);
                    if (SafeIsIgnored(child, childIsDirectory))
                        continue;

                    if (childIsDirectory)
                    {
                        pending.Push(child);
                        continue;
                    }

                    if (entries.Count >= _maxEntries)
                    {
                        incompleteReason = CodeSourceScanReasons.EntryLimitReached;
                        pending.Clear();
                        break;
                    }

                    entries.Add(ReadEntry(child));
                }
            }
        }

        entries.Sort((left, right) => CodePathIdentity.PathComparer.Compare(left.FilePath, right.FilePath));

        // 按路径观测**永远不完整**：没被问到的路径这一轮没有事实，绝不允许据此删除任何记录。
        return Task.FromResult(new CodeSourceScanOutcome(entries, RootUsable: true, Complete: false, incompleteReason));
    }

    private bool SafeIsIgnored(string path, bool isDirectory)
    {
        try
        {
            return _ignoreRules.IsIgnored(path, isDirectory);
        }
        catch (Exception)
        {
            // 忽略规则自身出错时按「不忽略」处理：多一个候选的代价是「核验一下」，
            // 而误忽略会真正漏掉文件 —— 两侧代价不对称，所以选保守的一侧。
            return false;
        }
    }

    private static bool IsDirectory(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 判不出来时按「不是目录」处理：它会被当作文件条目，stat 读不到就是 null，
            // 后续判定会保守地要求核验内容，不会静默漏掉。
            return false;
        }
    }

    private static CodeSourceDiskEntry ReadEntry(string path)
    {
        try
        {
            var info = new FileInfo(path);

            if (!info.Exists)
                return new CodeSourceDiskEntry(path, null, null);

            return new CodeSourceDiskEntry(
                path,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                info.Length);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new CodeSourceDiskEntry(path, null, null);
        }
    }
}
