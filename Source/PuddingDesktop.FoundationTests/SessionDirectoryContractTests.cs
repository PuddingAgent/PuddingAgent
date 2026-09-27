using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>
/// DS-14 session directory: Core's repository only filters by channel/user/workspace, so the page's own
/// filters and paging are asserted here - along with the Frozen exclusion the HTTP list also applies.
/// </summary>
public sealed class SessionDirectoryContractTests
{
    private static SessionDirectoryEntry Session(string id, string status = "Active", string role = "Task",
        string owner = "alice", string title = "", string template = "global:general-assistant",
        int minutesAgo = 0) => new(
        id, "default", template, "web", owner, "ServiceSession", role, status, title,
        "", "", "", "", "user", owner, DateTimeOffset.UtcNow.AddHours(-1),
        DateTimeOffset.UtcNow.AddMinutes(-minutesAgo));

    /// <summary>LastActiveAt order: s-1 (1m), s-4 (2m, Frozen), s-3 (3m), s-2 (5m), s-5 (10m).</summary>
    private static IReadOnlyList<SessionDirectoryEntry> Fixture() =>
    [
        Session("s-1", title: "Alpha task", minutesAgo: 1),
        Session("s-2", status: "Idle", role: "Main", owner: "bob", title: "Beta main", minutesAgo: 5),
        Session("s-3", status: "Failed", title: "Gamma failure", minutesAgo: 3),
        Session("s-4", status: "Frozen", title: "Frozen record", minutesAgo: 2),
        Session("s-5", status: "Completed", role: "Audit", title: "Delta audit", minutesAgo: 10),
    ];

    private static string[] Ids(SessionDirectoryPage page) => page.Items.Select(session => session.SessionId).ToArray();

    [Fact]
    public void FrozenSessionsAreExcludedAndCountedLikeTheHttpList()
    {
        var page = SessionDirectoryText.Apply(Fixture(), SessionFilter.Default);
        Assert.Equal(4, page.Total);
        Assert.Equal(1, page.FrozenExcluded);
        Assert.Equal(["s-1", "s-3", "s-2", "s-5"], Ids(page));
        Assert.Contains("已排除 1 个 Frozen", page.PageText, StringComparison.Ordinal);
    }

    [Fact]
    public void PageSideFiltersNarrowTheReturnedSet()
    {
        var all = Fixture();
        Assert.Equal(4, SessionDirectoryText.Apply(all, SessionFilter.Default).Total);

        Assert.Equal(["s-3"], Ids(SessionDirectoryText.Apply(all, SessionFilter.Default with { Status = "Failed" })));
        // 状态与角色都大小写不敏感。
        Assert.Equal(1, SessionDirectoryText.Apply(all, SessionFilter.Default with { Status = "failed" }).Total);
        Assert.Equal(["s-1", "s-3"], Ids(SessionDirectoryText.Apply(all, SessionFilter.Default with { Role = "Task" })));
        Assert.Equal(["s-5"], Ids(SessionDirectoryText.Apply(all, SessionFilter.Default with { Role = "audit" })));

        // 关键字匹配标题、会话 ID 与 Owner。
        Assert.Equal(["s-1"], Ids(SessionDirectoryText.Apply(all, SessionFilter.Default with { Text = "alpha" })));
        Assert.Equal(["s-2"], Ids(SessionDirectoryText.Apply(all, SessionFilter.Default with { Text = "b" })));
        Assert.Equal(4, SessionDirectoryText.Apply(all, SessionFilter.Default with { Text = "s-" }).Total);

        // 模板筛选（本页做）与实际模板一致。
        Assert.Equal(4, SessionDirectoryText.Apply(all, SessionFilter.Default with { AgentTemplateId = "global:general-assistant" }).Total);
        Assert.Equal(0, SessionDirectoryText.Apply(all, SessionFilter.Default with { AgentTemplateId = "global:other" }).Total);
    }

    [Fact]
    public void ItemsAreOrderedByLastActiveAndPagedLocally()
    {
        var first = SessionDirectoryText.Apply(Fixture(), SessionFilter.Default with { PageSize = 2, Page = 1 });
        Assert.Equal(4, first.Total);
        Assert.Equal(2, first.PageCount);
        Assert.Equal(["s-1", "s-3"], Ids(first));
        Assert.True(first.CanGoForward);
        Assert.False(first.CanGoBack);

        var second = SessionDirectoryText.Apply(Fixture(), SessionFilter.Default with { PageSize = 2, Page = 2 });
        Assert.Equal(["s-2", "s-5"], Ids(second));
        Assert.True(second.CanGoBack);
        Assert.False(second.CanGoForward);
        Assert.Contains("第 2/2 页", second.PageText, StringComparison.Ordinal);

        // 超出范围的页码收敛到最后一页，而不是返回空页。
        var clamped = SessionDirectoryText.Apply(Fixture(), SessionFilter.Default with { PageSize = 2, Page = 99 });
        Assert.Equal(2, clamped.Page);
        Assert.Equal(2, clamped.Items.Count);

        // 结果为空时不装作有页。
        var empty = SessionDirectoryText.Apply([], SessionFilter.Default);
        Assert.Equal(0, empty.Total);
        Assert.Equal(0, empty.PageCount);
        Assert.False(empty.CanGoForward);
        Assert.Contains("没有匹配", empty.PageText, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationRejectsWhatThePageCannotHonour()
    {
        Assert.Empty(SessionDirectoryText.Validate(SessionFilter.Default));
        Assert.Contains("页码", SessionDirectoryText.Validate(SessionFilter.Default with { Page = 0 }).Single(), StringComparison.Ordinal);
        Assert.Contains("1–500", SessionDirectoryText.Validate(SessionFilter.Default with { PageSize = 0 }).Single(), StringComparison.Ordinal);
        // Frozen 是本页不提供的筛选值。
        Assert.Contains("Frozen", SessionDirectoryText.Validate(SessionFilter.Default with { Status = "Frozen" }).Single(), StringComparison.Ordinal);
        Assert.Contains("会话角色", SessionDirectoryText.Validate(SessionFilter.Default with { Role = "Owner" }).Single(), StringComparison.Ordinal);
        // 大小写不敏感。
        Assert.Empty(SessionDirectoryText.Validate(SessionFilter.Default with { Status = "active", Role = "main" }));
    }

    [Fact]
    public void EntryTextUsesCoreFieldsAndStatesItsNotices()
    {
        var session = Session("s-1", title: "", minutesAgo: 1);
        Assert.Equal("（未命名会话）", session.DisplayTitle);
        Assert.Equal("活跃", session.StatusText);
        Assert.Equal("Task（任务）", session.RoleText);
        Assert.Equal("ServiceSession（服务会话）", session.TypeText);
        Assert.Contains("Owner alice", session.PrincipalText, StringComparison.Ordinal);
        Assert.Contains("没有父/根/实例关联", session.LineageText, StringComparison.Ordinal);
        Assert.Contains("分钟前心跳", session.ActiveText, StringComparison.Ordinal);

        var linked = session with
        {
            ParentSessionId = "p-1", RootSessionId = "r-1", AgentInstanceId = "a-1", RuntimeNodeId = "n-1",
        };
        Assert.Contains("父 p-1", linked.LineageText, StringComparison.Ordinal);
        Assert.Contains("节点 n-1", linked.LineageText, StringComparison.Ordinal);
        Assert.Contains("Audit", SessionDirectoryText.DescribeRole("audit"), StringComparison.Ordinal);
        Assert.Equal("状态未知", SessionDirectoryText.DescribeStatus(null));
        Assert.Equal("SomethingNew", SessionDirectoryText.DescribeType("SomethingNew"));

        // 三条说明必须写在界面上：数据来源、筛选归属、Frozen 口径。
        Assert.Contains("ISessionRepository", SessionDirectoryText.SourceNotice, StringComparison.Ordinal);
        Assert.Contains("不是 Core 侧的过滤条件", SessionDirectoryText.ClientSideNotice, StringComparison.Ordinal);
        Assert.Contains("Frozen", SessionDirectoryText.FrozenNotice, StringComparison.Ordinal);
        Assert.Empty(SessionDirectoryPage.Empty.Items);
        Assert.Equal("未设置筛选（Core 返回的全部会话）", SessionFilter.Default.DescribeText);
    }
}
