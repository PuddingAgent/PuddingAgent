using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-04 Smart slice: route format, parsing and the seven role slots.</summary>
public sealed class AgentSmartRouteContractTests
{
    [Fact]
    public void TheSevenSmartRolesMatchCoresRoleIds()
    {
        Assert.Equal(["explorer", "researcher", "planner", "reviewer", "developer", "deployer", "tester"],
            SmartRoleRoutes.Roles.Select(slot => slot.RoleId));
        Assert.Equal(7, SmartRoleRoutes.Roles.Select(slot => slot.Title).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("explorer", SmartRoleRoutes.Find("EXPLORER")!.RoleId);
        Assert.Equal("tester", SmartRoleRoutes.Find("tester")!.RoleId);
        // 显意识/潜意识不是 Smart 子代理角色。
        Assert.Null(SmartRoleRoutes.Find("conscious"));
        Assert.Equal("{providerId}/{modelId}", SmartRoleRoutes.Format);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("deepseek")]
    [InlineData("/deepseek-chat")]
    [InlineData("deepseek/")]
    [InlineData(" / ")]
    public void UnsetOrMalformedRoutesParseToNothing(string? route) => Assert.Null(SmartRoleRoutes.Parse(route));

    [Fact]
    public void RoutesUseTheSameSplitAsCore()
    {
        Assert.Equal(("deepseek", "deepseek-chat"), SmartRoleRoutes.Parse("deepseek/deepseek-chat"));
        Assert.Equal(("deepseek", "deepseek-chat"), SmartRoleRoutes.Parse("  deepseek / deepseek-chat  "));
        // Core splits on the FIRST slash, so a model id may itself contain one.
        Assert.Equal(("openrouter", "vendor/model"), SmartRoleRoutes.Parse("openrouter/vendor/model"));
    }

    [Fact]
    public void ChoiceRoundTripsAndNeverInventsARoute()
    {
        Assert.Equal("deepseek/deepseek-chat", SmartRoleRoutes.FromChoice(new AgentModelChoice("deepseek", "deepseek-chat")));
        Assert.Equal("", SmartRoleRoutes.FromChoice(AgentModelChoice.None));
        // 半填的选择不能被写成一条路由。
        Assert.Equal("", SmartRoleRoutes.FromChoice(new AgentModelChoice("deepseek", "")));
        Assert.Equal(AgentModelChoice.None, SmartRoleRoutes.ToChoice(""));
        Assert.Equal(new AgentModelChoice("a", "b"), SmartRoleRoutes.ToChoice("a/b"));
    }

    [Fact]
    public void ValidationMirrorsCoresNormalizer()
    {
        Assert.Empty(SmartRoleRoutes.Validate(null, "探索者"));
        Assert.Empty(SmartRoleRoutes.Validate("", "探索者"));
        Assert.Empty(SmartRoleRoutes.Validate("a/b", "探索者"));
        Assert.Empty(SmartRoleRoutes.Validate(" a/b ", "探索者"));
        Assert.Contains("{providerId}/{modelId}", SmartRoleRoutes.Validate("ab", "探索者").Single(), StringComparison.Ordinal);
        Assert.Contains("模型 ID", SmartRoleRoutes.Validate("a/", "探索者").Single(), StringComparison.Ordinal);
        Assert.Contains("探索者", SmartRoleRoutes.Validate("ab", "探索者").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeNeverPretendsAMalformedRouteIsRouted()
    {
        Assert.Contains("未设置", SmartRoleRoutes.Describe(null), StringComparison.Ordinal);
        Assert.Contains("未设置", SmartRoleRoutes.Describe("  "), StringComparison.Ordinal);
        Assert.Contains("deepseek / deepseek-chat", SmartRoleRoutes.Describe("deepseek/deepseek-chat"), StringComparison.Ordinal);
        Assert.Contains("格式", SmartRoleRoutes.Describe("deepseek"), StringComparison.Ordinal);
    }
}
