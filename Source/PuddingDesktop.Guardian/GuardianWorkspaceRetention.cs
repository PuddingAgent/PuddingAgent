namespace PuddingDesktop.Guardian;

/// <summary>
/// 隔离构建工作区回收。
///
/// <para>每次构建都会在 <c>temp/build/guardian/{guid}</c> 下新建一个工作区（见
/// <see cref="GuardianBuilder.PrepareAsync"/>），构建产物从那里复制到安装目录，之后工作区
/// 就没有用处了。此前没有任何回收逻辑，实测单个工作区约 1.6 GB，累积 39 个后占用 62.5 GB，
/// 属于会持续增长、最终耗尽磁盘的缺陷。</para>
///
/// <para>回收策略有两条硬约束：</para>
/// <list type="number">
/// <item>最近 <c>keepCount</c> 个一律保留——当前或上一轮部署的候选产物可能仍被引用，
/// 保留少量现场便于事后核对。</item>
/// <item>最后写入时间在 <c>minimumAge</c> 之内的目录不动——并用构建正在往那里写日志与产物，
/// 删掉会让部署中途失败。</item>
/// </list>
///
/// <para>回收是尽力而为：单个目录删不掉（被占用、权限不足）只跳过，不抛异常，
/// 不允许因为清理失败而让构建失败。</para>
/// </summary>
public static class GuardianWorkspaceRetention
{
    /// <summary>默认保留最近几个工作区。</summary>
    public const int DefaultKeepCount = 2;

    /// <summary>默认的“仍在写入”保护窗口。</summary>
    public static readonly TimeSpan DefaultMinimumAge = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 回收 <paramref name="root"/> 下的旧工作区，返回被删除的目录名。
    /// </summary>
    /// <param name="root">工作区的父目录，通常是 <c>temp/build/guardian</c>。</param>
    /// <param name="keepCount">保留最近几个（按最后写入时间倒序）。</param>
    /// <param name="minimumAge">比这个更“新”的目录不删；为空时用 <see cref="DefaultMinimumAge"/>。</param>
    /// <param name="now">当前时刻，仅测试需要注入；为空时取系统 UTC 时钟。</param>
    public static IReadOnlyList<string> Prune(
        string root,
        int keepCount = DefaultKeepCount,
        TimeSpan? minimumAge = null,
        DateTimeOffset? now = null)
    {
        var deleted = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return deleted;

        if (keepCount < 0)
            keepCount = 0;

        var age = minimumAge ?? DefaultMinimumAge;
        var reference = now ?? DateTimeOffset.UtcNow;

        var candidates = new DirectoryInfo(root)
            .EnumerateDirectories()
            .OrderByDescending(item => item.LastWriteTimeUtc)
            .ToList();

        for (var index = 0; index < candidates.Count; index++)
        {
            var directory = candidates[index];
            if (index < keepCount)
                continue;
            if (reference - directory.LastWriteTimeUtc < age)
                continue;

            try
            {
                directory.Delete(recursive: true);
                deleted.Add(directory.Name);
            }
            catch (IOException)
            {
                // 被占用：跳过，下一轮再试。
            }
            catch (UnauthorizedAccessException)
            {
                // 权限不足：跳过，不因清理失败而中断构建。
            }
        }

        return deleted;
    }
}
