using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-12 team slice: the member/access vocabularies and Core's team-deletion guard.</summary>
public sealed class TeamContractTests
{
    [Fact]
    public void VocabulariesMatchTheCoreService()
    {
        Assert.Equal(["Member", "Admin"], TeamContractsText.MemberRoles);
        Assert.Equal(["None", "ReadOnly", "Write", "Manage"], TeamContractsText.AccessLevels);
        // None 是合法枚举值但不是可用的白名单级别。
        Assert.Equal(["ReadOnly", "Write", "Manage"], TeamContractsText.WhitelistLevels);
        Assert.True(TeamContractsText.IsKnownAccessLevel("None"));
        Assert.False(TeamContractsText.IsUsableWhitelistLevel("None"));
        Assert.False(TeamContractsText.IsKnownAccessLevel("Owner"));
        Assert.Contains("团队管理员", TeamContractsText.DescribeMemberRole("admin"), StringComparison.Ordinal);
        Assert.Equal("角色未知", TeamContractsText.DescribeMemberRole(null));
    }

    [Fact]
    public void TeamIdsStayConservativeAndNamesAreRequired()
    {
        Assert.Empty(TeamContractsText.ValidateTeamId("team-a_space.v2"));
        Assert.Contains("不能为空", TeamContractsText.ValidateTeamId(" ").Single(), StringComparison.Ordinal);
        Assert.Contains("只能包含", TeamContractsText.ValidateTeamId("team/a").Single(), StringComparison.Ordinal);

        var edit = new TeamEdit("team-a", "Team A", "", true);
        Assert.Empty(TeamContractsText.Validate(edit, isCreate: false));
        // 新建必须给 TeamId；更新沿用已有 ID。
        Assert.Contains("TeamId", TeamContractsText.Validate(edit with { TeamId = "" }, isCreate: true).Single(), StringComparison.Ordinal);
        Assert.Empty(TeamContractsText.Validate(edit with { TeamId = "" }, isCreate: false));
        Assert.Contains("名称", TeamContractsText.Validate(edit with { Name = "" }, isCreate: false).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void MemberAndWhitelistFormsRejectWhatCoreRejects()
    {
        var member = new TeamMemberAdd("team-a", "alice", "Member");
        Assert.Empty(TeamContractsText.Validate(member));
        Assert.Contains("用户", TeamContractsText.Validate(member with { UserId = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("团队角色", TeamContractsText.Validate(member with { Role = "Owner" }).Single(), StringComparison.Ordinal);
        Assert.Contains("团队", TeamContractsText.Validate(member with { TeamId = "" }).Single(), StringComparison.Ordinal);

        var whitelist = new TeamWorkspaceMemberAdd("ws", "alice", "Write");
        Assert.Empty(TeamContractsText.Validate(whitelist));
        // Core 明确拒绝 None；表单按白名单词表先拦。
        Assert.Contains("不能用 None", TeamContractsText.Validate(whitelist with { AccessLevel = "None" }).Single(), StringComparison.Ordinal);
        Assert.Contains("访问级别", TeamContractsText.Validate(whitelist with { AccessLevel = "Superuser" }).Single(), StringComparison.Ordinal);
        Assert.Contains("工作区", TeamContractsText.Validate(whitelist with { WorkspaceId = "" }).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceDraftValidationCoversBothPolicies()
    {
        var draft = new TeamWorkspaceEdit("team-a", "ws-1", "Name", "d", "", "Manage", "ReadOnly", true);
        Assert.Empty(TeamContractsText.Validate(draft, isCreate: true));
        Assert.Contains("工作区名称", TeamContractsText.Validate(draft with { Name = "" }, isCreate: true).Single(), StringComparison.Ordinal);
        var badTeamPolicy = TeamContractsText.Validate(draft with { TeamAccessPolicy = "Owner" }, isCreate: true);
        Assert.Contains("团队访问策略", badTeamPolicy.Single(), StringComparison.Ordinal);
        var badCompanyPolicy = TeamContractsText.Validate(draft with { CompanyAccessPolicy = "99" }, isCreate: true);
        Assert.Contains("公司访问策略", badCompanyPolicy.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void SummariesSayWhatCanBeDone()
    {
        var team = new TeamSummary(1, "team-a", "Team A", "", true, 3, 0, DateTimeOffset.UtcNow);
        Assert.True(team.CanDelete);
        Assert.False((team with { WorkspaceCount = 2 }).CanDelete);
        Assert.Contains("成员 3", team.CountsText, StringComparison.Ordinal);
        Assert.Equal("已停用", (team with { IsEnabled = false }).StateText);

        var workspace = new TeamWorkspace(1, "team-a-space", "team-a-space", "team-a", "Team A", "Space", "", "",
            "Manage", "ReadOnly", true, false, 2, DateTimeOffset.UtcNow);
        Assert.Equal("启用中", workspace.StateText);
        Assert.False(workspace.IsBuiltInDefault);
        Assert.Contains("只读", workspace.PolicyText, StringComparison.Ordinal);
        Assert.True((workspace with { WorkspaceId = "default" }).IsBuiltInDefault);
        Assert.Equal("已冻结", (workspace with { IsFrozen = true }).StateText);
        Assert.Equal("已停用", (workspace with { IsEnabled = false, IsFrozen = true }).StateText);

        Assert.Contains("不能删除团队", TeamContractsText.TeamDeleteNotice, StringComparison.Ordinal);
        Assert.Contains("不能用 None", TeamContractsText.WhitelistNotice, StringComparison.Ordinal);
        Assert.Contains("默认工作空间", TeamContractsText.DefaultWorkspaceNotice, StringComparison.Ordinal);
    }
}