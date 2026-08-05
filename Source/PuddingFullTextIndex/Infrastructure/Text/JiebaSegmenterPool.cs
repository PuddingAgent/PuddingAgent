using JiebaNet.Segmenter;

namespace PuddingFullTextIndex.Infrastructure.Text;

/// <summary>
/// JiebaSegmenter 懒加载单例。词典首次加载 ~100ms，之后零开销。
/// 供 JiebaAnalyzer 和 JiebaTokenizer 共享同一个 Segmenter 实例。
///
/// 初始化语义：首次失败不缓存。使用静态锁 + 可空实例字段，
/// 初始化失败时 _instance 保持为 null，下次访问 Instance 会重新尝试，
/// 因此资源在进程运行期间补齐后无需重启即可自愈（不再被 Lazy 缓存失败）。
///
/// 关键防护：JiebaSegmenter 的 CLR 类型初始化器（静态构造函数）在整个进程
/// 生命周期内只运行一次。若首次运行因词典文件被瞬时锁定（杀软扫描刚构建
/// 的文件、资源复制未完成等）而失败，TypeInitializationException 会被 CLR
/// 永久缓存，该类型在进程存活期间将永远不可用，任何池级重试都无法绕过。
/// 因此 Instance 在首次构造 JiebaSegmenter 之前会调用
/// EnsureResourcesReadable 预校验所有词典文件可读（共享读打开 + 读 1 字节），
/// 遇到锁定退避重试，确保类型初始化器运行时文件确实可用。
///
/// Resources 路径解析策略（按优先级）：
/// 1. 当前程序集所在目录的 Resources/（须含 dict.txt）
/// 2. 应用程序基目录的 Resources/（须含 dict.txt）
/// 3. 从程序集位置向上查找源码树中的 Resources/（开发环境回退）
/// 4. 从 AppContext.BaseDirectory 向上查找 Resources/
/// 所有策略统一要求 Resources/dict.txt 存在，仅目录存在不算命中，
/// 避免把不完整的资源目录交给 Jieba 产生误导性错误。
/// </summary>
internal static class JiebaSegmenterPool
{
    private static readonly object SyncRoot = new();
    private static JiebaSegmenter? _instance;

    /// <summary>
    /// 最近一次初始化失败的诊断信息；成功初始化后清空（置为 null）。
    /// </summary>
    internal static string? LastInitError { get; private set; }

    /// <summary>
    /// Jieba 初始化可能读取的资源文件清单（预校验用）。
    /// 缺失的文件不视为锁定错误——交由 Jieba 自身报错以给出更准确的信息。
    /// </summary>
    private static readonly string[] ResourceFilesToPrecheck =
    {
        "dict.txt", "idf.txt", "stopwords.txt",
        "prob_emit.json", "prob_trans.json",
        "char_state_tab.json", "pos_prob_emit.json",
        "pos_prob_start.json", "pos_prob_trans.json",
        "cn_synonym.txt"
    };

    /// <summary>
    /// 获取 JiebaSegmenter 单例。初始化失败时抛出 InvalidOperationException，
    /// 但失败不会被缓存——下次访问本属性会重新尝试初始化（自愈）。
    /// 线程安全：实例的创建与读取均在 SyncRoot 锁内完成。
    /// </summary>
    public static JiebaSegmenter Instance
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
                    // 关键：在触发 JiebaSegmenter 类型初始化器之前确保词典文件可读，
                    // 防止瞬时文件锁导致类型初始化器失败并被 CLR 永久缓存。
                    EnsureResourcesReadable(resourceDir);
                    ConfigManager.ConfigFileBaseDir = resourceDir;

                    var seg = new JiebaSegmenter();
                    seg.Cut("预热"); // 触发词典加载

                    _instance = seg;
                    LastInitError = null; // 成功时清空失败记录
                    return _instance;
                }
                catch (Exception ex)
                {
                    var error = BuildInitializationError(ex);
                    LastInitError = error;
                    TryPersistErrorFile(error, ex);
                    throw new InvalidOperationException(error, ex);
                }
            }
        }
    }

    /// <summary>
    /// 预校验 Resources 文件可读：以共享读方式逐个打开并读取 1 字节。
    /// 遇到 IOException/UnauthorizedAccessException（典型为杀软扫描或复制
    /// 过程中的瞬时锁定）时退避重试，最多 10 次（约 10-15 秒窗口）。
    /// 全部可读后返回；持续锁定则抛出 IOException（此时尚未触碰
    /// JiebaSegmenter 类型，进程仍有机会在下次调用时自愈）。
    /// </summary>
    internal static void EnsureResourcesReadable(string resourceDir)
    {
        const int maxAttempts = 10;
        var delayMs = 200;

        for (var attempt = 1; ; attempt++)
        {
            Exception? lockError = null;
            string? lockedFile = null;

            foreach (var fileName in ResourceFilesToPrecheck)
            {
                var path = Path.Combine(resourceDir, fileName);
                if (!File.Exists(path))
                    continue; // 缺失不视为锁定，交由 Jieba 报错

                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.ReadByte();
                }
                catch (IOException ex)
                {
                    lockError = ex;
                    lockedFile = path;
                    break;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lockError = ex;
                    lockedFile = path;
                    break;
                }
            }

            if (lockError == null)
                return;

            if (attempt >= maxAttempts)
                throw new IOException(
                    $"Resources 文件在 {maxAttempts} 次尝试后仍被锁定，推迟初始化以避免类型初始化器永久失败：{lockedFile} -> {lockError.Message}",
                    lockError);

            Thread.Sleep(delayMs);
            delayMs = Math.Min((int)(delayMs * 1.5), 2000);
        }
    }

    /// <summary>
    /// 按优先级解析 Resources 目录路径。所有策略统一要求
    /// Resources/dict.txt 存在，仅目录存在不算命中。
    /// </summary>
    internal static string ResolveResourceDirectory()
    {
        // 策略1: 当前程序集所在目录（PuddingFullTextIndex.dll 位置）
        var assemblyDir = Path.GetDirectoryName(typeof(JiebaSegmenterPool).Assembly.Location);
        var fromAssembly = TryResolveResourceDir(assemblyDir);
        if (fromAssembly != null)
            return fromAssembly;

        // 策略2: 应用程序基目录（主机的 bin 目录）
        var appBase = AppContext.BaseDirectory;
        var fromAppBase = TryResolveResourceDir(appBase);
        if (fromAppBase != null)
            return fromAppBase;

        // 策略3: 从当前程序集位置向上查找源码树中的 Resources
        if (assemblyDir != null)
        {
            var candidate = FindResourceDirUpTree(assemblyDir);
            if (candidate != null)
                return candidate;
        }

        // 策略4: 从 AppContext.BaseDirectory 向上查找
        if (!string.IsNullOrEmpty(appBase))
        {
            var candidate = FindResourceDirUpTree(appBase);
            if (candidate != null)
                return candidate;
        }

        // 最后回退：仍然返回程序集目录（让 Jieba 自己报错，提供更好的错误信息）
        return assemblyDir ?? ".";
    }

    /// <summary>
    /// 策略1-2 的统一判定：给定基目录下存在 Resources/dict.txt 才算命中。
    /// 仅 Resources 目录存在而缺少 dict.txt 时返回 null（不命中），
    /// 避免把不完整的资源目录交给 Jieba。
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
    /// 格式化完整异常链（类型 + 消息），最内层异常附带堆栈。
    /// TypeInitializationException 的真实原因在 InnerException 中，
    /// 只报告最外层 Message 会丢失根因。
    /// </summary>
    internal static string FormatExceptionChain(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        var cur = ex;
        var depth = 0;
        Exception innermost = ex;

        while (cur != null && depth < 8)
        {
            if (sb.Length > 0) sb.Append(" ;; ");
            sb.Append($"[{depth}] {cur.GetType().FullName}: {cur.Message}");
            innermost = cur;
            cur = cur.InnerException;
            depth++;
        }

        var stack = innermost.StackTrace;
        if (!string.IsNullOrEmpty(stack))
            sb.Append(" ;; STACK: ").Append(stack.Replace("\r\n", " | ").Replace("\n", " | "));

        return sb.ToString();
    }

    /// <summary>
    /// 构造包含逐目录探测结果与完整异常链的初始化错误信息。
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
               $"异常链: {FormatExceptionChain(ex)}";
    }

    /// <summary>
    /// 将完整错误落盘到临时目录，便于无控制台环境（Desktop）事后取证。
    /// 写盘失败不影响主流程。
    /// </summary>
    private static void TryPersistErrorFile(string error, Exception ex)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "pudding-jieba-init-error.log");
            File.WriteAllText(path,
                $"{DateTime.Now:O} PuddingFullTextIndex JiebaSegmenterPool 初始化失败\n{error}\n\nFULL:\n{ex}\n");
        }
        catch
        {
            // 诊断落盘失败不应掩盖原始错误
        }
    }

    /// <summary>中文停用词（与 PuddingMemoryEngine 保持同步）。</summary>
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
