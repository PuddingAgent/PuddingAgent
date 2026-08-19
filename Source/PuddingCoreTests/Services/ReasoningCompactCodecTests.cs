using System.Security.Cryptography;
using System.Text;
using PuddingCode.Services;
using ThinkingChunk = PuddingCode.Services.ReasoningCompactCodec.ThinkingChunk;
using DecodedThinking = PuddingCode.Services.ReasoningCompactCodec.DecodedThinking;

namespace PuddingCoreTests.Services;

[TestClass]
public sealed class ReasoningCompactCodecTests
{
    // ---- 验收 1：round-trip 逐字节一致 ----

    [TestMethod]
    public void Encode_Decode_RoundTrip_ByteIdentical()
    {
        var chunks = new[]
        {
            new ThinkingChunk("首先分析需求，", 1_000),
            new ThinkingChunk("然后给出方案。", 1_150),
            new ThinkingChunk("最后验证。", 1_300),
        };
        var text = string.Concat(chunks.Select(c => c.Text));

        var json = ReasoningCompactCodec.Encode(text, chunks);
        var decoded = ReasoningCompactCodec.Decode(json);

        Assert.IsNotNull(decoded);
        Assert.IsTrue(decoded.IsCompactFormat);
        Assert.AreEqual(text, decoded.Text);
        Assert.AreEqual(chunks.Length, decoded.Chunks.Count);
        for (var i = 0; i < chunks.Length; i++)
        {
            // chunk 文本按 UTF-8 字节边界精确还原
            Assert.AreEqual(chunks[i].Text, decoded.Chunks[i].Text);
            // timestamp 还原为绝对原值（delta 累加正确）
            Assert.AreEqual(chunks[i].Timestamp, decoded.Chunks[i].Timestamp);
        }

        // hash 必须等于 text 的 SHA-256 小写 hex（独立计算验证）
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        Assert.AreEqual(expectedHash, decoded.Hash);
        Assert.IsTrue(decoded.HashValid);
    }

    [TestMethod]
    public void Encode_Decode_RoundTrip_SingleChunk()
    {
        var chunks = new[] { new ThinkingChunk("single chunk", 42) };
        var json = ReasoningCompactCodec.Encode("single chunk", chunks);
        var decoded = ReasoningCompactCodec.Decode(json);

        Assert.IsNotNull(decoded);
        Assert.AreEqual("single chunk", decoded.Text);
        Assert.AreEqual(1, decoded.Chunks.Count);
        Assert.AreEqual("single chunk", decoded.Chunks[0].Text);
        Assert.AreEqual(42, decoded.Chunks[0].Timestamp);
        Assert.IsTrue(decoded.HashValid);
    }

    // ---- 验收 2：旧格式兼容 ----

    [TestMethod]
    public void Decode_LegacyArrayFormat_Compatible()
    {
        const string legacy = """[{"text":"a","timestamp":100},{"text":"b","timestamp":150}]""";

        var decoded = ReasoningCompactCodec.Decode(legacy);

        Assert.IsNotNull(decoded);
        Assert.IsFalse(decoded.IsCompactFormat);
        Assert.AreEqual("ab", decoded.Text);
        Assert.AreEqual(2, decoded.Chunks.Count);
        Assert.AreEqual("a", decoded.Chunks[0].Text);
        Assert.AreEqual(100, decoded.Chunks[0].Timestamp);
        Assert.AreEqual("b", decoded.Chunks[1].Text);
        Assert.AreEqual(150, decoded.Chunks[1].Timestamp);
        Assert.IsNull(decoded.Hash);
        Assert.IsTrue(decoded.HashValid);
    }

    [TestMethod]
    public void Decode_LegacyFormat_ChineseTextJoinedCorrectly()
    {
        const string legacy = """[{"text":"分析","timestamp":100},{"text":"用户","timestamp":150}]""";

        var decoded = ReasoningCompactCodec.Decode(legacy);

        Assert.IsNotNull(decoded);
        Assert.AreEqual("分析用户", decoded.Text);
        Assert.AreEqual(2, decoded.Chunks.Count);
        Assert.AreEqual("分析", decoded.Chunks[0].Text);
        Assert.AreEqual("用户", decoded.Chunks[1].Text);
        Assert.AreEqual(100, decoded.Chunks[0].Timestamp);
        Assert.AreEqual(150, decoded.Chunks[1].Timestamp);
        Assert.IsTrue(decoded.HashValid);
    }

    // ---- 验收 3：篡改检测（fail-open，不抛异常）----

    [TestMethod]
    public void Decode_TamperedText_HashInvalid_FailOpen()
    {
        var chunks = new[] { new ThinkingChunk("original text", 42) };
        var json = ReasoningCompactCodec.Encode("original text", chunks);

        // 篡改 text 内容，保留 hash 不变
        var tampered = json.Replace("original text", "tampered text!!");
        Assert.AreNotEqual(json, tampered);

        var decoded = ReasoningCompactCodec.Decode(tampered);

        // 不抛异常（fail-open），返回篡改后的 text，但 hash 校验失败
        Assert.IsNotNull(decoded);
        Assert.AreEqual("tampered text!!", decoded.Text);
        Assert.IsFalse(decoded.HashValid);
    }

    [TestMethod]
    public void Decode_NewFormatWithoutHash_HashInvalid()
    {
        const string json = """{"v":2,"text":"hello","chunks":[{"o":0,"t":0}]}""";

        var decoded = ReasoningCompactCodec.Decode(json);

        Assert.IsNotNull(decoded);
        Assert.IsTrue(decoded.IsCompactFormat);
        Assert.AreEqual("hello", decoded.Text);
        Assert.IsNull(decoded.Hash);
        Assert.IsFalse(decoded.HashValid);
    }

    // ---- 验收 4：空边界 ----

    [TestMethod]
    public void Encode_Decode_EmptyTextAndChunks_Ok()
    {
        var json = ReasoningCompactCodec.Encode(string.Empty, Array.Empty<ThinkingChunk>());
        var decoded = ReasoningCompactCodec.Decode(json);

        Assert.IsNotNull(decoded);
        Assert.AreEqual(string.Empty, decoded.Text);
        Assert.AreEqual(0, decoded.Chunks.Count);
        Assert.IsTrue(decoded.IsCompactFormat);
        Assert.IsTrue(decoded.HashValid);
    }

    [TestMethod]
    public void Encode_TextMismatchChunks_Throws()
    {
        // 防御性校验：text 与 chunks join 不一致必须拒绝（防止写入 hash/偏移不一致的坏数据）
        var chunks = Array.Empty<ThinkingChunk>();
        Assert.ThrowsExactly<ArgumentException>(
            () => ReasoningCompactCodec.Encode("abc", chunks));
    }

    // ---- 验收 5：中文多字节 UTF-8 偏移正确（关键坑）----

    [TestMethod]
    public void Encode_ChineseText_OffsetIsUtf8Bytes_NotCharIndex()
    {
        // "分析"=6B，"用户需求"=12B，"abc"=3B；text 共 21 个 UTF-8 字节、9 个 char。
        var chunks = new[]
        {
            new ThinkingChunk("分析", 0),
            new ThinkingChunk("用户需求", 150),
            new ThinkingChunk("abc", 300),
        };
        var text = "分析用户需求abc";

        var json = ReasoningCompactCodec.Encode(text, chunks);

        // o 必须是 UTF-8 字节偏移：若误用 char index 会得到 [0,2,6]，正确应为 [0,6,18]
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var chunkArr = doc.RootElement.GetProperty("chunks");
        Assert.AreEqual(0, chunkArr[0].GetProperty("o").GetInt32());
        Assert.AreEqual(6, chunkArr[1].GetProperty("o").GetInt32());
        Assert.AreEqual(18, chunkArr[2].GetProperty("o").GetInt32());
        // t 为相对 delta：首条绝对基准 0，后续 = 当前 - 上一条
        Assert.AreEqual(0, chunkArr[0].GetProperty("t").GetInt64());
        Assert.AreEqual(150, chunkArr[1].GetProperty("t").GetInt64());
        Assert.AreEqual(150, chunkArr[2].GetProperty("t").GetInt64());

        // round-trip：按字节边界切回，chunk 文本与绝对 timestamp 精确还原
        var decoded = ReasoningCompactCodec.Decode(json);
        Assert.IsNotNull(decoded);
        Assert.AreEqual(text, decoded.Text);
        Assert.AreEqual("分析", decoded.Chunks[0].Text);
        Assert.AreEqual("用户需求", decoded.Chunks[1].Text);
        Assert.AreEqual("abc", decoded.Chunks[2].Text);
        Assert.AreEqual(0, decoded.Chunks[0].Timestamp);
        Assert.AreEqual(150, decoded.Chunks[1].Timestamp);
        Assert.AreEqual(300, decoded.Chunks[2].Timestamp);
        Assert.IsTrue(decoded.HashValid);
    }

    // ---- 验收 6：乱序 chunks（offset 非升序）→ 返回 null，不抛未处理异常 ----

    [TestMethod]
    public void Decode_OutOfOrderOffsets_ReturnsNull_NoThrow()
    {
        const string json = """{"v":2,"text":"abcd","chunks":[{"o":1,"t":0},{"o":0,"t":5}],"hash":"any"}""";

        var decoded = ReasoningCompactCodec.Decode(json);

        Assert.IsNull(decoded);
    }

    [TestMethod]
    public void Decode_DuplicateOffsets_ReturnsNull_NoThrow()
    {
        const string json = """{"v":2,"text":"ab","chunks":[{"o":0,"t":0},{"o":0,"t":5}],"hash":"any"}""";

        var decoded = ReasoningCompactCodec.Decode(json);

        Assert.IsNull(decoded);
    }

    // ---- 额外健壮性 ----

    [TestMethod]
    public void Decode_InvalidJson_ReturnsNull()
    {
        Assert.IsNull(ReasoningCompactCodec.Decode("{not json"));
        Assert.IsNull(ReasoningCompactCodec.Decode("[]!"));
        Assert.IsNull(ReasoningCompactCodec.Decode(null));
        Assert.IsNull(ReasoningCompactCodec.Decode(string.Empty));
        Assert.IsNull(ReasoningCompactCodec.Decode("   "));
    }

    [TestMethod]
    public void Decode_UnknownSchemaVersion_ReturnsNull()
    {
        const string json = """{"v":3,"text":"x","chunks":[{"o":0,"t":0}],"hash":"any"}""";

        Assert.IsNull(ReasoningCompactCodec.Decode(json));
    }

    [TestMethod]
    public void Decode_OffsetInsideMultiByteChar_ReturnsNull()
    {
        // "分析"=6 字节，o=4 落在"析"的 UTF-8 序列中间 → 非法边界
        const string json = """{"v":2,"text":"分析","chunks":[{"o":4,"t":0}],"hash":"any"}""";

        Assert.IsNull(ReasoningCompactCodec.Decode(json));
    }

    [TestMethod]
    public void Decode_OffsetBeyondTextLength_ReturnsNull()
    {
        const string json = """{"v":2,"text":"abc","chunks":[{"o":0,"t":0},{"o":9,"t":5}],"hash":"any"}""";

        Assert.IsNull(ReasoningCompactCodec.Decode(json));
    }
}
