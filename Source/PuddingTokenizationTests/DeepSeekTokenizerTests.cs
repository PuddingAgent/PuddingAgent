using System.Text;
using PuddingTokenization;

namespace PuddingTokenizationTests;

/// <summary>
/// 精确分词链路的行为基线。
/// </summary>
/// <remarks>
/// <para>
/// 这批用例是用来**取红**的。它们记录的是实测中真实出现过的失败：
/// v1（默认 <c>BpeTokenizer.Create(vocab, merges)</c>）下「人工智能」编码为 **0** 个 token、
/// special token 被拆成 6 个普通 token；v2（仅开 ByteLevel）下 special token 仍错。
/// 谁把 <c>ByteLevel</c> 关掉、或删掉任一级预分词，这里就会失败。
/// </para>
/// <para>
/// 需要真实词表的用例通过环境变量 <c>PUDDING_DEEPSEEK_TOKENIZER_JSON</c> 定位
/// <c>tokenizer.json</c>；未提供时跳过（Inconclusive），不伪装成通过。
/// 结构/错误分支的用例不依赖任何资源，任何机器上都应通过。
/// </para>
/// </remarks>
[TestClass]
public sealed class DeepSeekTokenizerTests
{
    private const string AssetEnvVar = "PUDDING_DEEPSEEK_TOKENIZER_JSON";

    private static string? AssetPath => Environment.GetEnvironmentVariable(AssetEnvVar);

    private static TokenizerLoadResult LoadOrSkip()
    {
        var path = AssetPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Assert.Inconclusive(
                $"未设置 {AssetEnvVar} 或文件不存在，跳过依赖真实词表的断言。当前值：{path ?? "(null)"}");
        }

        using var stream = File.OpenRead(path!);
        return DeepSeekTokenizerJsonLoader.Load(stream);
    }

    [TestMethod]
    public void Load_ReportsExpectedStructure()
    {
        var result = LoadOrSkip();

        Assert.AreEqual("BPE", result.ModelType);
        Assert.IsTrue(result.UsesByteLevel, "DeepSeek 词表必须启用 ByteLevel，否则非 ASCII 无法编码。");
        Assert.AreEqual(3, result.SplitStageCount, "官方 pre_tokenizer 含 3 级 Split。");
        Assert.AreEqual(128000, result.VocabularySize);
        Assert.AreEqual(127741, result.MergeCount);
        Assert.AreEqual(1283, result.SpecialTokenCount);
    }

    [TestMethod]
    public void SpecialTokens_MapToDeclaredIds()
    {
        var tokenizer = LoadOrSkip().Tokenizer;

        // 三个 special token 在 vocab 里分别是 0 / 1 / 2。
        // 若 pre_tokenizer 没有逐级拿到 specialTokens，这里会得到一串普通 token。
        AssertTokenIsExactly(tokenizer, "<\uFF5Cbegin\u2581of\u2581sentence\uFF5C>", 0);
        AssertTokenIsExactly(tokenizer, "<\uFF5Cend\u2581of\u2581sentence\uFF5C>", 1);
        AssertTokenIsExactly(tokenizer, "<\uFF5C\u2581pad\u2581\uFF5C>", 2);
    }

    private static void AssertTokenIsExactly(
        Microsoft.ML.Tokenizers.BpeTokenizer tokenizer,
        string text,
        int expectedId)
    {
        var ids = tokenizer.EncodeToIds(text);
        Assert.HasCount(1, ids, $"「{text}」应被识别为单个 special token，实际 {ids.Count} 个。");
        Assert.AreEqual(expectedId, ids[0]);
    }

    [TestMethod]
    public void Chinese_EncodesToNonEmptyTokens()
    {
        var tokenizer = LoadOrSkip().Tokenizer;

        // v1 的实测结果：0 个 token。这条断言直接守住那个回归。
        var ids = tokenizer.EncodeToIds("\u4eba\u5de5\u667a\u80fd");
        Assert.IsNotEmpty(ids, "中文必须能编码；返回 0 表示字节级映射缺失。");

        // 顺带守住可逆性，避免「编出来了但解不回去」。
        Assert.AreEqual("\u4eba\u5de5\u667a\u80fd", tokenizer.Decode(ids));
    }

    [TestMethod]
    public void Digits_AreSplitByTheConfiguredRule()
    {
        var tokenizer = LoadOrSkip().Tokenizer;

        // 官方规则 \p{N}{1,3}：10 位数字切成 3+3+3+1 = 4 组。
        // 这条用例证明「预分词真的参与运算」，而不是仅仅被构造出来。
        var ids = tokenizer.EncodeToIds("1234567890");
        Assert.HasCount(4, ids, "数字应按 1~3 位一组切分，10 位应产生 4 个 token。");
    }

    [TestMethod]
    public void MixedSamples_RoundTrip()
    {
        var tokenizer = LoadOrSkip().Tokenizer;

        var samples = new[]
        {
            "hello world",
            "def foo(x): return x+1",
            "{\"a\":1,\"b\":[2,3]}",
            "Agent \u8c03\u7528 http_fetch \u5931\u8d25\u7387 51.7%",
            "\u6211\u4eec\u4eca\u5929\u8981\u4fee\u590d\u8fd9\u4e2a\u7f3a\u9677",
        };

        foreach (var sample in samples)
        {
            var ids = tokenizer.EncodeToIds(sample);
            Assert.AreEqual(sample, tokenizer.Decode(ids), $"往返失败：{sample}");
        }
    }

    [TestMethod]
    public void Load_RejectsNonBpeModelType()
    {
        // 不依赖真实词表：给一个 Unigram 结构，必须明确拒绝而不是按 BPE 硬算。
        var json = """{"model":{"type":"Unigram","vocab":{"a":0},"merges":["a b"]}}""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        Assert.ThrowsExactly<InvalidDataException>(
            () => DeepSeekTokenizerJsonLoader.Load(stream));
    }

    [TestMethod]
    public void Load_RejectsBpeWithoutByteLevel()
    {
        // 缺 ByteLevel 时必须失败：否则会构造出一个「能编码但中文全为 0」的对象，
        // 这种静默错误比直接失败危险得多。
        var json = """
        {
          "model": {"type":"BPE","vocab":{"a":0,"b":1},"merges":["a b"]},
          "pre_tokenizer": {"type":"Split","pattern":{"Regex":"\\p{L}+"}}
        }
        """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        Assert.ThrowsExactly<InvalidDataException>(
            () => DeepSeekTokenizerJsonLoader.Load(stream));
    }
}
