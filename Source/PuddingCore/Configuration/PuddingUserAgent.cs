namespace PuddingCode.Configuration;

/// <summary>
/// PuddingAgent 出站 HTTP 请求的统一 User-Agent 标识（单一事实来源）。
/// </summary>
/// <remarks>
/// 所有由本仓库发起的出站 HTTP/WS 请求都应引用此常量，而不是各自硬编码字符串。
/// 组合根通过 <c>ConfigureHttpClientDefaults</c> + PuddingUserAgentHandler 为
/// IHttpClientFactory 创建的客户端统一兜底；Flurl 通道由 FlurlWebClient 兜底。
/// </remarks>
public static class PuddingUserAgent
{
    /// <summary>产品名（User-Agent 的产品标识部分）。</summary>
    public const string ProductName = "PuddingAgent";

    /// <summary>标识版本；UA 语义变化时递增。</summary>
    public const string Version = "1.0";

    /// <summary>默认的完整 User-Agent 值：PuddingAgent/1.0。</summary>
    public const string Value = ProductName + "/" + Version;

    /// <summary>组合「产品 + 子系统」形态，例如 PuddingAgent/1.0 FeishuConnector/1.0。</summary>
    public static string Compose(string componentName, string componentVersion = Version)
        => $"{Value} {componentName}/{componentVersion}";
}
