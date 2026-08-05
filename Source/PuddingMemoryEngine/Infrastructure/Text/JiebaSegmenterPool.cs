namespace PuddingMemoryEngine.Infrastructure.Text;

/// <summary>
/// JiebaSegmenter 懒加载单例。词典首次加载 ~100ms，之后零开销。
///
/// 初始化语义：首次失败不缓存。使用静态锁 + 可空实例字段，
/// 初始化失败时 _instance 保持为 null，下次访问 Instance 会重新尝试，
/// 因此资源在进程运行期间补齐后无需重启即可自愈（不再被 Lazy 缓存失败）。
///
/// Resources 路径解析策略（按优先级）：
/// 1. AppContext.BaseDirectory 的 Resources/（须含 dict.txt）
/// 2. 当前程序集所在目录的 Resources/（须含 dict.txt）
/// 3. 当前工作目录的 Resources/（须含 dict.txt）
/// 4. 从程序集位置向上查找源码树中的 Resources/（开发环境回退）
/// 5. 从 AppContext.BaseDirectory 向上查找 Resources/
/// 所有策略统一要求 Resources/dict.txt 存在，仅目录存在不算命中，
/// 避免把不完整的资源目录交给 Jieba 产生误导性错误。
/// </summary>
internal static class JiebaSegmenterPool
{
    private static readonly object SyncRoot = new();
    private static JiebaNet.Segmenter.JiebaSegmenter? _instance;

    /// <summary>
    /// 最近一次初始化失败的诊断信息；成功初始化后清空（置为 null）。
    /// </summary>
    internal static string? LastInitError { get; private set; }

    /// <summary>
    /// 获取 JiebaSegmenter 单例。初始化失败时抛出 InvalidOperationException，
    /// 但失败不会被缓存——下次访问本属性会重新尝试初始化（自愈）。
    /// 线程安全：实例的创建与读取均在 SyncRoot 锁内完成。
    /// </summary>
    public static JiebaNet.Segmenter.JiebaSegmenter Instance
    {
        get
        {
            lock (SyncRoot)
            {
                if (_instance != null)
                    return _instance;

                try
                {
                    var resourceDir = ResolveResourceDirectory();
                    JiebaNet.Segmenter.ConfigManager.ConfigFileBaseDir = resourceDir;

                    var segmenter = new JiebaNet.Segmenter.JiebaSegmenter();
                    segmenter.Cut("预热"); // 触发词典加载

                    _instance = segmenter;
                    LastInitError = null; // 成功时清空失败记录
                    return _instance;
                }
                catch (Exception ex)
                {
                    var error = BuildInitializationError(ex);
                    LastInitError = error;
                    throw new InvalidOperationException(error, ex);
                }
            }
        }
    }

    /// <summary>
    /// 按优先级解析 Resources 目录路径。所有策略统一要求
    /// Resources/dict.txt 存在，仅目录存在不算命中。
    /// internal static 以便 PuddingMemoryEngineTests 直接验证。
    /// </summary>
    internal static string ResolveResourceDirectory()
    {
        // 策略1: AppContext.BaseDirectory（启动项目的 bin 目录）
        var appBase = AppContext.BaseDirectory;
        var fromAppBase = TryResolveResourceDir(appBase);
        if (fromAppBase != null)
            return fromAppBase;

        // 策略2: 当前程序集所在目录
        var assemblyDir = Path.GetDirectoryName(typeof(JiebaSegmenterPool).Assembly.Location);
        var fromAssembly = TryResolveResourceDir(assemblyDir);
        if (fromAssembly != null)
            return fromAssembly;

        // 策略3: 当前工作目录
        var fromCwd = TryResolveResourceDir(Directory.GetCurrentDirectory());
        if (fromCwd != null)
            return fromCwd;

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
    /// 策略1-3 的统一判定：给定基目录下存在 Resources/dict.txt 才算命中。
    /// 仅 Resources 目录存在而缺少 dict.txt 时返回 null（不命中），
    /// 避免把不完整的资源目录交给 Jieba。internal static 以便单元测试
    /// 用临时目录验证"无 dict.txt 的 Resources 目录不被策略1-3 命中"。
    /// </summary>
    internal static string? TryResolveResourceDir(string? baseDir)
    {
        if (string.IsNullOrEmpty(baseDir))
            return null;

        var resourcesPath = Path.Combine(baseDir, "Resources");
        return Directory.Exists(resourcesPath) &&
               File.Exists(Path.Combine(resourcesPath, "dict.txt"))
            ? resourcesPath
            : null;
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
            var hit = TryResolveResourceDir(dir);
            if (hit != null)
                return hit;

            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        return null;
    }

    /// <summary>
    /// 构造包含逐目录探测结果的初始化错误信息，便于准确定位资源缺失原因
    /// （每个探测目录均标注 Resources/ 与 dict.txt 的存在状态）。
    /// </summary>
    private static string BuildInitializationError(Exception ex)
    {
        var probed = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(JiebaSegmenterPool).Assembly.Location),
            Directory.GetCurrentDirectory()
        };

        var probeDetails = probed
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d =>
            {
                var resourcesPath = Path.Combine(d!, "Resources");
                var hasDir = Directory.Exists(resourcesPath);
                var hasDict = hasDir && File.Exists(Path.Combine(resourcesPath, "dict.txt"));
                return $"{d} [Resources: {(hasDir ? "存在" : "缺失")}, dict.txt: {(hasDict ? "存在" : "缺失")}]";
            });

        return "JiebaSegmenter 初始化失败。请确认 Resources/dict.txt 已复制到输出目录。" +
               $"Assembly: {typeof(JiebaSegmenterPool).Assembly.Location}, " +
               $"BaseDirectory: {AppContext.BaseDirectory}, " +
               $"CWD: {Directory.GetCurrentDirectory()}, " +
               $"已探测: [{string.Join("; ", probeDetails)}], " +
               $"原始错误: {ex.Message}";
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
