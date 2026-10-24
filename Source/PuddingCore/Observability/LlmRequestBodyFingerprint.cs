using System.Security.Cryptography;

namespace PuddingCode.Observability;

/// <summary>计算最终请求体的字节数和 SHA-256；不解码、不重新序列化，也不归一化换行。</summary>
public static class LlmRequestBodyFingerprint
{
    public static (int BodyBytes, string BodySha256) Compute(ReadOnlySpan<byte> body)
        => (body.Length, Convert.ToHexStringLower(SHA256.HashData(body)));
}
