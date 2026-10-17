namespace PuddingDeepSeekWeb;

/// <summary>
/// DeepSeek Search（网页搜索）与 Fetch（单页抓取）的协议客户端接口。
/// <para>
/// 只读、无副作用；凭据以 <see cref="DeepSeekWebConnection"/> 逐次传入，组件本身不读取配置、
/// 不持有长期凭据。实现负责：参数校验 → 构造线上 JSON → 经 <see cref="IDeepSeekWebTransport"/>
/// 发送 → 解析成功响应 / 把 HTTP 错误映射为 <see cref="DeepSeekWebError"/> → 对可重试错误做有界重试。
/// </para>
/// </summary>
public interface IDeepSeekWebService
{
    /// <summary>按 query 搜索网页（POST /v1/tools/search）。</summary>
    Task<DeepSeekWebResult<DeepSeekSearchResponse>> SearchAsync(
        DeepSeekWebConnection connection,
        DeepSeekSearchRequest request,
        CancellationToken ct = default);

    /// <summary>抓取单个网页并以 Markdown 正文返回（POST /v1/tools/fetch）。</summary>
    Task<DeepSeekWebResult<DeepSeekFetchResponse>> FetchAsync(
        DeepSeekWebConnection connection,
        DeepSeekFetchRequest request,
        CancellationToken ct = default);
}
