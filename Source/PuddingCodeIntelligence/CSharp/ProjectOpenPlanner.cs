namespace PuddingCodeIntelligence.CSharp;

/// <summary>
/// 决定这一轮真正需要交给 <c>OpenProjectAsync</c> 的项目集合。
/// <para>
/// <b>为什么需要它</b>：MSBuild 在打开一个项目时，会把**它引用的项目作为依赖一并装载**。
/// 于是按 <c>.csproj</c> 列表逐个显式打开时，后出现的被引用项目其实已经在工作区里，
/// <c>OpenProjectAsync</c> 必然抛 <c>ArgumentException</c>（"…已经是工作区的一部分"）。
/// 那不是失败，却意味着**同一份 MSBuild 评估白做了一遍**：读入整套引用程序集、
/// 重新求值属性与目标。实测日志里该异常与"每轮 4 reindex × 几十个项目"叠加，
/// 直接表现为进程读几百 MB/s、CPU 常忙（2026-10-28 现场数据）。
/// </para>
/// <para>
/// 契约：<b>纯函数</b>，不碰文件系统、不依赖 Roslyn，只看两组路径字符串。
/// 这样才能在没有 MSBuild 的环境里用断言钉住。
/// </para>
/// </summary>
internal static class ProjectOpenPlanner
{
    /// <summary>
    /// 计算需要打开的项目路径（保持输入顺序）。
    /// </summary>
    /// <param name="candidateProjectPaths">候选 <c>.csproj</c> 路径，允许含重复。</param>
    /// <param name="alreadyLoadedProjectPaths">
    /// 工作区当前已装载项目的 <c>FilePath</c>（可为 <c>null</c>，表示未知）。
    /// </param>
    /// <returns>去重、且剔除"已作为依赖装载"之后仍需显式打开的项目。</returns>
    public static IReadOnlyList<string> PlanOpenOrder(
        IEnumerable<string>? candidateProjectPaths,
        IEnumerable<string?>? alreadyLoadedProjectPaths)
    {
        var loaded = new HashSet<string>(PathComparer);
        if (alreadyLoadedProjectPaths is not null)
        {
            foreach (var loadedPath in alreadyLoadedProjectPaths)
            {
                if (string.IsNullOrWhiteSpace(loadedPath))
                    continue;
                loaded.Add(Normalize(loadedPath));
            }
        }

        var planned = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        if (candidateProjectPaths is null)
            return planned;

        foreach (var candidate in candidateProjectPaths)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var key = Normalize(candidate);

            // ① 同一路径在候选里出现多次：只打开一次。
            if (!seen.Add(key))
                continue;

            // ② 已作为别的项目的依赖装载：显式打开必定抛 ArgumentException，跳过。
            if (loaded.Contains(key))
                continue;

            planned.Add(candidate);
        }

        return planned;
    }

    /// <summary>
    /// 路径比较口径。<b>必须是大小写不敏感</b>：Windows 上
    /// <c>Source\Foo\Foo.csproj</c> 与 <c>source\foo\foo.csproj</c> 是同一个文件，
    /// 用序数比较会把同一个项目当成两个，去重直接失效（静默，不报错）。
    /// </summary>
    private static StringComparer PathComparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// 归一化为可比较的键：相对路径转绝对、统一目录分隔符、去掉尾随分隔符。
    /// 归一化失败（非法路径）时退回原始字符串——宁可不去重，也不要因为一条坏路径
    /// 把整批项目丢掉。
    /// </summary>
    private static string Normalize(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            full = full.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar);
            return trimmed.Length == 0 ? full : trimmed;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
