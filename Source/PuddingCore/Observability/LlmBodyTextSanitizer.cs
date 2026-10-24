using System.Text;

namespace PuddingCode.Observability;

/// <summary>
/// 把出站报文里的内联图片载荷（<c>data:image/...;base64,...</c>）替换为占位符，
/// 让「按文本 token 数报文」这件事只覆盖 provider 真的按文本计费的部分。
///
/// 为什么需要它：provider 对 inline 图片按图片张数/尺寸计费，不按 base64 字符数计费。
/// 若直接对整份报文分词，几十万字节的 base64 会被算成十几万 token，
/// 于是「估算 vs 报文」这个对照会稳定地差出 5~6 倍——那不是估算错了，是仪表错了。
/// 实测依据：同一会话 attempts 1..11 中 bodyTokens/localEstimatedTokens 从 5.72 单调降到 4.39，
/// 而 provider 侧的 prompt_tokens 与估算只差 14%，两者不可能同时成立。
/// </summary>
public static class LlmBodyTextSanitizer
{
    private const string ImagePrefix = "data:image";
    private const string Base64Marker = ";base64,";
    private const string Placeholder = "data:image;base64,<redacted>";

    /// <summary>一次净化的结果：净化后的文本、内联图片张数、被剔除的 base64 载荷字符数。</summary>
    public readonly record struct Result(string Text, int InlineImageCount, long InlineImageWireBytes);

    public static Result RedactInlineImages(string? body)
    {
        if (string.IsNullOrEmpty(body) || body.IndexOf(ImagePrefix, StringComparison.Ordinal) < 0)
            return new Result(body ?? string.Empty, 0, 0);

        var output = new StringBuilder(body.Length);
        var imageCount = 0;
        long wireBytes = 0;
        var cursor = 0;

        while (cursor < body.Length)
        {
            var marker = body.IndexOf(Base64Marker, cursor, StringComparison.Ordinal);
            if (marker < 0)
                break;

            // 只有紧跟 "data:image" 前缀的 base64 才是图片；audio 等其它载荷不在本函数职责内。
            var prefixStart = body.LastIndexOf(ImagePrefix, marker, StringComparison.Ordinal);
            if (prefixStart < cursor)
            {
                output.Append(body, cursor, marker + Base64Marker.Length - cursor);
                cursor = marker + Base64Marker.Length;
                continue;
            }

            var payloadStart = marker + Base64Marker.Length;
            var payloadEnd = payloadStart;
            while (payloadEnd < body.Length && IsBase64Char(body[payloadEnd]))
                payloadEnd++;

            output.Append(body, cursor, prefixStart - cursor);
            output.Append(Placeholder);
            imageCount++;
            wireBytes += payloadEnd - payloadStart;
            cursor = payloadEnd;
        }

        output.Append(body, cursor, body.Length - cursor);
        return new Result(output.ToString(), imageCount, wireBytes);
    }

    private static bool IsBase64Char(char c)
        => (c >= 'A' && c <= 'Z')
        || (c >= 'a' && c <= 'z')
        || (c >= '0' && c <= '9')
        || c == '+'
        || c == '/'
        || c == '=';
}
