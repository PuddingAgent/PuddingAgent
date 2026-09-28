using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>
/// DS-16 tasks card: the list is a keyset-cursored management entry, every write carries the read version,
/// and the container-mother-card refusal is stated without offering the force escape hatch.
/// </summary>
public sealed class TaskContractTests
{
    private static WorkspaceTaskItem Task(string id = "t-1", string status = "Backlog", int version = 3) => new(
        id, "default", "Write the report", "desc", status, "Backlog", "p1", "inherit", "", "", "task.manual",
        version, 1, 40, "", "", "", "", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow,
        ["Ready", "Cancelled"]);

    private static TaskFilter Filter() => new("default", "", "", "", "", 50);

    [Fact]
    public void WorkspaceIsRequiredBecauseCoresQueryDemandsIt()
    {
        Assert.Empty(TaskText.Validate(Filter()));
        Assert.Contains("必须选择工作区", TaskText.Validate(Filter() with { WorkspaceId = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("每页条数", TaskText.Validate(Filter() with { Limit = 0 }).Single(), StringComparison.Ordinal);
        Assert.Contains("状态取值", TaskText.Validate(Filter() with { Status = "Doing" }).Single(), StringComparison.Ordinal);
        Assert.Contains("优先级", TaskText.Validate(Filter() with { Priority = "urgent" }).Single(), StringComparison.Ordinal);
        // 大小写不敏感：Core 的 wire 值是小写优先级与固定状态名。
        Assert.Empty(TaskText.Validate(Filter() with { Status = "InProgress", Priority = "p2" }));
    }

    [Fact]
    public void VocabulariesMirrorCoreWireValues()
    {
        Assert.Equal(12, TaskText.Statuses.Count);
        Assert.Contains("NeedsReview", TaskText.Statuses);
        Assert.Contains("Archived", TaskText.Statuses);
        Assert.Equal(["p0", "p1", "p2", "p3"], TaskText.Priorities);
        Assert.Equal(["inherit", "anytime", "off_peak_only"], TaskText.ExecutionWindows);
        Assert.Equal("p0（最高）", TaskText.DescribePriority("p0"));
        Assert.Equal("InProgress（进行中）", TaskText.DescribeStatus("InProgress"));
        Assert.Equal("状态未知", TaskText.DescribeStatus(null));
        Assert.Equal("SomethingNew", TaskText.DescribeStatus("SomethingNew"));
        Assert.Equal("仅非高峰", TaskText.DescribeExecutionWindow("off_peak_only").Split('（')[1].TrimEnd('）'));
    }

    [Fact]
    public void ListPagingIsKeysetAndTotalIsIndependentOfTheCursor()
    {
        // 空结果没有下一页。
        Assert.False(TaskListPage.Empty.CanGoForward);
        Assert.Contains("没有匹配", TaskListPage.Empty.PageText, StringComparison.Ordinal);

        var first = new TaskListPage([Task()], "t-1", 120, 50);
        Assert.True(first.CanGoForward);
        Assert.Contains("共 120 个任务", first.PageText, StringComparison.Ordinal);
        Assert.Contains("还有下一页", first.PageText, StringComparison.Ordinal);

        // 末页：Core 不再给游标。
        var lastPage = new TaskListPage([Task()], "", 120, 50);
        Assert.False(lastPage.CanGoForward);
        Assert.Contains("已到末页", lastPage.PageText, StringComparison.Ordinal);
        // TotalCount 与游标无关：翻页时不变。
        Assert.Equal(first.TotalCount, lastPage.TotalCount);

        Assert.Contains("keyset 游标", TaskText.CursorNotice, StringComparison.Ordinal);
        Assert.Contains("恒定不变", TaskText.CursorNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandsCarryTheReadVersionAndOnlyTwoNeedAnAgent()
    {
        var request = new TaskCommandRequest("default", "t-1", 3, "", "because");
        Assert.Empty(TaskText.Validate(TaskCommandKind.Cancel, request));
        Assert.Empty(TaskText.Validate(TaskCommandKind.Archive, request));
        // 指派与立即执行必须给出 Agent。
        Assert.Contains("必须指定 Agent", TaskText.Validate(TaskCommandKind.Assign, request).Single(), StringComparison.Ordinal);
        Assert.Contains("必须指定 Agent", TaskText.Validate(TaskCommandKind.RunNow, request).Single(), StringComparison.Ordinal);
        Assert.Empty(TaskText.Validate(TaskCommandKind.Assign, request with { AgentId = "agent-1" }));
        Assert.True(TaskText.RequiresAgent(TaskCommandKind.RunNow));
        Assert.False(TaskText.RequiresAgent(TaskCommandKind.Requeue));

        // 缺少任务或工作区、版本非法都要拦下。
        Assert.Contains("请先选择任务", TaskText.Validate(TaskCommandKind.Cancel, request with { TaskId = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("缺少工作区", TaskText.Validate(TaskCommandKind.Cancel, request with { WorkspaceId = " " }).Single(), StringComparison.Ordinal);
        Assert.Contains("版本无效", TaskText.Validate(TaskCommandKind.Cancel, request with { ExpectedVersion = -1 }).Single(), StringComparison.Ordinal);

        Assert.Equal("指派", TaskText.DescribeCommand(TaskCommandKind.Assign));
        Assert.Equal("立即执行", TaskText.DescribeCommand(TaskCommandKind.RunNow));
        Assert.Equal("重新排队", TaskText.DescribeCommand(TaskCommandKind.Requeue));
    }

    [Fact]
    public void CreateValidationAndItemTextUseCoreFieldsOnly()
    {
        var create = new TaskCreate("default", "Title", "desc", "p2");
        Assert.Empty(TaskText.Validate(create));
        Assert.Contains("标题", TaskText.Validate(create with { Title = "  " }).Single(), StringComparison.Ordinal);
        Assert.Contains("必须选择工作区", TaskText.Validate(create with { WorkspaceId = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("优先级", TaskText.Validate(create with { Priority = "urgent" }).Single(), StringComparison.Ordinal);
        // 优先级留空由 Core 用默认值，不算错误。
        Assert.Empty(TaskText.Validate(create with { Priority = "" }));

        var task = Task();
        Assert.Equal("Write the report", task.DisplayTitle);
        Assert.Contains("Backlog（待办池）", task.LineText, StringComparison.Ordinal);
        Assert.Contains("p1（高）", task.LineText, StringComparison.Ordinal);
        Assert.Contains("v3", task.LineText, StringComparison.Ordinal);
        Assert.Contains("版本 3", task.DetailText, StringComparison.Ordinal);
        Assert.Contains("进度 40%", task.DetailText, StringComparison.Ordinal);
        Assert.Contains("Ready → Cancelled", task.DetailText, StringComparison.Ordinal);
        Assert.Contains("当前指派 无", task.DetailText, StringComparison.Ordinal);

        // 无标题、无迁移、无偏好 Agent 时都要明确写出来。
        var bare = task with { Title = "", AllowedTransitions = [], PreferredAgentId = "", BlockerReason = "waiting" };
        Assert.Equal("（无标题任务）", bare.DisplayTitle);
        Assert.Contains("未指定", bare.DetailText, StringComparison.Ordinal);
        Assert.Contains("当前状态无可迁移目标", bare.DetailText, StringComparison.Ordinal);
        Assert.Contains("阻塞", bare.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public void NoticesStateCasHierarchyProtectionAndTheWorkPageScope()
    {
        Assert.Contains("ExpectedVersion", TaskText.VersionNotice, StringComparison.Ordinal);
        Assert.Contains("阻止覆盖", TaskText.VersionNotice, StringComparison.Ordinal);
        // 母卡保护：说明 fail-closed 且本页不提供 force。
        Assert.Contains("task.has_non_terminal_children", TaskText.HierarchyNotice, StringComparison.Ordinal);
        Assert.Contains("不提供", TaskText.HierarchyNotice, StringComparison.Ordinal);
        // 范围：看板/编辑/评论等属于独立工作页，这里不假装已有。
        Assert.Contains("独立原生工作页", TaskText.WorkPageNotice, StringComparison.Ordinal);
        Assert.Contains("尚未实现", TaskText.WorkPageNotice, StringComparison.Ordinal);
        Assert.Contains("不假装", TaskText.WorkPageNotice, StringComparison.Ordinal);
    }
}
