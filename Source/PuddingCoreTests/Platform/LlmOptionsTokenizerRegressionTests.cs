using Microsoft.ML.Tokenizers;
using PuddingCode.Platform;

namespace PuddingCoreTests;

/// <summary>
/// S5 接入的回归保护：**没有配置词表时，计数结果必须与接入前逐例相同**。
/// </summary>
/// <remarks>
/// <para>
/// 这条测试守的不是新功能，而是「新功能没被用上时什么都不会变」。S5 让
/// <c>GetTokenizer</c> 先试模型自带词表，一旦有人把分支写错（比如让未配置场景也走进
/// 精确路径、或让回退返回 0），全仓的上下文门禁都会跟着漂移，而那种漂移在面板上看起来
/// 只是「数字变了」，很难追。
/// </para>
/// <para>
/// <c>ModelTokenizers</c> 是 <c>static readonly</c>，进程内首次使用时定型，测试无法在
/// 同一个进程里切换配置。因此本类只断言「未配置环境」下的行为——这也正是默认部署形态：
/// 资源没接上时，行为必须与改动前完全一致。
/// </para>
/// </remarks>
[TestClass]
public sealed class LlmOptionsTokenizerRegressionTests
{
    private const string AssetEnvVar = "PUDDING_TOKENIZER_ASSETS";

    /// <summary>
    /// 确认本进程确实处于「未配置」形态；不满足时跳过而不是误判。
    /// </summary>
    private static void RequireUnconfiguredDeployment()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AssetEnvVar)))
        {
            Assert.Inconclusive(
                $"检测到 {AssetEnvVar}，本进程已启用精确词表，跳过「未配置」断言。");
        }

        var besideApplication = Path.Combine(AppContext.BaseDirectory, "tokenizers");
        if (Directory.Exists(besideApplication))
        {
            Assert.Inconclusive(
                $"检测到旁挂目录 {besideApplication}，本进程已启用精确词表，跳过「未配置」断言。");
        }
    }

    [TestMethod]
    public void DeepSeekWithoutVocabulary_MatchesO200kBaseExactly()
    {
        RequireUnconfiguredDeployment();

        // 接入前 DeepSeek 就是按 o200k_base 算的。没接资源时必须还是这个数，
        // 一个 token 都不能差。
        foreach (var text in new[] { "hello world", "\u4eba\u5de5\u667a\u80fd", "1234567890", "<|\u2581pad\u2581|>" })
        {
            var expected = TiktokenTokenizer.CreateForEncoding("o200k_base").CountTokens(text);
            var actual = ContextUsageSnapshotStore.CountTokens(text, "deepseek-flash");

            Assert.AreEqual(expected, actual, $"「{text}」在未配置词表时结果发生了变化。");
        }
    }

    [TestMethod]
    public void EmptyOrNullText_ReturnsZero()
    {
        Assert.AreEqual(0, ContextUsageSnapshotStore.CountTokens(null));
        Assert.AreEqual(0, ContextUsageSnapshotStore.CountTokens(string.Empty));
    }

    [TestMethod]
    public void UnregisteredModel_StillProducesACountAndDoesNotThrow()
    {
        // 回退链的契约：任何 modelId 都必须能算出数，不能因为「没登记」就抛异常——
        // 抛异常会让整条请求准备链路挂掉，比数字不准严重得多。
        Assert.IsTrue(ContextUsageSnapshotStore.CountTokens("hello world", "some-unknown-model") > 0);
        Assert.IsTrue(ContextUsageSnapshotStore.CountTokens("hello world", null) > 0);
    }

    [TestMethod]
    public void SameModelAndText_IsStableAcrossCalls()
    {
        // 词表缓存若被写坏（例如每次重建、或 key 冲突到别的 tokenizer），
        // 同一输入会给出不同结果。这条用重复调用来兜住。
        var first = ContextUsageSnapshotStore.CountTokens("\u6211\u4eec\u4eca\u5929\u8981\u4fee\u590d\u8fd9\u4e2a\u7f3a\u9677", "deepseek-flash");
        for (var i = 0; i < 5; i++)
        {
            Assert.AreEqual(
                first,
                ContextUsageSnapshotStore.CountTokens("\u6211\u4eec\u4eca\u5929\u8981\u4fee\u590d\u8fd9\u4e2a\u7f3a\u9677", "deepseek-flash"));
        }
    }
}
