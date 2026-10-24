namespace PuddingTokenization;

/// <summary>
/// 模型 id → 词表身份 的登记表。
/// </summary>
/// <remarks>
/// <para>
/// 这里刻意**只做精确别名匹配**，不做 <c>Contains("deepseek")</c> 这类前缀猜测。
/// 理由是可证伪的：DeepSeek-V4-Pro / V4-Flash 与 V4.1-Flash 的 <c>tokenizer.json</c>
/// 字节数不同（5,530,562 vs 5,530,707）、ETag 也不同，用同一份词表去估算另一个模型
/// 只会把「估算偏差」换成另一种偏差。未登记的模型一律走调用方原有的通用编码。
/// </para>
/// <para>
/// 归一化仅限去首尾空白 + 忽略大小写。不剥离 <c>deepseek-ai/</c> 前缀、不猜版本号 ——
/// 猜错一次就会静默返回错误的计数，而这种错误比「回退到通用编码」难发现得多。
/// </para>
/// </remarks>
public static class ModelTokenizerCatalog
{
    /// <summary>DeepSeek-V4.1-Flash 的词表身份（对应 assets 目录下的资源名前缀）。</summary>
    public const string DeepSeekV41Flash = "deepseek-v4.1-flash";

    private static readonly Dictionary<string, string> ExactMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deepseek-flash"] = DeepSeekV41Flash,
        ["DeepSeek-V4.1-Flash"] = DeepSeekV41Flash,
        ["deepseek-ai/DeepSeek-V4.1-Flash"] = DeepSeekV41Flash,
        ["deepseek-ai/deepseek-v4.1-flash"] = DeepSeekV41Flash,
    };

    /// <summary>
    /// 解析 modelId 对应的词表身份；未登记返回 <c>null</c>（调用方应回退）。
    /// </summary>
    public static string? ResolveExact(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        return ExactMap.TryGetValue(modelId.Trim(), out var identity) ? identity : null;
    }

    /// <summary>已登记的所有词表身份（供诊断与资源校验使用）。</summary>
    public static IReadOnlyCollection<string> RegisteredIdentities { get; } =
        ExactMap.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
