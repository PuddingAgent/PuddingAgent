namespace PuddingPathFiltering;

/// <summary>
/// <b>唯一真源</b>的噪声路径判定（ADR-089 U4-4 D2）。索引侧与工具侧的排除名单全部从这里派生，
/// 不再各自维护一份。
/// <para>
/// 名单 = 该仓库既有的三套「已文档化」排除集的<b>并集</b>（46 项，见
/// <c>PuddingRetrievalEval/Services/NoiseDirectoryRules.cs</c> 的注释：<c>SearchGrepTool.DefaultExcludeDirs</c> 12 项、
/// <c>IndexExcludePatterns.NoiseDirNames</c> 28 项、<c>FullTextIndexOptions.ExcludedDirectoryNames</c> 33 项）
/// <b>并</b> U4-4 任务书 §D2 显式要求补齐的 4 项：<c>.pudding</c>（19,178 个可索引文件级的仓库第一大噪声源）、
/// <c>.tmp-build</c>、<c>.pnpm-store</c>、<c>.tmp-test-out</c>、<c>.firecrawl</c>。
/// </para>
/// <para>
/// <b>语义</b>：按路径段精确匹配（大小写不敏感），最后一段也参与匹配 —— 因此名为 <c>bin</c> 的<b>文件</b>也算噪声
/// （与 <c>FullTextIndexOptions.IsExcludedPath</c> 完全一致，这是既有契约，本刀不改动它）。
/// 名字<b>以</b>噪声名开头不算（<c>bin.cs</c> / <c>obj.json</c> 不是噪声）。
/// </para>
/// <para>
/// ⚠️ 已知过度排除（非本刀引入，沿用既有语义并登记为后续切片）：<c>Debug</c>/<c>Release</c>/<c>x64</c>/<c>x86</c>/
/// <c>ARM</c>/<c>ARM64</c> 是<b>名字级</b>匹配，因此 <c>Source/PuddingDesktop/Debug</c> 与
/// <c>Tests/PuddingDesktop.Tests/Debug</c>（<c>.gitignore</c> 注释明说它们是真实源码）共 14 个文件会被排除。
/// 仓库的 <c>.gitignore</c> 对这些名字用的是<b>根锚定</b>（<c>/[Dd]ebug/</c>）以避免该误伤；本类型没有引入
/// 「根限定」这一维度，因为消费者中 <c>PythonIndexer</c>/<c>TypeScriptIndexer</c> 只拿到裸目录名、无法表达深度，
/// 引入该维度会让同一份清单在不同消费者下语义不一致。
/// </para>
/// </summary>
public static class PathNoiseRules
{
    /// <summary>噪声目录名（制成品 / 依赖 / IDE / 工具产物），按路径段精确匹配。</summary>
    public static IReadOnlySet<string> DirectoryNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // ── 制成品（编译 / 打包 / 测试产物）──
        "bin", "obj", "out", "dist", "build", "pub", "publish", "artifacts",
        ".next", ".nuxt", ".output", ".angular", "target",
        "TestResults", "coverage", ".nyc_output",
        "Debug", "Release", "x64", "x86", "ARM", "ARM64",

        // ── 依赖（包缓存 / 虚拟环境 / 第三方代码）──
        "node_modules", "packages", "vendor", "bower_components",
        ".pnpm-store", "venv", ".venv", ".tox", ".eggs",

        // ── IDE / 版本控制元数据 ──
        ".git", ".svn", ".hg", ".vs", ".idea", ".vscode",

        // ── 工具产物 / 缓存（含本仓库的运行时与工具目录）──
        ".pudding", ".pudding-code", ".firecrawl", ".codex-out",
        ".tmp", ".tmp-build", ".tmp-test-out", "tmp", "temp",
        "__pycache__", ".pytest_cache", ".mypy_cache", ".cache", ".turbo",
        "$outputWwwroot",
    };

    /// <summary>噪声文件名（精确匹配，忽略大小写）。来源：<c>FullTextIndexOptions.ExcludedFileNames</c>。</summary>
    public static IReadOnlySet<string> FileNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json",
        "yarn.lock",
        "pnpm-lock.yaml",
        ".DS_Store",
        "Thumbs.db",
        "desktop.ini",
    };

    /// <summary>单个目录名/文件名是否为噪声名（与深度无关，供只能拿到裸名字的遍历器使用）。</summary>
    public static bool IsNoiseSegment(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        return DirectoryNames.Contains(name) || FileNames.Contains(name);
    }

    /// <summary>
    /// 把 <paramref name="path"/> 折算成相对扫描根 <paramref name="scanRoot"/> 的形式再做噪声判定。
    /// <para>
    /// <b>为什么必须有这个重载</b>：噪声名单是按<b>相对扫描根</b>的路径段匹配的。若把绝对路径直接喂给
    /// <see cref="IsNoisePath"/>，宿主自身的目录名会被当成噪声 —— 例如工作区在
    /// <c>C:\Users\x\AppData\Local\Temp\...</c> 下时，段名 <c>Temp</c> 命中 <c>temp</c>，
    /// 于是<b>整个工作区被静默排除</b>。U4-4 接入时 <c>PuddingCodeIntelligenceTests</c> 的两个用例
    /// 当场抓到了这个错误（见报告 §D4）。
    /// </para>
    /// 纯字符串运算，不做路径解析、不触文件系统。
    /// </summary>
    public static bool IsNoisePathBelow(string? scanRoot, string? path)
    {
        var root = PathText.Normalize(scanRoot);
        var candidate = PathText.Normalize(path);

        if (root.Length == 0)
            return IsNoisePath(candidate);

        if (candidate.Length > root.Length + 1 &&
            candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
            candidate[root.Length] == '/')
        {
            return IsNoisePath(candidate[(root.Length + 1)..]);
        }

        // 不在给定根之下（例如链接文件/跨根路径）：**降级为只看最后一段**。
        // 这样既能拦住「文件直接叫 bin/obj」的情形，又绝不会因为宿主自己的目录名而误判整棵树。
        return IsNoiseSegment(PathText.Basename(candidate));
    }

    /// <summary>
    /// 相对扫描根的路径是否落在噪声目录下。任何一个路径段（含最后一段）命中即算噪声。
    /// 接受 <c>\</c> 与 <c>/</c> 两种分隔符。<b>只接受相对路径</b>；握有绝对路径时用
    /// <see cref="IsNoisePathBelow"/>。
    /// </summary>
    public static bool IsNoisePath(string? relativePath)
    {
        var normalized = PathText.Normalize(relativePath);
        if (normalized.Length == 0)
            return false;

        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (DirectoryNames.Contains(segment) || FileNames.Contains(segment))
                return true;
        }

        return false;
    }
}
