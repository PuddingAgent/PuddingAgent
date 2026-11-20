namespace PuddingTokenization;

/// <summary>
/// 词表资产目录的解析结果：目录 + 它从哪儿来。
/// </summary>
/// <param name="Directory">词表目录；<c>null</c> 表示放弃精确计数。</param>
/// <param name="Source">来源，取值见 <see cref="TokenizerAssetDirectory"/> 上的来源常量。</param>
public sealed record TokenizerAssetResolution(string? Directory, string Source);

/// <summary>
/// 决定「去哪儿找 <c>&lt;identity&gt;.tokenizer.json</c>」。
/// </summary>
/// <remarks>
/// <para>
/// 独立成组件而不是留在调用方，是因为这条策略有一个**真实发生过**的错误：
/// 部署目录里有一个**空的** <c>tokenizers/</c>（构建本来就会建出目录，但没人往里放文件），
/// 旧判据只问 <c>Directory.Exists</c>，于是在「从没配过」的情况下报出了
/// <c>beside-application</c>，再由此把每份词表标成 <c>AssetMissing</c>——
/// 那句话读起来像「文件被删了」，让人去查部署回滚，而真相是环境变量没被这个进程读到。
/// </para>
/// <para>
/// 因此判据是「目录里**至少有一份** <c>*.tokenizer.json</c>」而不是「目录存在」。
/// 「目录在但没有资产」与「目录不存在」对使用方是同一件事：都拿不到精确计数，
/// 也都不该声称自己找到了资产。
/// </para>
/// <para>
/// 目录不写死：它是环境数据（同代码索引库），换目录不该改代码。
/// </para>
/// </remarks>
public static class TokenizerAssetDirectory
{
    /// <summary>指定资产目录的环境变量名。</summary>
    public const string EnvironmentVariableName = "PUDDING_TOKENIZER_ASSETS";

    /// <summary>
    /// 资产文件后缀。<see cref="TokenizerProvider"/> 按 <c>&lt;identity&gt;.tokenizer.json</c> 查找。
    /// </summary>
    public const string AssetFileSuffix = ".tokenizer.json";

    /// <summary>来自环境变量。</summary>
    public const string EnvironmentSource = "environment";

    /// <summary>来自应用目录旁带有资产的 <c>tokenizers</c> 子目录。</summary>
    public const string BesideApplicationSource = "beside-application";

    /// <summary>两处都没有可用资产 ⇒ 放弃精确计数。</summary>
    public const string NoneSource = "none";

    /// <summary>
    /// 数据根下的词表目录（<c>{DataRoot}/models/tokenizers</c>）。
    /// </summary>
    /// <remarks>
    /// 「词表放在数据根的哪里」与「资产叫什么、怎么算配好了」是同一件事，所以两者住在一起：
    /// 安装脚本把文件放进去，运行时去那里找，中间不需要另一处约定。数据根本身由宿主解析，
    /// 本组件不读环境变量也不猜它。
    /// </remarks>
    public static string DataRootAssetDirectory(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Path.Combine(dataRoot, "models", "tokenizers");
    }

    /// <summary>
    /// 解析目录。
    /// </summary>
    /// <remarks>
    /// 环境变量值与基准目录由调用方传入，而不是在这里直接读进程状态：这样这条策略
    /// 可以在测试里被完整驱动，不需要改环境变量、也不需要真的往应用目录里放文件。
    /// </remarks>
    /// <param name="environmentValue">
    /// <see cref="EnvironmentVariableName"/> 的当前值；<c>null</c>/空/空白一律视为未设置。
    /// </param>
    /// <param name="baseDirectory">应用目录（通常是 <see cref="AppContext.BaseDirectory"/>）。</param>
    public static TokenizerAssetResolution Resolve(string? environmentValue, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return new TokenizerAssetResolution(environmentValue.Trim(), EnvironmentSource);
        }

        var besideApplication = Path.Combine(baseDirectory, "tokenizers");
        return ContainsAnyAsset(besideApplication)
            ? new TokenizerAssetResolution(besideApplication, BesideApplicationSource)
            : new TokenizerAssetResolution(null, NoneSource);
    }

    /// <summary>
    /// 目录里是否有至少一份词表资产。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 公开而不是私有：「什么样的目录算配好了资产」必须只有一份定义。解析策略与引导期
    /// （把数据根告诉运行时）都要问同一个问题，两处各写一份必然会漂移。
    /// </para>
    /// <para>
    /// 不抛异常是硬要求：本方法会经由 <c>static readonly</c> 字段在**类型初始化**里被调用，
    /// 一次 <see cref="UnauthorizedAccessException"/> 就会让整个计数类型的初始化失败，
    /// 把「没有精确词表」升级成「所有上下文估算都不可用」。读不到就按「没有」处理，
    /// 与 <see cref="Directory.Exists"/> 在拒绝访问时返回 <c>false</c> 同义。
    /// </para>
    /// <para>
    /// 用「枚举后按后缀过滤」而不是通配符 <c>*.tokenizer.json</c>：后者的匹配规则随平台与
    /// 扩展名长度变化，而这里要的是一个能写进断言的确定语义。
    /// </para>
    /// </remarks>
    public static bool ContainsAnyAsset(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return false;
            }

            return Directory
                .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Any(file => file.EndsWith(AssetFileSuffix, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
