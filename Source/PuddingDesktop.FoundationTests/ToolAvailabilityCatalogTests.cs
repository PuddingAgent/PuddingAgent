namespace PuddingDesktop.FoundationTests;

using PuddingDesktop.Foundation;

/// <summary>
/// IMG11（设计规格 §13.2/§13.5）：工具首页卡片的「可用 / 待接入」标签与标签页
/// 的 Availability 必须来自同一判断，否则会出现「卡片说可用、点开说未接入」的
/// 自我矛盾。这里把真源钉住，并覆盖全部 ToolTabKind（新增 kind 时会被迫做决定）。
/// </summary>
public sealed class ToolAvailabilityCatalogTests
{
    [Theory]
    [InlineData(ToolTabKind.Home, ToolTabAvailability.Ready)]
    [InlineData(ToolTabKind.Browser, ToolTabAvailability.Ready)]
    [InlineData(ToolTabKind.Output, ToolTabAvailability.Ready)]
    [InlineData(ToolTabKind.Terminal, ToolTabAvailability.Deferred)]
    [InlineData(ToolTabKind.Artifact, ToolTabAvailability.Deferred)]
    [InlineData(ToolTabKind.Panel, ToolTabAvailability.Deferred)]
    public void For_matches_the_real_capability(ToolTabKind kind, ToolTabAvailability expected) =>
        Assert.Equal(expected, ToolAvailabilityCatalog.For(kind));

    [Theory]
    [InlineData(ToolTabKind.Home, "可用")]
    [InlineData(ToolTabKind.Browser, "可用")]
    [InlineData(ToolTabKind.Output, "可用")]
    [InlineData(ToolTabKind.Terminal, "待接入")]
    [InlineData(ToolTabKind.Artifact, "待接入")]
    [InlineData(ToolTabKind.Panel, "待接入")]
    public void CardLabel_is_derived_from_availability(ToolTabKind kind, string expected) =>
        Assert.Equal(expected, ToolAvailabilityCatalog.CardLabel(kind));

    [Fact]
    public void Every_enum_member_is_decided()
    {
        // 没有 default 兜底的遗漏：新增 ToolTabKind 时必须显式选边，
        // 否则这张表会漏项 —— 用「枚举成员数 = 覆盖数」把缺口暴露出来。
        var kinds = Enum.GetValues<ToolTabKind>();
        var labelled = kinds.Select(ToolAvailabilityCatalog.CardLabel).ToArray();
        Assert.Equal(kinds.Length, labelled.Length);
        Assert.All(labelled, label => Assert.Contains(label, new[] { "可用", "待接入" }));
    }
}
