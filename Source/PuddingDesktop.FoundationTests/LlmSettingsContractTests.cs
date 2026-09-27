using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-02: form semantics that must not silently drop limits, secrets or invalid input.</summary>
public sealed class LlmSettingsContractTests
{
    [Fact]
    public void KeepIsTheDefaultSoAnUnshownSecretSurvives()
    {
        Assert.Equal(ApiKeyChange.Keep, default(ApiKeyChange));
        Assert.Contains("未配置密钥", LlmSettingsText.DescribeKeyState(false, ApiKeyChange.Keep), StringComparison.Ordinal);
        Assert.Contains("已配置密钥", LlmSettingsText.DescribeKeyState(true, ApiKeyChange.Keep), StringComparison.Ordinal);
        Assert.Contains("清除", LlmSettingsText.DescribeKeyState(true, ApiKeyChange.Clear), StringComparison.Ordinal);
        Assert.Contains("替换", LlmSettingsText.DescribeKeyState(true, ApiKeyChange.Replace), StringComparison.Ordinal);
        Assert.DoesNotContain("明文", LlmSettingsText.DescribeKeyState(true, ApiKeyChange.Replace), StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyLimitTextMeansUnsetInsteadOfZero()
    {
        Assert.Null(LlmSettingsText.ParseOptionalInt(""));
        Assert.Null(LlmSettingsText.ParseOptionalInt("   "));
        Assert.Null(LlmSettingsText.ParseOptionalInt("0"));
        Assert.Null(LlmSettingsText.ParseOptionalInt("-5"));
        Assert.Null(LlmSettingsText.ParseOptionalInt("abc"));
        Assert.Equal(12, LlmSettingsText.ParseOptionalInt(" 12 "));
        Assert.Null(LlmSettingsText.ParseOptionalLong(""));
        Assert.Equal(2_000_000L, LlmSettingsText.ParseOptionalLong("2000000"));
        Assert.Equal("", LlmSettingsText.FormatOptional<int>(null));
        Assert.Equal("7", LlmSettingsText.FormatOptional<int>(7));
    }

    [Theory]
    [InlineData("1.25", 1.25)]
    [InlineData(" 0 ", 0)]
    [InlineData("", 0)]
    [InlineData("abc", 0)]
    [InlineData("-1", 0)]
    public void PricesParseInvariantlyAndNeverGoNegative(string text, double expected)
    {
        Assert.Equal((decimal)expected, LlmSettingsText.ParsePrice(text));
    }

    [Fact]
    public void CapabilityTagsAreSplitDeduplicatedAndRoundTrip()
    {
        Assert.Equal(["tools", "vision"], LlmSettingsText.ParseTags("tools, vision"));
        Assert.Equal(["tools", "vision"], LlmSettingsText.ParseTags("tools，vision；tools"));
        Assert.Empty(LlmSettingsText.ParseTags(null));
        Assert.Empty(LlmSettingsText.ParseTags(" , ; "));
        Assert.Equal("tools, vision", LlmSettingsText.FormatTags(["tools", "vision"]));
        Assert.Equal("", LlmSettingsText.FormatTags(null));
    }

    [Fact]
    public void ProviderFormRejectsWhatCoreWouldReject()
    {
        var valid = new LlmProviderEdit("pool", "Pool", "https://example.invalid/v1", "", true,
            ApiKeyChange.Keep, null, new LlmProviderLimits(3, 100, 4));
        Assert.Empty(LlmSettingsText.Validate(valid));

        Assert.Contains("服务商 ID", LlmSettingsText.Validate(valid with { ProviderId = "bad id" }).Single(), StringComparison.Ordinal);
        Assert.Contains("名称", LlmSettingsText.Validate(valid with { Name = " " }).Single(), StringComparison.Ordinal);
        Assert.Contains("BaseUrl", LlmSettingsText.Validate(valid with { BaseUrl = "example.invalid" }).Single(), StringComparison.Ordinal);
        Assert.Contains("BaseUrl", LlmSettingsText.Validate(valid with { BaseUrl = "https://user:pass@example.invalid/v1" }).Single(), StringComparison.Ordinal);
        Assert.Contains("BaseUrl", LlmSettingsText.Validate(valid with { BaseUrl = "https://example.invalid/v1?x=1" }).Single(), StringComparison.Ordinal);
        Assert.Contains("新密钥", LlmSettingsText.Validate(valid with { KeyChange = ApiKeyChange.Replace, NewKey = "" }).Single(), StringComparison.Ordinal);
        Assert.Empty(LlmSettingsText.Validate(valid with { KeyChange = ApiKeyChange.Clear, NewKey = null }));
    }

    [Fact]
    public void ModelFormChecksProtocolAndTokenNesting()
    {
        var valid = new LlmModelEdit("pool", "model", "Model", "openai", [], 32768, 16384, 4096, null, 1m, 2m, 0.5m, true, false, false, 0);
        Assert.Empty(LlmSettingsText.Validate(valid));
        Assert.Contains("协议", LlmSettingsText.Validate(valid with { Protocol = "grpc" }).Single(), StringComparison.Ordinal);
        Assert.Contains("最大输出", LlmSettingsText.Validate(valid with { MaxOutputTokens = 65536 }).Single(), StringComparison.Ordinal);
        Assert.Contains("最大输入", LlmSettingsText.Validate(valid with { MaxInputTokens = 65536 }).Single(), StringComparison.Ordinal);
        Assert.Contains("服务商", LlmSettingsText.Validate(valid with { ProviderId = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("最大上下文", LlmSettingsText.Validate(valid with { MaxContextTokens = null }).Single(), StringComparison.Ordinal);
        Assert.Contains("最大输出", LlmSettingsText.Validate(valid with { MaxOutputTokens = 0 }).Single(), StringComparison.Ordinal);
        Assert.Empty(LlmSettingsText.Validate(valid with { MaxInputTokens = null }));
    }

    [Fact]
    public void ProtocolListMatchesTheCoreContract()
    {
        Assert.Equal(["openai", "responses", "anthropic"], LlmSettingsText.Protocols);
    }

    [Fact]
    public void QuotaLimitsAreOnlyRejectedWhenTheyCannotBeHonoured()
    {
        Assert.Empty(LlmSettingsText.Validate(LlmQuotaLimits.Unlimited));
        Assert.Empty(LlmSettingsText.Validate(new LlmQuotaLimits(1000, 1000)));
        Assert.Empty(LlmSettingsText.Validate(new LlmQuotaLimits(1000, null)));
        Assert.Contains("每日", LlmSettingsText.Validate(new LlmQuotaLimits(0, null)).Single(), StringComparison.Ordinal);
        Assert.Contains("每月", LlmSettingsText.Validate(new LlmQuotaLimits(null, -1)).Single(), StringComparison.Ordinal);
        Assert.Contains("不能大于", LlmSettingsText.Validate(new LlmQuotaLimits(5000, 1000)).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void QuotaStatusReportsUsageHonestlyAndNeverInventsACounter()
    {
        var unlimited = new LlmQuotaStatus(null, null, 120, 400, false, null, null, DateTimeOffset.UtcNow);
        Assert.Null(unlimited.DailyUsedPercent);
        Assert.Null(unlimited.MonthlyUsedPercent);
        Assert.Contains("未设置限额", unlimited.Describe(), StringComparison.Ordinal);
        Assert.Equal(LlmQuotaLimits.Unlimited, unlimited.Limits);

        var half = new LlmQuotaStatus(200, 800, 100, 400, false, null, null, DateTimeOffset.UtcNow);
        Assert.Equal(0.5, half.DailyUsedPercent);
        Assert.Equal(0.5, half.MonthlyUsedPercent);
        Assert.Equal("配额内。", half.Describe());

        var suspended = new LlmQuotaStatus(200, 800, 200, 400, true, null, null, DateTimeOffset.UtcNow);
        Assert.Contains("超出配额", suspended.Describe(), StringComparison.Ordinal);
        Assert.Equal(1d, suspended.DailyUsedPercent);

        var over = new LlmQuotaStatus(200, 800, 260, 400, true, null, null, DateTimeOffset.UtcNow);
        Assert.True(over.DailyUsedPercent > 1d, "超额时百分比必须大于 100%，不得截断成 100%");
        Assert.Equal(new LlmQuotaLimits(200, 800), over.Limits);
    }
}
