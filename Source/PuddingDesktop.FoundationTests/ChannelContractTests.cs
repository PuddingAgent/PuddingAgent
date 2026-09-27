using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>
/// DS-05 channel slice: the App Secret is write-only, "blank" means keep, and there is no clear-secret
/// path because Core does not implement one.
/// </summary>
public sealed class ChannelContractTests
{
    private static ChannelEdit Edit(ChannelSecret secret, string appId = "cli_app") => new(
        "team-a-space", "channel-1", "Feishu Channel", "notes", "feishu", "agent-1",
        appId, secret, true, false, "", ["ou_1", "ou_2"], true);

    [Fact]
    public void KeepAndReplaceAreTheOnlySecretIntents()
    {
        Assert.False(ChannelSecret.Keep.Replace);
        Assert.Empty(ChannelSecret.Keep.Value);
        Assert.True(ChannelSecret.Of("s3cret").Replace);
        Assert.Equal("s3cret", ChannelSecret.Of("s3cret").Value);
        Assert.Contains("只写不读", ChannelText.SecretNotice, StringComparison.Ordinal);
        Assert.Contains("没有“清除密钥”", ChannelText.SecretNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewChannelMustCarryASecretAndAnEditMayKeepTheStoredOne()
    {
        // 新建：必须提供密钥。
        var missing = ChannelText.Validate(Edit(ChannelSecret.Keep), hasStoredSecret: false);
        Assert.Contains("还没有 App Secret", missing.Single(), StringComparison.Ordinal);
        Assert.Empty(ChannelText.Validate(Edit(ChannelSecret.Of("s3cret")), hasStoredSecret: false));

        // 编辑：留空 + 不勾选替换 = 保持，Core 会沿用已保存的密钥。
        Assert.Empty(ChannelText.Validate(Edit(ChannelSecret.Keep), hasStoredSecret: true));
        // 勾选替换却留空是自相矛盾的输入，必须拦下。
        var contradictory = ChannelText.Validate(Edit(ChannelSecret.Of(" ")), hasStoredSecret: true);
        Assert.Contains("内容为空", contradictory.Single(), StringComparison.Ordinal);
        // 没有已保存密钥时勾选替换但留空同样拦下。
        Assert.NotEmpty(ChannelText.Validate(Edit(ChannelSecret.Of("")), hasStoredSecret: false));
    }

    [Fact]
    public void ChannelFormRejectsWhatCoreRejects()
    {
        var valid = Edit(ChannelSecret.Of("s3cret"));
        Assert.Empty(ChannelText.Validate(valid, hasStoredSecret: true));
        Assert.Contains("工作区", ChannelText.Validate(valid with { WorkspaceId = "" }, true).Single(), StringComparison.Ordinal);
        Assert.Contains("渠道名称", ChannelText.Validate(valid with { Name = " " }, true).Single(), StringComparison.Ordinal);
        Assert.Contains("服务商", ChannelText.Validate(valid with { ProviderId = "" }, true).Single(), StringComparison.Ordinal);
        Assert.Contains("App ID", ChannelText.Validate(valid with { AppId = "" }, true).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void PrivilegedOpenIdsAreSplitNormalizedAndDeduplicated()
    {
        Assert.Equal(["ou_1", "ou_2"], ChannelText.ParseOpenIds(" ou_1, ou_2 "));
        Assert.Equal(["ou_1", "ou_2"], ChannelText.ParseOpenIds("ou_1;ou_2"));
        Assert.Equal(["ou_1"], ChannelText.ParseOpenIds("ou_1, ou_1, ou_1"));
        Assert.Empty(ChannelText.ParseOpenIds(null));
        Assert.Empty(ChannelText.ParseOpenIds("   "));
        Assert.Equal("ou_1, ou_2", ChannelText.FormatOpenIds(["ou_1", "ou_2"]));
        Assert.Equal("", ChannelText.FormatOpenIds(null));
    }

    [Fact]
    public void ReplyModesAndSecretStateAreDescribedHonestly()
    {
        Assert.Equal("普通回复", ChannelText.DescribeReplies(false, false));
        Assert.Equal("流式回复", ChannelText.DescribeReplies(true, false));
        Assert.Equal("流式回复 + 语音回复", ChannelText.DescribeReplies(true, true));

        var channel = new ChannelSummary("id", "Name", "", "feishu", "飞书", "feishu", "", "cli_app", false,
            true, false, "Cherry", [], true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Equal("未配置", channel.SecretText);
        Assert.Equal("已启用", channel.StateText);
        Assert.Equal("已配置（留空表示保持）", (channel with { HasAppSecret = true }).SecretText);
        Assert.Equal("已停用", (channel with { IsEnabled = false }).StateText);

        Assert.Contains("只能改名", ChannelText.ProviderNotice, StringComparison.Ordinal);
        Assert.Contains("Cherry", ChannelText.TtsVoiceHint, StringComparison.Ordinal);
    }
}