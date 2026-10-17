using PuddingDeepSeekWeb;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.DeepSeekWeb;

/// <summary>
/// <see cref="IDeepSeekWebTransport"/> 的宿主适配器：把组件的出站请求原样映射到既有
/// <see cref="IWebClient"/>（复用统一 UA、超时与 Flurl 通道），并把原始响应原样带回。
/// <para>
/// 职责边界（组件化交付规程 S5）：本类<b>不做</b>错误分类、<b>不重试</b>、<b>不吞异常</b>。
/// HTTP 状态码语义、OpenAI 兼容错误块解析与可重试判定全部在组件
/// （<c>PuddingDeepSeekWeb</c>）内完成；这样测组件无需网络，测宿主无需组件语义。
/// </para>
/// </summary>
internal sealed class WebClientDeepSeekTransport(IWebClient webClient) : IDeepSeekWebTransport
{
    private readonly IWebClient _webClient =
        webClient ?? throw new ArgumentNullException(nameof(webClient));

    public async Task<DeepSeekWebTransportResponse> SendAsync(
        DeepSeekWebTransportRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = await _webClient.SendAsync(
            new WebClientRequest
            {
                Url = request.Url,
                Method = request.Method,
                Headers = request.Headers,
                Body = request.Body,
                ContentType = request.ContentType,
                TimeoutSeconds = request.TimeoutSeconds,
            },
            ct).ConfigureAwait(false);

        return new DeepSeekWebTransportResponse
        {
            StatusCode = response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            Body = response.Body,
            Headers = response.Headers,
        };
    }
}
