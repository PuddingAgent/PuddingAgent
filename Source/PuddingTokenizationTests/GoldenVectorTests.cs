using System.Text.Json;
using Microsoft.ML.Tokenizers;
using PuddingTokenization;

namespace PuddingTokenizationTests;

/// <summary>
/// 与官方 tokenizer 的逐 token 对齐验证。
/// </summary>
/// <remarks>
/// <para>
/// 这是整条链路上**唯一**能判定「真的对齐了」的断言。此前的证据（round-trip 可逆、
/// 中文非 0、数字按规则切分）都只说明实现自洽，不能排除「两边一起偏移但各自自洽」。
/// </para>
/// <para>
/// 期望值来源：用官方 <c>@huggingface/tokenizers</c>（Node 0.2.0）加载**同一份**
/// <c>DeepSeek-V4.1-Flash</c> 的 <c>tokenizer.json</c> 生成，见
/// <c>Docs/14_reports/2026-10-24-DeepSeek-tokenizer调研.md</c> 的做法记录。
/// 生成环境与本实现完全独立（不同语言、不同库、不同 BPE 引擎），所以二者一致
/// 才算交叉验证通过。
/// </para>
/// </remarks>
[TestClass]
public sealed class GoldenVectorTests
{
    private const string AssetEnvVar = "PUDDING_DEEPSEEK_TOKENIZER_JSON";

    [TestMethod]
    public void EncodeMatchesOfficialTokenizerPerTokenId()
    {
        var assetPath = Environment.GetEnvironmentVariable(AssetEnvVar);
        if (string.IsNullOrWhiteSpace(assetPath) || !File.Exists(assetPath))
            Assert.Inconclusive($"未设置 {AssetEnvVar}，跳过与官方向量的比对。");

        var vectorsPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "golden-vectors.json");
        Assert.IsTrue(File.Exists(vectorsPath), $"找不到黄金向量文件：{vectorsPath}");

        BpeTokenizer tokenizer;
        using (var stream = File.OpenRead(assetPath!))
            tokenizer = DeepSeekTokenizerJsonLoader.Load(stream).Tokenizer;

        using var document = JsonDocument.Parse(File.ReadAllText(vectorsPath));
        var checkedCount = 0;

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var text = item.GetProperty("text").GetString() ?? string.Empty;
            var expected = item.GetProperty("ids")
                .EnumerateArray()
                .Select(e => e.GetInt32())
                .ToArray();

            var actual = tokenizer.EncodeToIds(text).ToArray();

            CollectionAssert.AreEqual(
                expected,
                actual,
                $"「{text}」与本实现不一致。官方 {expected.Length} 个 token，本实现 {actual.Length} 个。");

            checkedCount++;
        }

        // 防止「夹具被清空导致测试空转」——空集合的 CollectionAssert 永远通过。
        Assert.IsTrue(checkedCount >= 10, $"黄金向量数量异常，仅 {checkedCount} 条。");
    }
}
