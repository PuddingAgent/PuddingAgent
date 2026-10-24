namespace PuddingTokenization;

/// <summary>
/// 为什么没能用上「精确 tokenizer」，而回退到了通用编码。
/// </summary>
/// <remarks>
/// 存在的意义是让「回退」变成可观测的事实而不是静默行为：调用方要能在诊断里
/// 说清楚「这次计数是精确的」还是「这次是兜底的」。缺了它，方案上线后没人能
/// 分辨估算偏差是来自内容本身还是来自词表没加载上。
/// </remarks>
public enum TokenizerFallbackReason
{
    /// <summary>用上了模型自己的词表。</summary>
    None = 0,

    /// <summary>modelId 不在登记表里（例如 DeepSeek-V4-Pro、或全新模型）。</summary>
    ModelNotRegistered = 1,

    /// <summary>登记了，但词表目录里没有对应资源文件。</summary>
    AssetMissing = 2,

    /// <summary>资源文件在，但内容不符合预期（结构、版本或校验失败）。</summary>
    AssetInvalid = 3,

    /// <summary>资源可解析，但构造 tokenizer 时抛错。</summary>
    LoadFailed = 4,

    /// <summary>调用方没给词表目录，等于放弃精确计数。</summary>
    NotConfigured = 5,
}
