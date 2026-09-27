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
    public void VocabularyMatchesCoresOwnWhitelists()
    {
        Assert.Equal(["create", "patch", "split", "compress", "retire", "merge", "fork"], SkillHubText.EvolutionActions);
        Assert.Equal(["active", "deprecated", "retired"], SkillHubText.Statuses);
        Assert.Equal(["global", "workspace"], SkillHubText.Visibilities);
    }

    [Fact]
    public void SkillIdRulesMirrorCoresPattern()
    {
        Assert.Empty(SkillHubText.ValidateSkillId("pudding-code-search"));
        Assert.Empty(SkillHubText.ValidateSkillId("ab"));
        Assert.Contains("技能 ID", SkillHubText.ValidateSkillId(null).Single(), StringComparison.Ordinal);
        Assert.Contains("技能 ID", SkillHubText.ValidateSkillId("").Single(), StringComparison.Ordinal);
        // Core rejects dots, uppercase and a leading dash; the form must reject them first.
        Assert.NotEmpty(SkillHubText.ValidateSkillId("pudding.code-search"));
        Assert.NotEmpty(SkillHubText.ValidateSkillId("Pudding-Code"));
        Assert.NotEmpty(SkillHubText.ValidateSkillId("-leading"));
        Assert.NotEmpty(SkillHubText.ValidateSkillId("a"));
        Assert.NotEmpty(SkillHubText.ValidateSkillId(new string('a', 129)));
    }

    [Fact]
    public void MetaEditIsRejectedForValuesOutsideCoresVocabulary()
    {
        var valid = new SkillHubMetaEdit("Name", "summary", "description", ["a"], ["b"], "active", "global");
        Assert.Empty(SkillHubText.Validate(valid));
        Assert.Empty(SkillHubText.Validate(valid with { Status = "retired" }));
        Assert.Contains("状态", SkillHubText.Validate(valid with { Status = "archived" }).Single(), StringComparison.Ordinal);
        Assert.Contains("可见性", SkillHubText.Validate(valid with { Visibility = "public" }).Single(), StringComparison.Ordinal);
        Assert.Contains("名称", SkillHubText.Validate(valid with { Name = " " }).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void VersionPublishRequiresAParentForEveryActionExceptCreate()
    {
        var create = new SkillHubVersionPublish("pudding-a", "A", "1.0.0", "# A", "create", "", "", [], "global");
        Assert.Empty(SkillHubText.Validate(create));

        var patch = create with { EvolutionAction = "patch", ParentVersion = "1.0.0", Version = "1.1.0" };
        Assert.Empty(SkillHubText.Validate(patch));
        Assert.Contains("父版本", SkillHubText.Validate(patch with { ParentVersion = "" }).Single(), StringComparison.Ordinal);

        Assert.Contains("进化动作", SkillHubText.Validate(create with { EvolutionAction = "refine", ParentVersion = "1.0.0" }).Single(), StringComparison.Ordinal);
        // 非法动作与被漏掉的父版本会一起报出来，而不是一次只报一条。
        Assert.Equal(2, SkillHubText.Validate(create with { EvolutionAction = "refine" }).Count);
        Assert.Contains("版本号", SkillHubText.Validate(create with { Version = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("Markdown", SkillHubText.Validate(create with { SkillMarkdown = "  " }).Single(), StringComparison.Ordinal);
        Assert.Contains("技能 ID", SkillHubText.Validate(create with { SkillId = "pudding.a" }).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void InstallRegistrationNeedsASkillAgentAndVersion()
    {
        var valid = new SkillHubInstallRegistration("pudding-a", "default.agent_1", "default", "1.0.0", "", "tester");
        Assert.Empty(SkillHubText.Validate(valid));
        Assert.Contains("Agent 实例", SkillHubText.Validate(valid with { AgentInstanceId = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("已安装版本", SkillHubText.Validate(valid with { InstalledVersion = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("台账", SkillHubText.InstallLedgerNotice, StringComparison.Ordinal);
        Assert.Contains("不代表", SkillHubText.InstallLedgerNotice, StringComparison.Ordinal);
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
