using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-10 vault and classifier slice: write-only secrets, placeholder syntax, honest health states.</summary>
public sealed class SecurityContractTests
{
    [Fact]
    public void PlaceholderMatchesCoresRegexSyntax()
    {
        // Core 的正则是 \{\{vault:(?<name>[a-zA-Z0-9._-]+)\}\}
        Assert.Equal("{{vault:openai-key}}", SecurityText.BuildPlaceholder("openai-key"));
        Assert.Equal("{{vault:a.b_c-1}}", SecurityText.BuildPlaceholder("a.b_c-1"));

        var secret = new VaultSecret(1, "kv-1", "openai-key", "", "api", ["prod"],
            DateTimeOffset.UtcNow, null);
        Assert.Equal("{{vault:openai-key}}", secret.Placeholder);
        Assert.Equal("api（接口密钥）", secret.CategoryText);
        Assert.Equal("prod", secret.TagsText);
        Assert.Equal("无标签", (secret with { Tags = [] }).TagsText);
    }

    [Fact]
    public void SecretNamesMustStayInsideThePlaceholderAlphabet()
    {
        Assert.True(SecurityText.IsValidSecretName("openai-key"));
        Assert.True(SecurityText.IsValidSecretName("a.b_c-1"));
        Assert.False(SecurityText.IsValidSecretName("has space"));
        Assert.False(SecurityText.IsValidSecretName("has:colon"));
        Assert.False(SecurityText.IsValidSecretName(""));
        Assert.False(SecurityText.IsValidSecretName(null));
    }

    [Fact]
    public void CreateNeedsAValueAndUpdateMayKeepIt()
    {
        var create = new VaultSecretEdit("", "openai-key", "desc", "api", "sk-value", ["prod"]);
        Assert.True(create.IsCreate);
        Assert.Empty(SecurityText.Validate(create));
        Assert.Contains("必须填写密钥值", SecurityText.Validate(create with { Value = "" }).Single(), StringComparison.Ordinal);

        // 更新留空 = 保持原值，Core 的 UpdateKeyVaultSecretCommand 也是这个语义。
        var update = new VaultSecretEdit("kv-1", "openai-key", "desc", "api", "", []);
        Assert.False(update.IsCreate);
        Assert.Empty(SecurityText.Validate(update));

        Assert.Contains("名称", SecurityText.Validate(create with { Name = " " }).Single(), StringComparison.Ordinal);
        Assert.Contains("只能包含", SecurityText.Validate(create with { Name = "bad name" }).Single(), StringComparison.Ordinal);
        Assert.Contains("分类", SecurityText.Validate(create with { Category = "secret" }).Single(), StringComparison.Ordinal);
        Assert.Equal(["general", "api", "token"], SecurityText.VaultCategories);
    }

    [Fact]
    public void TagParsingIsDeduplicatedAndBlankTolerant()
    {
        Assert.Equal(["prod", "eu"], SecurityText.ParseTags(" prod, eu "));
        Assert.Equal(["prod", "eu"], SecurityText.ParseTags("prod;eu"));
        Assert.Equal(["prod"], SecurityText.ParseTags("prod, PROD, prod"));
        Assert.Empty(SecurityText.ParseTags(null));
        Assert.Empty(SecurityText.ParseTags("  "));
        Assert.Equal("prod, eu", SecurityText.FormatTags(["prod", "eu"]));
        Assert.Equal("", SecurityText.FormatTags(null));
    }

    [Fact]
    public void ClassifierHealthIsDescribedWithoutPretending()
    {
        Assert.Contains("未接线", SecurityText.DescribeHealthSummary(ClassifierHealthReport.NotConfigured), StringComparison.Ordinal);
        Assert.Contains("没有", SecurityText.DescribeHealthSummary(new ClassifierHealthReport(true, [])), StringComparison.Ordinal);

        var healthy = new ClassifierHealthReport(true,
            [new ClassifierStatusEntry("safety", "healthy", "", 0, DateTimeOffset.UtcNow, 12.5)]);
        Assert.Contains("全部健康", SecurityText.DescribeHealthSummary(healthy), StringComparison.Ordinal);

        var degraded = new ClassifierHealthReport(true,
        [
            new ClassifierStatusEntry("safety", "healthy", "", 0, DateTimeOffset.UtcNow, 12.5),
            new ClassifierStatusEntry("intent", "degraded", "timeout", 3, DateTimeOffset.UtcNow, 900)
        ]);
        Assert.Contains("1 个非健康", SecurityText.DescribeHealthSummary(degraded), StringComparison.Ordinal);

        Assert.Equal("降级", SecurityText.DescribeClassifierHealth("degraded"));
        Assert.Equal("不可用", SecurityText.DescribeClassifierHealth("Unavailable"));
        Assert.Equal("未知（尚未探测）", SecurityText.DescribeClassifierHealth("unknown"));
        Assert.Equal("状态未知", SecurityText.DescribeClassifierHealth(null));
        Assert.Equal("SomethingNew", SecurityText.DescribeClassifierHealth("SomethingNew"));

        Assert.Equal("12.5 ms", healthy.Classifiers[0].LatencyText);
        Assert.Equal("无延迟数据", (healthy.Classifiers[0] with { LastLatencyMs = null }).LatencyText);
        Assert.Equal("尚未探测", (healthy.Classifiers[0] with { LastCheckedAtUtc = null }).CheckedText);
    }

    [Fact]
    public void NoticesStateTheWriteOnlyBoundary()
    {
        Assert.Contains("不回显明文", SecurityText.WriteOnlyNotice, StringComparison.Ordinal);
        Assert.Contains("{{vault:名称}}", SecurityText.PlaceholderNotice, StringComparison.Ordinal);
        Assert.Contains("未知态", SecurityText.ClassifierUnknownNotice, StringComparison.Ordinal);
    }
}