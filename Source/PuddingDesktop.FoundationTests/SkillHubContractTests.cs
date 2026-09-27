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
    private static SkillHubEvoMap Map(params EvoMapNode[] nodes) => new(nodes,
        nodes.Where(node => node.ParentNodeId.Length > 0)
            .Select(node => new EvoMapEdge(node.ParentNodeId, node.NodeId, node.EvolutionAction)).ToArray(),
        DateTimeOffset.UtcNow);

    private static EvoMapNode Node(string version, string parent = "", string action = "patch", string status = "active") =>
        new($"pudding-a@{version}", "pudding-a", version, action, parent, "A", status, "", DateTimeOffset.UtcNow, 100, 1);

    [Fact]
    public void LineageRendersAsATreeWithParentChildOrdering()
    {
        var map = Map(Node("1.0.0", action: "create"), Node("1.1.0", "pudding-a@1.0.0"), Node("1.2.0", "pudding-a@1.1.0"));
        var lines = SkillHubText.RenderLineage(map);
        Assert.Equal(3, lines.Count);
        Assert.StartsWith("1.0.0 · create", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("  1.1.0 · patch", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("    1.2.0 · patch", lines[2], StringComparison.Ordinal);
        Assert.Contains("节点 3 · 边 2 · 根 1", SkillHubText.DescribeLineage(map), StringComparison.Ordinal);
    }

    [Fact]
    public void LineageStopsAtCyclesAndReportsMissingParentsInsteadOfLooping()
    {
        var cyclic = Map(
            new EvoMapNode("a@1", "a", "1", "patch", "a@2", "A", "active", "", DateTimeOffset.UtcNow, 1, 0),
            new EvoMapNode("a@2", "a", "2", "patch", "a@1", "A", "active", "", DateTimeOffset.UtcNow, 1, 0));
        var lines = SkillHubText.RenderLineage(cyclic);
        Assert.Contains(lines, line => line.Contains("无根组件", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("谱系存在环", StringComparison.Ordinal));
        Assert.Equal(3, lines.Count);
        Assert.Contains("根 0", SkillHubText.DescribeLineage(cyclic), StringComparison.Ordinal);

        var orphan = Map(Node("2.0.0", "pudding-a@9.9.9"));
        var orphanLines = SkillHubText.RenderLineage(orphan);
        Assert.Contains("父节点不在结果集中", orphanLines[0], StringComparison.Ordinal);
        Assert.Contains("父节点缺失 1", SkillHubText.DescribeLineage(orphan), StringComparison.Ordinal);

        // An edge whose endpoints are outside the node set is counted, not silently dropped.
        var dangling = new SkillHubEvoMap([Node("1.0.0", action: "create")],
            [new EvoMapEdge("gone@1", "pudding-a@1.0.0", "patch")], DateTimeOffset.UtcNow);
        Assert.Contains("悬空边 1", SkillHubText.DescribeLineage(dangling), StringComparison.Ordinal);
        Assert.Empty(SkillHubText.RenderLineage(new SkillHubEvoMap([], [], DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void UpdateBehindIsDecidedByStringDifferenceNotGuessedOrdering()
    {
        var behind = new SkillHubUpdate("pudding-a", "A", "1.0.0", "1.1.0", "patch", DateTimeOffset.UtcNow, "note");
        Assert.True(behind.IsBehind);
        Assert.False((behind with { LatestVersion = "1.0.0" }).IsBehind);
        // A non-semver pair is reported as behind when the strings differ; the page never interprets it.
        var odd = behind with { InstalledVersion = "v1", LatestVersion = "release-2" };
        Assert.True(odd.IsBehind);
        Assert.Contains("已登记 v1 → 最新 release-2", SkillHubText.DescribeUpdate(odd), StringComparison.Ordinal);
        Assert.Contains("note", SkillHubText.DescribeUpdate(behind), StringComparison.Ordinal);
        Assert.DoesNotContain("· note", SkillHubText.DescribeUpdate(behind with { PublishNote = "" }), StringComparison.Ordinal);
    }
    [Fact]
    public void SkillPackageFilesFollowTheCoreRuleAndNeverWidenIt()
    {
        Assert.Equal([".zip", ".tar.gz"], SkillPackageText.AllowedExtensions);
        Assert.True(SkillPackageText.IsAllowedFile("pack.zip"));
        Assert.True(SkillPackageText.IsAllowedFile("PACK.TAR.GZ"));
        Assert.False(SkillPackageText.IsAllowedFile("pack.tgz"), "卡片提到 .tgz，但 Core 不接受，桌面端不得擅自放宽");
        Assert.False(SkillPackageText.IsAllowedFile("pack.rar"));
        Assert.False(SkillPackageText.IsAllowedFile(null));
    }

    [Fact]
    public void SkillPackageFormsRejectWhatCoreWouldReject()
    {
        var meta = new SkillPackageMetaEdit("pack", "Name", "desc", true, 100);
        Assert.Empty(SkillPackageText.Validate(meta));
        Assert.Contains("技能包", SkillPackageText.Validate(meta with { SkillPackageId = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("名称", SkillPackageText.Validate(meta with { Name = " " }).Single(), StringComparison.Ordinal);
        Assert.Contains("排序", SkillPackageText.Validate(meta with { SortOrder = -1 }).Single(), StringComparison.Ordinal);

        var upload = new SkillPackageUploadEdit("pack", "Name", "desc", "1.0.0", 100, "/tmp/pack.zip");
        Assert.Empty(SkillPackageText.Validate(upload));
        Assert.Contains("小写", SkillPackageText.Validate(upload with { SkillPackageId = "Pack" }).Single(), StringComparison.Ordinal);
        Assert.Contains("小写", SkillPackageText.Validate(upload with { SkillPackageId = "pack_one" }).Single(), StringComparison.Ordinal);
        Assert.Contains("包文件", SkillPackageText.Validate(upload with { FilePath = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("格式", SkillPackageText.Validate(upload with { FilePath = "/tmp/pack.tgz" }).Single(), StringComparison.Ordinal);

        var replace = new SkillPackageFileEdit("pack", "2.0.0", "/tmp/pack.zip");
        Assert.Empty(SkillPackageText.Validate(replace));
        Assert.Contains("版本", SkillPackageText.Validate(replace with { Version = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("格式", SkillPackageText.Validate(replace with { FilePath = "/tmp/x.rar" }).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void PackageSizesAreRenderedReadably()
    {
        Assert.Equal("512 B", SkillPackageText.FormatBytes(512));
        Assert.Equal("1.5 KB", SkillPackageText.FormatBytes(1536));
        Assert.Equal("2.5 MB", SkillPackageText.FormatBytes(2_621_440));
        Assert.Equal("1.5 GB", SkillPackageText.FormatBytes(1_610_612_736));
        Assert.Equal("未知大小", SkillPackageText.FormatBytes(-1));
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
