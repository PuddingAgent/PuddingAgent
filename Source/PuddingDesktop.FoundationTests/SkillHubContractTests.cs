using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-07 overview + events slice: status/event vocabulary and the retirement rule.</summary>
public sealed class SkillHubContractTests
{
    [Fact]
    public void StatusVocabularyNeverClaimsARetiredSkillIsUsable()
    {
        Assert.Equal("启用中", SkillHubText.DescribeStatus("active"));
        Assert.Equal("已退役", SkillHubText.DescribeStatus("retired"));
        Assert.Equal("状态未知", SkillHubText.DescribeStatus(null));
        Assert.Equal("something-else", SkillHubText.DescribeStatus("something-else"));
        Assert.True(SkillHubText.IsUsable("active"));
        Assert.False(SkillHubText.IsUsable("retired"));
        Assert.False(SkillHubText.IsUsable("RETIRED"), "退役判定必须大小写无关");
        Assert.True(SkillHubText.IsUsable("draft"));
    }

    [Fact]
    public void EventTypesTranslateButUnknownOnesAreShownVerbatim()
    {
        Assert.Equal("技能发布", SkillHubText.DescribeEventType("skill.published"));
        Assert.Equal("版本发布", SkillHubText.DescribeEventType("skill.version_published"));
        Assert.Equal("技能退役", SkillHubText.DescribeEventType("skill.retired"));
        Assert.Equal("未知事件", SkillHubText.DescribeEventType(""));
        Assert.Equal("skill.future_thing", SkillHubText.DescribeEventType("skill.future_thing"));
    }

    [Fact]
    public void ActorAndSkillReferencesAreRenderedWithoutInventingValues()
    {
        Assert.Equal("Agent · agent-1", SkillHubText.DescribeActor("agent", "agent-1"));
        Assert.Equal("系统", SkillHubText.DescribeActor("system", null));
        Assert.Equal("未知来源", SkillHubText.DescribeActor(null, null));
        Assert.Equal("custom-kind", SkillHubText.DescribeActor("custom-kind", ""));
        Assert.Equal("pudding.a@1.0.0", SkillHubText.DescribeSkillReference("pudding.a", "1.0.0"));
        Assert.Equal("pudding.a", SkillHubText.DescribeSkillReference("pudding.a", ""));
        Assert.Equal("（无技能）", SkillHubText.DescribeSkillReference(null, "1.0.0"));
    }

    [Fact]
    public void EventSearchCoversTheFieldsThePageShows()
    {
        var entry = new SkillHubEvent(7, "pudding.code-search", "1.0.0", "skill.published",
            "agent", "agent-1", "default", "{\"note\":\"hello\"}", DateTimeOffset.UtcNow);
        Assert.True(SkillHubText.MatchesEvent(entry, null));
        Assert.True(SkillHubText.MatchesEvent(entry, "  "));
        Assert.True(SkillHubText.MatchesEvent(entry, "code-search"));
        Assert.True(SkillHubText.MatchesEvent(entry, "SKILL.PUBLISHED"));
        Assert.True(SkillHubText.MatchesEvent(entry, "agent-1"));
        Assert.True(SkillHubText.MatchesEvent(entry, "hello"));
        Assert.False(SkillHubText.MatchesEvent(entry, "nothing-matches-this"));
    }

    [Fact]
    public void EmptyOverviewIsExplicitlyEmptyRatherThanFabricated()
    {
        Assert.Equal(0, SkillHubOverview.Empty.TotalSkills);
        Assert.Empty(SkillHubOverview.Empty.TopInstalled);
        Assert.Empty(SkillHubOverview.Empty.EvolutionActionCounts);
        Assert.Equal(DateTimeOffset.MinValue, SkillHubOverview.Empty.GeneratedAt);
        Assert.Contains(20, SkillHubText.EventPageSizes);
        Assert.Contains(200, SkillHubText.EventPageSizes);
    }
}
