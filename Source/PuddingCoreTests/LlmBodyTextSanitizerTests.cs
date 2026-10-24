using PuddingCode.Observability;

namespace PuddingCoreTests;

/// <summary>
/// 内联图片净化必须满足三条不变量，否则「估算 vs 报文」这个对照就没有意义：
/// ① 没有图片时报文一字不改；② 只切 base64 载荷，JSON 结构和前后文本保持原样；
/// ③ 张数与剔除字节数必须如实计数——它们是把 bodyBytes 解释清楚的关键。
/// </summary>
[TestClass]
public sealed class LlmBodyTextSanitizerTests
{
    [TestMethod]
    public void RedactInlineImages_LeavesTextWithoutImagesUntouched()
    {
        const string body = "{\"messages\":[{\"role\":\"user\",\"content\":\"你好\"}]}";

        var result = LlmBodyTextSanitizer.RedactInlineImages(body);

        Assert.AreEqual(body, result.Text, "无图片时不得改写报文。");
        Assert.AreEqual(0, result.InlineImageCount);
        Assert.AreEqual(0L, result.InlineImageWireBytes);
    }

    [TestMethod]
    public void RedactInlineImages_StripsPayloadAndKeepsSurroundingJson()
    {
        var payload = new string('A', 4096);
        var body = "{\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,"
            + payload + "\"}},{\"type\":\"text\",\"text\":\"看图\"}]}";

        var result = LlmBodyTextSanitizer.RedactInlineImages(body);

        Assert.AreEqual(1, result.InlineImageCount);
        Assert.AreEqual(4096L, result.InlineImageWireBytes);
        Assert.IsFalse(result.Text.Contains(payload, StringComparison.Ordinal), "base64 载荷必须被剔除。");
        StringAssert.Contains(result.Text, "data:image;base64,<redacted>");
        StringAssert.Contains(result.Text, "\"type\":\"text\",\"text\":\"看图\"", "图片之后的文本不得被吞。");
        Assert.IsTrue(result.Text.Length < 200, $"净化后应远小于原文，实际 {result.Text.Length}。");
    }

    [TestMethod]
    public void RedactInlineImages_CountsEveryImage()
    {
        var first = new string('B', 100);
        var second = new string('C', 250);
        var body = "[\"data:image/jpeg;base64," + first + "\",\"between\",\"data:image/webp;base64," + second + "\"]";

        var result = LlmBodyTextSanitizer.RedactInlineImages(body);

        Assert.AreEqual(2, result.InlineImageCount);
        Assert.AreEqual(350L, result.InlineImageWireBytes);
        StringAssert.Contains(result.Text, "\"between\"", "两张图之间的内容必须保留。");
    }

    [TestMethod]
    public void RedactInlineImages_DoesNotTreatAudioPayloadAsImage()
    {
        var body = "{\"url\":\"data:audio/wav;base64," + new string('D', 64) + "\"}";

        var result = LlmBodyTextSanitizer.RedactInlineImages(body);

        Assert.AreEqual(0, result.InlineImageCount, "本函数只处理图片；audio 由 provider 按音频计费，不在其职责内。");
        Assert.AreEqual(0L, result.InlineImageWireBytes);
        Assert.AreEqual(body, result.Text);
    }

    [TestMethod]
    public void RedactInlineImages_HandlesNullAndEmpty()
    {
        Assert.AreEqual(0, LlmBodyTextSanitizer.RedactInlineImages(null).InlineImageCount);
        Assert.AreEqual(string.Empty, LlmBodyTextSanitizer.RedactInlineImages(null).Text);
        Assert.AreEqual(string.Empty, LlmBodyTextSanitizer.RedactInlineImages(string.Empty).Text);
    }
}
