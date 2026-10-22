namespace PuddingDesktop.FoundationTests;

using PuddingDesktop.Foundation;

/// <summary>
/// IMG11（设计规格 §13.2/§13.5）：工具首页卡片的标签、说明文字与可点状态，必须与
/// 标签页的 Availability 来自同一判断，否则会出现「卡片说可用、点开说未接入」
/// （或反过来：能用的能力被写成待接入）的自我矛盾。这里把真源钉住，并覆盖全部
/// ToolTabKind（新增 kind 时会被迫做决定）。
/// </summary>
public sealed class ToolAvailabilityCatalogTests
{
    [Theory]
    [InlineData(ToolTabKind.Home, ToolTabAvailability.Ready)]
    [InlineData(ToolTabKind.Browser, ToolTabAvailability.Ready)]
    [InlineData(ToolTabKind.Output, ToolTabAvailability.Ready)]
    [InlineData(ToolTabKind.Terminal, ToolTabAvailability.Deferred)]
    [InlineData(ToolTabKind.Artifact, ToolTabAvailability.Ready)]
    [InlineData(ToolTabKind.Panel, ToolTabAvailability.Ready)]
    [InlineData(ToolTabKind.Document, ToolTabAvailability.Ready)]
    public void For_matches_the_real_capability(ToolTabKind kind, ToolTabAvailability expected) =>
        Assert.Equal(expected, ToolAvailabilityCatalog.For(kind));

    [Theory]
    [InlineData(ToolTabKind.Home, "可用")]
    [InlineData(ToolTabKind.Browser, "可用")]
    [InlineData(ToolTabKind.Output, "可用")]
    [InlineData(ToolTabKind.Terminal, "待接入")]
    [InlineData(ToolTabKind.Artifact, "可用")]
    [InlineData(ToolTabKind.Panel, "可用")]
    [InlineData(ToolTabKind.Document, "可用")]
    public void CardLabel_is_derived_from_availability(ToolTabKind kind, string expected) =>
        Assert.Equal(expected, ToolAvailabilityCatalog.CardLabel(kind));

    /// <summary>
    /// 用户 UX 清单第 ⑩ 条的原症状：交互面板芯片是「可用」，副标题却残留
    /// 「（等待接入）」；「新建终端」写着待接入却没有禁用。这里用两条互斥规则把
    /// 说明文字钉到可用性上，任何一侧漂移都会取红。
    /// </summary>
    [Fact]
    public void CardSubtitle_never_contradicts_availability()
    {
        string[] deferredWords = { "待接入", "等待接入", "尚未接入", "暂未接入" };

        foreach (var kind in Enum.GetValues<ToolTabKind>())
        {
            var subtitle = ToolAvailabilityCatalog.CardSubtitle(kind);
            Assert.False(string.IsNullOrWhiteSpace(subtitle), $"{kind} 没有卡片说明文字");

            if (ToolAvailabilityCatalog.For(kind) == ToolTabAvailability.Deferred)
            {
                // 待接入必须说清缺什么，不能只留一句「暂不可用」。
                Assert.Contains("接入", subtitle);
            }
            else
            {
                // 可用却不许自称待接入 —— 这正是交互面板卡片原先的谎。
                Assert.DoesNotContain(
                    deferredWords,
                    word => subtitle.Contains(word, StringComparison.Ordinal));
            }
        }
    }

    [Theory]
    [InlineData(ToolTabKind.Home)]
    [InlineData(ToolTabKind.Browser)]
    [InlineData(ToolTabKind.Output)]
    [InlineData(ToolTabKind.Terminal)]
    [InlineData(ToolTabKind.Artifact)]
    [InlineData(ToolTabKind.Panel)]
    [InlineData(ToolTabKind.Document)]
    public void IsEntryEnabled_follows_availability(ToolTabKind kind) =>
        Assert.Equal(
            ToolAvailabilityCatalog.For(kind) == ToolTabAvailability.Ready,
            ToolAvailabilityCatalog.IsEntryEnabled(kind));

    [Fact]
    public void Every_enum_member_is_decided()
    {
        // 没有 default 兜底的遗漏：新增 ToolTabKind 时必须显式选边，
        // 否则这张表会漏项 —— 用「枚举成员数 = 覆盖数」把缺口暴露出来。
        var kinds = Enum.GetValues<ToolTabKind>();
        var labelled = kinds.Select(ToolAvailabilityCatalog.CardLabel).ToArray();
        Assert.Equal(kinds.Length, labelled.Length);
        Assert.All(labelled, label => Assert.Contains(label, new[] { "可用", "待接入" }));
        Assert.All(kinds, kind => Assert.False(string.IsNullOrWhiteSpace(ToolAvailabilityCatalog.CardSubtitle(kind))));
    }
}
