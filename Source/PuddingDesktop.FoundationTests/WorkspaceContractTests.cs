using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-05 workspace slice: the access vocabulary and the built-in workspace rule.</summary>
public sealed class WorkspaceContractTests
{
    private static WorkspaceCreateRequest Create() => new(
        "team-a-space", "team-a", "Team A Space", "notes", "{\"theme\":\"dark\"}", "Manage", "ReadOnly");

    private static WorkspaceEdit Edit(string workspaceId = "team-a-space", bool isEnabled = true) => new(
        workspaceId, "Team A Space", "notes", "", "Manage", "ReadOnly", isEnabled);

    [Fact]
    public void AccessLevelsMatchTheCoreEnum()
    {
        Assert.Equal(["None", "ReadOnly", "Write", "Manage"], WorkspaceText.AccessLevels);
        Assert.Contains("只读", WorkspaceText.DescribeAccessLevel("readonly"), StringComparison.Ordinal);
        Assert.Equal("未设置", WorkspaceText.DescribeAccessLevel(null));
        // 未知取值原样显示，不猜一个名字。
        Assert.Equal("Owner", WorkspaceText.DescribeAccessLevel("Owner"));
        Assert.True(WorkspaceText.IsAccessLevel("manage"));
        Assert.False(WorkspaceText.IsAccessLevel("Owner"));
    }

    [Fact]
    public void WorkspaceIdStaysConservativeBecauseItBecomesASlug()
    {
        Assert.Empty(WorkspaceText.ValidateWorkspaceId("team-a_space"));
        Assert.Contains("不能为空", WorkspaceText.ValidateWorkspaceId(" ").Single(), StringComparison.Ordinal);
        Assert.Contains("只能包含", WorkspaceText.ValidateWorkspaceId("team a").Single(), StringComparison.Ordinal);
        Assert.Contains("只能包含", WorkspaceText.ValidateWorkspaceId("team/a").Single(), StringComparison.Ordinal);
        Assert.NotEmpty(WorkspaceText.ValidateWorkspaceId(new string('a', 65)));
    }

    [Fact]
    public void CreateRequiresATeamNameAndAKnownPolicy()
    {
        Assert.Empty(WorkspaceText.Validate(Create()));
        Assert.Contains("团队", WorkspaceText.Validate(Create() with { TeamId = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("名称", WorkspaceText.Validate(Create() with { Name = " " }).Single(), StringComparison.Ordinal);
        Assert.Contains("团队访问策略", WorkspaceText.Validate(Create() with { TeamAccessPolicy = "Owner" }).Single(), StringComparison.Ordinal);
        Assert.Contains("公司访问策略", WorkspaceText.Validate(Create() with { CompanyAccessPolicy = "99" }).Single(), StringComparison.Ordinal);
        Assert.Contains("JSON", WorkspaceText.Validate(Create() with { UserProfile = "{not json" }).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void EditRejectsWhatCoreRejectsIncludingDisablingTheBuiltInWorkspace()
    {
        Assert.Empty(WorkspaceText.Validate(Edit()));
        Assert.Contains("名称", WorkspaceText.Validate(Edit() with { Name = "" }).Single(), StringComparison.Ordinal);
        var builtIn = WorkspaceText.Validate(Edit("default", isEnabled: false));
        Assert.Contains("默认工作空间", builtIn.Single(), StringComparison.Ordinal);
        Assert.Empty(WorkspaceText.Validate(Edit("default", isEnabled: true)));
        Assert.True(WorkspaceText.IsJson("{}"));
        Assert.False(WorkspaceText.IsJson("{"));
        // 空串本身不是合法 JSON；校验只在非空时调用它，留空由"留空表示不设置"这条规则处理。
        Assert.False(WorkspaceText.IsJson(""));
        Assert.Contains("不可删除", WorkspaceText.DefaultWorkspaceNotice, StringComparison.Ordinal);
        Assert.Contains("独立状态", WorkspaceText.FrozenNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void StateTextKeepsDisabledAndFrozenSeparate()
    {
        var workspace = new WorkspaceSummary("team-a-space", "team-a-space", "team-a", "Team A", "Name", "", "",
            "Manage", "ReadOnly", true, false, 2, DateTimeOffset.UtcNow);
        Assert.Equal("启用中", workspace.StateText);
        Assert.Equal("已冻结", (workspace with { IsFrozen = true }).StateText);
        Assert.Equal("已停用", (workspace with { IsEnabled = false, IsFrozen = true }).StateText);
        Assert.False(workspace.IsBuiltInDefault);
        Assert.True((workspace with { WorkspaceId = "default" }).IsBuiltInDefault);
        Assert.Equal("ReadOnly（只读）", new WorkspaceMember(1, "u", "user", "", "ReadOnly").AccessText);
    }
}