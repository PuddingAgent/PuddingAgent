using PuddingPathFiltering;

namespace PuddingCodeIndex.Services;

/// <summary>
/// 索引侧路径排除的<b>唯一入口</b>（ADR-089 U4-4 D4）。
/// <para>
/// 名单与判定逻辑都属于叶子组件 <see cref="PathNoiseRules"/> / <see cref="WorkspacePathFilter"/>，
/// 这里只是薄适配器：保留既有类型名与成员名，避免与本切片无关的重构。
/// </para>
/// <para>
/// <b>U4-4 删除了什么</b>：旧版本在这里有一份自实现的 .gitignore 解析
/// （<c>CollectGitIgnorePatterns</c> / <c>ParseGitIgnoreFile</c> / <c>MatchesGitIgnore</c> /
/// <c>ShouldExclude</c>，见 ADR-089 §2.1）。它既<b>从未被调用</b>（全仓零调用点），语义也不正确
/// （注释自述 <c>Skip negation rules</c>，且用 <c>EndsWith</c>/<c>Contains</c> 近似匹配通配符）。
/// 现在由 <see cref="GitIgnoreFileLoader"/> + <see cref="IgnoreStack"/> 提供与 <c>git check-ignore</c>
/// 逐条一致的真实实现，并被 <c>CodeIndexWatcher</c> 实际使用。
/// </para>
/// </summary>
public static class IndexExcludePatterns
{
    /// <summary>噪声目录名（单一真源：<see cref="PathNoiseRules.DirectoryNames"/>，52 项）。</summary>
    public static IReadOnlySet<string> NoiseDirNames => PathNoiseRules.DirectoryNames;

    /// <summary>噪声文件名（单一真源：<see cref="PathNoiseRules.FileNames"/>，6 项）。</summary>
    public static IReadOnlySet<string> NoiseFileNames => PathNoiseRules.FileNames;

    /// <summary>
    /// gitignore 匹配是否大小写折叠。Windows 上 git 的 <c>core.ignoreCase</c> 默认为 <c>true</c>
    /// （本机实测：<c>caseprobe.txt</c> 命中 <c>CASEPROBE.TXT</c>），因此这里跟随平台。
    /// </summary>
    public static bool GitIgnoreCaseFolding { get; } = OperatingSystem.IsWindows();

    /// <summary>相对扫描根的路径是否落在噪声目录/文件名下（含最后一段）。</summary>
    public static bool IsNoisePath(string filePath) => PathNoiseRules.IsNoisePath(filePath);

    /// <summary>裸目录名/文件名是否为噪声名（供只能拿到名字的遍历器使用）。</summary>
    public static bool IsNoiseDirectoryName(string directoryName) => PathNoiseRules.IsNoiseSegment(directoryName);

    /// <summary>
    /// 把 <paramref name="filePath"/> 折算成相对扫描根 <paramref name="scanRoot"/> 后再做噪声判定。
    /// 握有绝对路径时<b>必须</b>用它（否则工作区自己所在的目录名会被当成噪声），见
    /// <see cref="PathNoiseRules.IsNoisePathBelow"/>。
    /// </summary>
    public static bool IsNoisePathBelow(string? scanRoot, string? filePath)
        => PathNoiseRules.IsNoisePathBelow(scanRoot, filePath);

    /// <summary>
    /// 为一次索引扫描构造路径过滤器 = 名字级噪声名单 + 真实 .gitignore 忽略栈。
    /// <paramref name="scopeRoot"/> 之内（含）的 .gitignore 才生效，基准目录相对
    /// <paramref name="repositoryRoot"/>（见 <see cref="GitIgnoreFileLoader.Load"/>）。
    /// </summary>
    public static WorkspacePathFilter CreateWorkspaceFilter(string repositoryRoot, string scopeRoot)
        => new(GitIgnoreFileLoader.Load(repositoryRoot, GitIgnoreCaseFolding, scopeRoot));

    /// <summary>
    /// 从 <paramref name="startDirectory"/> 逐级向上寻找含 <c>.git</c> 的目录（git 仓库根）；
    /// 找不到（或该目录是裸仓库的工作树之外）时返回 <paramref name="startDirectory"/> 本身。
    /// </summary>
    public static string ResolveRepositoryRoot(string startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
            return startDirectory;

        var current = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(startDirectory)));
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;

            current = current.Parent;
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(startDirectory));
    }
}
