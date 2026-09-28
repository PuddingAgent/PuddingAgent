using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>
/// DS-15 admin-home card: the summary is pure composition (so it is asserted without a host), disk space is
/// read from the OS and reported as unknown rather than zero, and the card is explicitly not a workbench copy.
/// </summary>
public sealed class AdminHomeContractTests
{
    [Fact]
    public void DiskSpaceIsReadFromTheOsOrReportedUnknown()
    {
        // 临时目录所在卷一定存在。
        var info = DiskSpaceProbe.TryRead(Path.GetTempPath());
        Assert.NotNull(info);
        Assert.True(info!.TotalBytes > 0, "真实卷的总容量必须大于 0");
        Assert.True(info.FreeBytes >= 0);
        Assert.Equal(info.TotalBytes - info.FreeBytes, info.UsedBytes);
        Assert.Contains("可用", info.DescribeText, StringComparison.Ordinal);

        // 非法/不存在的路径返回 null —— 不伪造 0。
        Assert.Null(DiskSpaceProbe.TryRead(null));
        Assert.Null(DiskSpaceProbe.TryRead(""));
        Assert.Null(DiskSpaceProbe.TryRead("   "));

        // 磁盘字母不存在时返回 null；「存在但未就绪」的卷单独表达，而不是当成 0 字节。
        // 具体字母因机器而异（本机可能有未就绪的映射盘），所以动态挑一个不存在的字母。
        var existing = DriveInfo.GetDrives()
            .Select(drive => drive.Name[..1])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingLetter = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".First(letter => !existing.Contains(letter.ToString()));
        Assert.Null(DiskSpaceProbe.TryRead($@"{missingLetter}:\definitely-not-a-real-volume\sub"));

        var notReady = new DiskSpaceInfo(@"Z:\", "", 0, 0, false);
        Assert.False(notReady.IsReady);
        Assert.Contains("不可用", notReady.DescribeText, StringComparison.Ordinal);
        Assert.Equal(0, notReady.UsedPercent);
    }

    [Fact]
    public void SummaryComposesCountsNamesAndWarningsOnly()
    {
        var disk = new DiskSpaceInfo(@"D:\", "Data", 1_000_000_000_000, 250_000_000_000, true);
        var summary = AdminHomeSummary.Compose(
            coreReady: true, coreStateText: "运行中",
            workspaceNames: ["default", "sandbox"], teamNames: ["platform"],
            nodeCount: 3, onlineNodeCount: 2,
            dataFootprintBytes: 1_500_000_000, diskSpace: disk, warnings: ["快照过期", "磁盘紧张"]);

        Assert.True(summary.CoreReady);
        Assert.Equal("运行中", summary.CoreStateText);
        Assert.Equal(2, summary.WorkspaceCount);
        Assert.Contains("default、sandbox", summary.WorkspaceText, StringComparison.Ordinal);
        Assert.Equal(1, summary.TeamCount);
        Assert.Contains("platform", summary.TeamText, StringComparison.Ordinal);
        Assert.Contains("3 个节点（在线 2）", summary.NodeText, StringComparison.Ordinal);
        Assert.True(summary.DataFootprintKnown);
        Assert.Contains("Pudding 数据占用", summary.FootprintText, StringComparison.Ordinal);
        Assert.Contains("快照过期 · 磁盘紧张", summary.Warnings, StringComparison.Ordinal);
        Assert.True(summary.HasWarnings);
        Assert.Contains("可用", summary.DiskText, StringComparison.Ordinal);

        // 空状态：明确写出来，而不是显示 0 个。
        var empty = AdminHomeSummary.Compose(false, "已停止", [], [], 0, 0, null, null, []);
        Assert.False(empty.CoreReady);
        Assert.Contains("还没有工作区", empty.WorkspaceText, StringComparison.Ordinal);
        Assert.Contains("还没有协作团队", empty.TeamText, StringComparison.Ordinal);
        Assert.Contains("没有已注册的运行时节点", empty.NodeText, StringComparison.Ordinal);
        Assert.False(empty.DataFootprintKnown);
        Assert.Contains("未知", empty.FootprintText, StringComparison.Ordinal);
        Assert.Contains("未知", empty.DiskText, StringComparison.Ordinal);
        Assert.False(empty.HasWarnings);
        Assert.False(AdminHomeSummary.Empty.CoreReady);
    }

    [Fact]
    public void SizesAndKernelStatesAreHumanReadable()
    {
        Assert.Equal("512 B", AdminHomeText.Size(512));
        Assert.Equal("1 KB", AdminHomeText.Size(1024));
        Assert.Equal("1.5 MB", AdminHomeText.Size(1024 * 1024 + 512 * 1024));
        Assert.Equal("2 GB", AdminHomeText.Size(2L * 1024 * 1024 * 1024));
        Assert.Equal("1 TB", AdminHomeText.Size(1024L * 1024 * 1024 * 1024));
        Assert.Equal("未知", AdminHomeText.Size(-1));

        Assert.Equal("运行中", AdminHomeText.DescribeKernelState(DesktopKernelState.Ready));
        Assert.Equal("未配置数据目录", AdminHomeText.DescribeKernelState(DesktopKernelState.NotConfigured));
        Assert.Equal("启动失败", AdminHomeText.DescribeKernelState(DesktopKernelState.Failed));
        Assert.True(AdminHomeText.IsReady(DesktopKernelState.Ready));
        Assert.False(AdminHomeText.IsReady(DesktopKernelState.Starting));
    }

    [Fact]
    public void NoticesStateScopeDiskSourceAndShortcutBehaviour()
    {
        // 卡片要求：只做摘要与快捷入口，不复制工作台。
        Assert.Contains("不在设置中心复制完整工作台", AdminHomeText.ScopeNotice, StringComparison.Ordinal);
        // 可用空间来源与「未知不显示 0」必须写明。
        Assert.Contains("桌面进程", AdminHomeText.DiskSourceNotice, StringComparison.Ordinal);
        Assert.Contains("没有磁盘剩余字段", AdminHomeText.DiskSourceNotice, StringComparison.Ordinal);
        Assert.Contains("未知", AdminHomeText.DiskSourceNotice, StringComparison.Ordinal);
        Assert.Contains("不会触发扫描", AdminHomeText.FootprintNotice, StringComparison.Ordinal);
        Assert.Contains("只做导航", AdminHomeText.ShortcutNotice, StringComparison.Ordinal);
        Assert.Equal(["开始对话", "工作空间", "模型服务", "系统诊断"], AdminHomeText.Shortcuts);
    }
}
