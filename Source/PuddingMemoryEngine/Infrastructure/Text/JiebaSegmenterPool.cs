namespace PuddingMemoryEngine.Infrastructure.Text;

/// <summary>
/// JiebaSegmenter 懒加载单例。词典首次加载 ~100ms，之后零开销。
/// 
/// Resources 路径解析策略（按优先级）：
/// 1. AppContext.BaseDirectory 的 Resources/
/// 2. 当前程序集所在目录的 Resources/
/// 3. 当前工作目录的 Resources/
/// 4. 从程序集位置向上查找源码树中的 Resources/（开发环境回退）
/// 5. 从 AppContext.BaseDirectory 向上查找 Resources/
/// </summary>
internal static class JiebaSegmenterPool
{
    private static readonly Lazy<JiebaNet.Segmenter.JiebaSegmenter> _segmenter = new(() =>
    {
        try
        {
            var resourceDir = ResolveResourceDirectory();
            JiebaNet.Segmenter.ConfigManager.ConfigFileBaseDir = resourceDir;

            var segmenter = new JiebaNet.Segmenter.JiebaSegmenter();
            segmenter.Cut("预热"); // 触发词典加载
            return segmenter;
        }
        catch (Exception ex)
        {
            var tried = new[]
            {
                AppContext.BaseDirectory,
                Path.GetDirectoryName(typeof(JiebaSegmenterPool).Assembly.Location),
                Directory.GetCurrentDirectory()
            };
            throw new InvalidOperationException(
                "JiebaSegmenter 初始化失败。请确认 Resources/ 目录已复制到输出目录。" +
                $"Assembly: {typeof(JiebaSegmenterPool).Assembly.Location}, " +
                $"BaseDirectory: {AppContext.BaseDirectory}, " +
                $"CWD: {Directory.GetCurrentDirectory()}, " +
                $"已探测: [{string.Join("; ", tried.Where(d => d != null))}], " +
                $"原始错误: {ex.Message}", ex);
        }
    });

    public static JiebaNet.Segmenter.JiebaSegmenter Instance => _segmenter.Value;

    /// <summary>
    /// 按优先级解析 Resources 目录路径。
    /// </summary>
    private static string ResolveResourceDirectory()
    {
        // 策略1: AppContext.BaseDirectory（启动项目的 bin 目录）
        var appBase = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(appBase) && Directory.Exists(Path.Combine(appBase, "Resources")))
            return Path.Combine(appBase, "Resources");

        // 策略2: 当前程序集所在目录
        var assemblyDir = Path.GetDirectoryName(typeof(JiebaSegmenterPool).Assembly.Location);
        if (assemblyDir != null && Directory.Exists(Path.Combine(assemblyDir, "Resources")))
            return Path.Combine(assemblyDir, "Resources");

        // 策略3: 当前工作目录
        var cwd = Directory.GetCurrentDirectory();
        if (Directory.Exists(Path.Combine(cwd, "Resources")))
            return Path.Combine(cwd, "Resources");

        // 策略4: 从程序集位置向上查找源码树中的 Resources
        if (assemblyDir != null)
        {
            var candidate = FindResourceDirUpTree(assemblyDir);
            if (candidate != null) return candidate;
        }

        // 策略5: 从 AppContext.BaseDirectory 向上查找
        if (!string.IsNullOrEmpty(appBase))
        {
            var candidate = FindResourceDirUpTree(appBase);
            if (candidate != null) return candidate;
        }

        // 最后回退：返回程序集目录（让 Jieba 自己报错，提供更好的错误信息）
        return (assemblyDir ?? appBase) ?? ".";
    }

    /// <summary>
    /// 从给定目录向上遍历目录树，查找包含 dict.txt 的 Resources 目录。
    /// 最多向上遍历 6 层。只检查各级目录下的 Resources/ 子目录（不递归枚举）。
    /// </summary>
    private static string? FindResourceDirUpTree(string startDir)
    {
        var dir = startDir;
        for (int i = 0; i < 6; i++)
        {
            var resourcesPath = Path.Combine(dir, "Resources");
            if (Directory.Exists(resourcesPath) &&
                File.Exists(Path.Combine(resourcesPath, "dict.txt")))
            {
                return resourcesPath;
            }

            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        return null;
    }

    /// <summary>中文停用词（可后续扩展）。</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "的", "了", "是", "在", "我", "有", "和", "就",
        "不", "人", "都", "一", "一个", "上", "也", "很",
        "到", "说", "要", "去", "你", "会", "着", "没有",
        "看", "好", "自己", "这", "他", "她", "它", "们",
        "那", "些", "什么", "怎么", "如何", "哪个", "为什么",
        "吗", "吧", "呢", "啊", "哦", "嗯", "哈"
    };

    public static bool IsStopWord(string word) => StopWords.Contains(word);
}
