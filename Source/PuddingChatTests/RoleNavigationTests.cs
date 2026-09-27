using PuddingChat;

namespace PuddingChatTests;

/// <summary>
/// The host-owned role sidebar's pure rules. These decide what the sidebar shows and what it may select;
/// the WinUI shell only renders the result.
/// </summary>
public class RoleNavigationTests
{
    private static Workspace Workspace(string id) => new(id, "工作区 " + id);
    private static Agent Agent(string id, string? name = null, bool enabled = true, bool frozen = false, string? avatar = null) =>
        new(id, name ?? id, DisplayName: null, Description: "职责 " + id, AvatarUrl: avatar,
            SourceTemplateId: null, MainSessionId: "session-" + id, IsEnabled: enabled, IsFrozen: frozen);
    private static AgentStatus Status(string id, string status, string summary, int unread = 0) =>
        new(id, status, summary, unread);

    [Fact]
    public void EmptyWorkspacesProduceEmptySidebar()
    {
        var snapshot = RoleNavigation.Build([], new Dictionary<string, IReadOnlyList<Agent>>(),
            new Dictionary<string, IReadOnlyList<AgentStatus>>());

        Assert.Empty(snapshot.Items);
        Assert.Equal(0, snapshot.DuplicateAgentsDropped);
    }

    [Fact]
    public void OrderFollowsWorkspaceThenAgentOrderWithoutReSorting()
    {
        var snapshot = RoleNavigation.Build(
            [Workspace("w2"), Workspace("w1")],
            new Dictionary<string, IReadOnlyList<Agent>>
            {
                ["w2"] = [Agent("z"), Agent("a")],
                ["w1"] = [Agent("m")],
            },
            new Dictionary<string, IReadOnlyList<AgentStatus>>());

        Assert.Equal(["z", "a", "m"], snapshot.Items.Select(item => item.Agent.AgentId));
        Assert.Equal(["w2", "w2", "w1"], snapshot.Items.Select(item => item.Workspace.WorkspaceId));
    }

    [Fact]
    public void StatusIsMatchedByAgentIdAndAnAbsentEntryStaysNull()
    {
        var snapshot = RoleNavigation.Build(
            [Workspace("w1")],
            new Dictionary<string, IReadOnlyList<Agent>> { ["w1"] = [Agent("a"), Agent("b")] },
            new Dictionary<string, IReadOnlyList<AgentStatus>> { ["w1"] = [Status("a", "运行中", "正在执行 1 项")] });

        Assert.Equal("运行中", snapshot.Items[0].Status!.Status);
        Assert.Null(snapshot.Items[1].Status);
    }

    [Fact]
    public void StatusFromAnotherWorkspaceIsNotBorrowed()
    {
        var snapshot = RoleNavigation.Build(
            [Workspace("w1")],
            new Dictionary<string, IReadOnlyList<Agent>> { ["w1"] = [Agent("a")] },
            new Dictionary<string, IReadOnlyList<AgentStatus>> { ["w2"] = [Status("a", "运行中", "别人的状态")] });

        Assert.Null(snapshot.Items[0].Status);
    }

    [Fact]
    public void MissingAgentListForAWorkspaceIsTolerated()
    {
        var snapshot = RoleNavigation.Build(
            [Workspace("w1"), Workspace("w2")],
            new Dictionary<string, IReadOnlyList<Agent>> { ["w2"] = [Agent("b")] },
            new Dictionary<string, IReadOnlyList<AgentStatus>>());

        Assert.Single(snapshot.Items);
        Assert.Equal("b", snapshot.Items[0].Agent.AgentId);
    }

    [Fact]
    public void SameAgentIdInDifferentWorkspacesStaysTwoDistinctRows()
    {
        var snapshot = RoleNavigation.Build(
            [Workspace("w1"), Workspace("w2")],
            new Dictionary<string, IReadOnlyList<Agent>>
            {
                ["w1"] = [Agent("shared")],
                ["w2"] = [Agent("shared")],
            },
            new Dictionary<string, IReadOnlyList<AgentStatus>>());

        Assert.Equal(2, snapshot.Items.Count);
        Assert.Equal(new RoleKey("w1", "shared"), snapshot.Items[0].Role);
        Assert.Equal(new RoleKey("w2", "shared"), snapshot.Items[1].Role);
    }

    [Fact]
    public void DuplicateAgentWithinOneWorkspaceIsDroppedAndReported()
    {
        var snapshot = RoleNavigation.Build(
            [Workspace("w1")],
            new Dictionary<string, IReadOnlyList<Agent>> { ["w1"] = [Agent("a"), Agent("a"), Agent("b")] },
            new Dictionary<string, IReadOnlyList<AgentStatus>>());

        Assert.Equal(["a", "b"], snapshot.Items.Select(item => item.Agent.AgentId));
        Assert.Equal(1, snapshot.DuplicateAgentsDropped);
    }

    [Fact]
    public void FrozenAndDisabledRolesStayListedButAreNotSelectable()
    {
        var snapshot = RoleNavigation.Build(
            [Workspace("w1")],
            new Dictionary<string, IReadOnlyList<Agent>>
            {
                ["w1"] = [Agent("ok"), Agent("frozen", frozen: true), Agent("disabled", enabled: false)],
            },
            new Dictionary<string, IReadOnlyList<AgentStatus>>());

        Assert.Equal(3, snapshot.Items.Count);
        Assert.True(snapshot.Items[0].CanSelect);
        Assert.False(snapshot.Items[1].CanSelect);
        Assert.False(snapshot.Items[2].CanSelect);
    }

    [Fact]
    public void LabelPrefersDisplayNameAndAvatarUrlIsCarriedThrough()
    {
        var snapshot = RoleNavigation.Build(
            [Workspace("w1")],
            new Dictionary<string, IReadOnlyList<Agent>>
            {
                ["w1"] = [new Agent("a", "内部名称", DisplayName: "显示名称", Description: "职责",
                    AvatarUrl: "file:///C:/app/wwwroot/assets/agent-avatars/agent-avatar-smile.png",
                    SourceTemplateId: "tpl", MainSessionId: "s")],
            },
            new Dictionary<string, IReadOnlyList<AgentStatus>>());

        Assert.Equal("显示名称", snapshot.Items[0].Label);
        Assert.EndsWith("agent-avatar-smile.png", snapshot.Items[0].Agent.AvatarUrl);
        Assert.True(snapshot.Items[0].CanSelect);
    }
}
