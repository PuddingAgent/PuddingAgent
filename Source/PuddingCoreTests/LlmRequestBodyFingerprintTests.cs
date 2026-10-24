using System.Security.Cryptography;
using System.Text;
using PuddingCode.Observability;

namespace PuddingCoreTests;

/// <summary>
/// 出站请求指纹的两条不变量：
/// ① 字节数按 UTF-8 编码计，不按字符计；② 不做任何换行归一化。
/// 这两条一旦被"顺手优化"掉，日志里的指纹就会和真实报文对不上，而那正是这个仪表存在的意义。
/// </summary>
[TestClass]
public sealed class LlmRequestBodyFingerprintTests
{
    [TestMethod]
    public void Compute_CountsUtf8Bytes_NotCharacters()
    {
        // "你好" 各 3 字节 + emoji 4 字节 = 10；按字符数计只有 3。
        var body = Encoding.UTF8.GetBytes("你好🙂");

        var (bodyBytes, sha256) = LlmRequestBodyFingerprint.Compute(body);

        Assert.AreEqual(10, bodyBytes);
        Assert.AreEqual(64, sha256.Length);
        Assert.AreEqual(sha256.ToLowerInvariant(), sha256, "摘要必须是小写十六进制，否则跨日志比对会漏。");
    }

    [TestMethod]
    public void Compute_DoesNotNormalizeNewlines()
    {
        var lf = Encoding.UTF8.GetBytes("{\n}");
        var crlf = Encoding.UTF8.GetBytes("{\r\n}");

        var (lfBytes, lfSha) = LlmRequestBodyFingerprint.Compute(lf);
        var (crlfBytes, crlfSha) = LlmRequestBodyFingerprint.Compute(crlf);

        // 若做了换行归一化，两份不同报文会得到同一指纹——那正是要防的事故。
        Assert.AreNotEqual(lfSha, crlfSha);
        Assert.AreEqual(lfBytes + 1, crlfBytes);
    }

    [TestMethod]
    public void Compute_IsDeterministicAndMatchesRawSha256()
    {
        var body = Encoding.UTF8.GetBytes("{\"model\":\"deepseek-flash\",\"messages\":[]}");

        var first = LlmRequestBodyFingerprint.Compute(body);
        var second = LlmRequestBodyFingerprint.Compute(body);

        Assert.AreEqual(first.BodyBytes, second.BodyBytes);
        Assert.AreEqual(first.BodySha256, second.BodySha256);
        // 与独立算出的原始 SHA-256 对齐，防止实现里偷加前缀/后缀。
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(body)), first.BodySha256);
    }
}
