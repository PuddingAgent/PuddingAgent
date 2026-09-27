using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-04 model &amp; memory slice: pair semantics, catalogue validation and value preservation.</summary>
public sealed class AgentModelPolicyContractTests
{
    private static readonly AgentModelCatalogEntry[] Catalog =
    [
        new("deepseek", "DeepSeek", "deepseek-chat", "DeepSeek Chat", false, true, false),
        new("deepseek", "DeepSeek", "deepseek-reasoner", "DeepSeek Reasoner", false, true, false),
        new("deepseek", "DeepSeek", "legacy-chat", "Legacy Chat", false, true, true),
        new("dashscope", "DashScope", "qwen-embedding", "Qwen Embedding", true, true, false),
        new("xunfei", "Xunfei", "spark-chat", "Spark Chat", false, false, false),
    ];

    [Fact]
    public void ChoiceDistinguishesUnsetPartialAndSet()
    {
        Assert.False(AgentModelChoice.None.IsSet);
        Assert.False(AgentModelChoice.None.IsPartial);
        Assert.True(new AgentModelChoice("deepseek", "").IsPartial);
        Assert.True(new AgentModelChoice("", "deepseek-chat").IsPartial);
        Assert.True(new AgentModelChoice("deepseek", "deepseek-chat").IsSet);
        Assert.False(new AgentModelChoice("deepseek", "deepseek-chat").IsPartial);
    }

    [Fact]
    public void MemorySearchModesMatchTheDocumentedSetAndPreserveUnknownValues()
    {
        Assert.Equal(["off", "instant", "deep"], AgentModelPolicyText.MemorySearchModes);
        Assert.Equal("deep", AgentModelPolicyText.DefaultMemorySearchMode);
        Assert.Equal("deep", AgentModelPolicyText.NormalizeMemorySearchMode(null));
        Assert.Equal("deep", AgentModelPolicyText.NormalizeMemorySearchMode("  "));
        Assert.Equal("instant", AgentModelPolicyText.NormalizeMemorySearchMode(" instant "));
        Assert.Equal("hybrid", AgentModelPolicyText.NormalizeMemorySearchMode("hybrid"));
        Assert.True(AgentModelPolicyText.IsKnownMemorySearchMode("DEEP"));
        Assert.False(AgentModelPolicyText.IsKnownMemorySearchMode("hybrid"));
    }

    [Fact]
    public void ReasoningEffortIsFreeTextBecauseTheProviderDecidesTheVocabulary()
    {
        Assert.Equal("", AgentModelPolicyText.NormalizeReasoningEffort(null));
        Assert.Equal("max", AgentModelPolicyText.NormalizeReasoningEffort("  max "));
        Assert.Empty(AgentModelPolicyText.Validate(
            new AgentModelPolicy(AgentModelChoice.None, AgentModelChoice.None, AgentModelChoice.None, "deep", "max"), Catalog));
        Assert.Contains("空白", AgentModelPolicyText.Validate(
            new AgentModelPolicy(AgentModelChoice.None, AgentModelChoice.None, AgentModelChoice.None, "deep", "low high"),
            Catalog).Single(), StringComparison.Ordinal);
        Assert.Contains("推理强度", AgentModelPolicyText.Validate(
            new AgentModelPolicy(AgentModelChoice.None, AgentModelChoice.None, AgentModelChoice.None, "deep", new string('x', 65)),
            Catalog).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationRejectsPartialUnknownDisabledDeprecatedAndWrongKindChoices()
    {
        AgentModelPolicy Policy(AgentModelChoice chat) => new(chat, AgentModelChoice.None, AgentModelChoice.None, "deep", "");

        Assert.Empty(AgentModelPolicyText.Validate(Policy(new AgentModelChoice("deepseek", "deepseek-chat")), Catalog));
        Assert.Contains("同时指定", AgentModelPolicyText.Validate(Policy(new AgentModelChoice("deepseek", "")), Catalog).Single(), StringComparison.Ordinal);
        Assert.Contains("不存在", AgentModelPolicyText.Validate(Policy(new AgentModelChoice("deepseek", "nope")), Catalog).Single(), StringComparison.Ordinal);
        Assert.Contains("已停用", AgentModelPolicyText.Validate(Policy(new AgentModelChoice("xunfei", "spark-chat")), Catalog).Single(), StringComparison.Ordinal);
        Assert.Contains("已废弃", AgentModelPolicyText.Validate(Policy(new AgentModelChoice("deepseek", "legacy-chat")), Catalog).Single(), StringComparison.Ordinal);
        Assert.Contains("embedding 模型", AgentModelPolicyText.Validate(Policy(new AgentModelChoice("dashscope", "qwen-embedding")), Catalog).Single(), StringComparison.Ordinal);

        var embedding = new AgentModelPolicy(AgentModelChoice.None, AgentModelChoice.None, new AgentModelChoice("deepseek", "deepseek-chat"), "deep", "");
        Assert.Contains("不是 embedding", AgentModelPolicyText.Validate(embedding, Catalog).Single(), StringComparison.Ordinal);
        var goodEmbedding = new AgentModelPolicy(AgentModelChoice.None, AgentModelChoice.None, new AgentModelChoice("dashscope", "qwen-embedding"), "deep", "");
        Assert.Empty(AgentModelPolicyText.Validate(goodEmbedding, Catalog));

        Assert.Contains("检索模式", AgentModelPolicyText.Validate(
            new AgentModelPolicy(AgentModelChoice.None, AgentModelChoice.None, AgentModelChoice.None, "", ""), Catalog).Single(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeNeverClaimsAMissingModelExists()
    {
        Assert.Contains("未设置", AgentModelPolicyText.Describe(AgentModelChoice.None, Catalog), StringComparison.Ordinal);
        Assert.Contains("未完成", AgentModelPolicyText.Describe(new AgentModelChoice("deepseek", ""), Catalog), StringComparison.Ordinal);
        Assert.Contains("DeepSeek / DeepSeek Chat", AgentModelPolicyText.Describe(new AgentModelChoice("deepseek", "deepseek-chat"), Catalog), StringComparison.Ordinal);
        Assert.Contains("目录中不存在", AgentModelPolicyText.Describe(new AgentModelChoice("deepseek", "gone"), Catalog), StringComparison.Ordinal);
    }

    [Fact]
    public void ChoiceListsAreSplitByModelKind()
    {
        Assert.Equal(3, AgentModelPolicyText.ChoicesForProvider(Catalog, "deepseek", embedding: false).Count);
        Assert.Empty(AgentModelPolicyText.ChoicesForProvider(Catalog, "deepseek", embedding: true));
        Assert.Equal("qwen-embedding",
            AgentModelPolicyText.ChoicesForProvider(Catalog, "dashscope", embedding: true).Single().ModelId);
        Assert.NotNull(AgentModelPolicyText.Find(Catalog, "DEEPSEEK", "DEEPSEEK-CHAT"));
        Assert.Null(AgentModelPolicyText.Find(Catalog, "deepseek", "gone"));
    }
}
