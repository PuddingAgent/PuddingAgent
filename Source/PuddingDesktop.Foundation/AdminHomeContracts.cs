namespace PuddingDesktop.Foundation;

/// <summary>
/// Free space on the volume that holds a path. Read by the desktop process from the OS, because Core's
/// storage snapshot reports Pudding's own data footprint and has no disk-free field at all.
/// </summary>
public sealed record DiskSpaceInfo(string Root, string VolumeLabel, long TotalBytes, long FreeBytes, bool IsReady)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedPercent => TotalBytes <= 0 ? 0 : (double)UsedBytes / TotalBytes;
    public string DescribeText => IsReady
        ? $"{Root} · 可用 {AdminHomeText.Size(FreeBytes)} / 共 {AdminHomeText.Size(TotalBytes)}（已用 {UsedPercent:P0}）"
        : $"{Root} · 磁盘不可用";
}

/// <summary>Reads the volume for a data root. Returns null rather than inventing zeros.</summary>
public static class DiskSpaceProbe
{
    public static DiskSpaceInfo? TryRead(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return null;
            // A volume that does not exist at all is unknown (null); one that exists but is not ready is
            // reported as not ready. DriveInfo alone cannot tell the two apart, so the drive list is consulted.
            var exists = DriveInfo.GetDrives()
                .Any(drive => string.Equals(drive.Name, root, StringComparison.OrdinalIgnoreCase));
            if (!exists) return null;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return new DiskSpaceInfo(root, "", 0, 0, false);
            return new DiskSpaceInfo(root, drive.VolumeLabel ?? "", drive.TotalSize, drive.AvailableFreeSpace, true);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException or System.Security.SecurityException)
        {
            // 盘不存在、无权限或路径非法：返回 null，由界面显示「未知」。
            return null;
        }
    }
}

/// <summary>
/// The admin home summary: compact counts and shortcuts, deliberately not a copy of the full workbench
/// (which is what the card asks for).
/// </summary>
public sealed record AdminHomeSummary(
    string CoreStateText, bool CoreReady,
    int WorkspaceCount, string WorkspaceNames,
    int TeamCount, string TeamNames,
    int NodeCount, int OnlineNodeCount,
    long DataFootprintBytes, bool DataFootprintKnown,
    DiskSpaceInfo? DiskSpace, string Warnings)
{
    public static AdminHomeSummary Empty { get; } = new(
        "未知", false, 0, "", 0, "", 0, 0, 0, false, null, "");

    public string WorkspaceText => WorkspaceCount == 0
        ? "还没有工作区"
        : $"{WorkspaceCount} 个工作区：{WorkspaceNames}";
    public string TeamText => TeamCount == 0
        ? "还没有协作团队"
        : $"{TeamCount} 个团队：{TeamNames}";
    public string NodeText => NodeCount == 0
        ? "没有已注册的运行时节点"
        : $"{NodeCount} 个节点（在线 {OnlineNodeCount}）";
    public string FootprintText => DataFootprintKnown
        ? $"Pudding 数据占用 {AdminHomeText.Size(DataFootprintBytes)}（Core 快照，读取不触发扫描）"
        : "Pudding 数据占用未知（Core 快照不可用）";
    public string DiskText => DiskSpace is { } disk ? disk.DescribeText : "磁盘可用空间未知（由桌面进程读取，Core 不提供该字段）";
    public bool HasWarnings => Warnings.Length > 0;

    /// <summary>Pure composition so the summary can be asserted without a host.</summary>
    public static AdminHomeSummary Compose(
        bool coreReady, string coreStateText,
        IReadOnlyList<string> workspaceNames, IReadOnlyList<string> teamNames,
        int nodeCount, int onlineNodeCount,
        long? dataFootprintBytes, DiskSpaceInfo? diskSpace, IReadOnlyList<string> warnings) => new(
        coreStateText, coreReady,
        workspaceNames.Count, string.Join("、", workspaceNames),
        teamNames.Count, string.Join("、", teamNames),
        nodeCount, onlineNodeCount,
        dataFootprintBytes ?? 0, dataFootprintBytes.HasValue,
        diskSpace, string.Join(" · ", warnings));
}

public static class AdminHomeText
{
    public const string ScopeNotice =
        "这是**摘要 + 快捷入口**：不在设置中心复制完整工作台（工作区的完整管理在「工作区与渠道」，模型在「模型与服务商」，诊断在「会话与诊断」）。";

    public const string DiskSourceNotice =
        "可用空间由**桌面进程**读取数据目录所在卷（Core 的存储快照只有 Pudding 自身占用，没有磁盘剩余字段）；" +
        "读不到时显示「未知」，不显示 0。";

    public const string FootprintNotice =
        "Pudding 数据占用来自 Core 的缓存快照：读取不会触发扫描，刷新需要显式请求。";

    public const string ShortcutNotice = "快捷入口只做导航（切页/切分类），不在首页直接执行写操作。";

    public static IReadOnlyList<string> Shortcuts { get; } =
        ["开始对话", "工作空间", "模型服务", "系统诊断"];

    public static string Size(long bytes)
    {
        if (bytes < 0) return "未知";
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        foreach (var unit in units)
        {
            value /= 1024;
            if (value < 1024 || unit == units[^1]) return $"{value:0.##} {unit}";
        }
        return $"{value:0.##} TB";
    }

    public static string DescribeKernelState(DesktopKernelState state) => state switch
    {
        DesktopKernelState.NotConfigured => "未配置数据目录",
        DesktopKernelState.Stopped => "已停止",
        DesktopKernelState.Starting => "启动中",
        DesktopKernelState.Ready => "运行中",
        DesktopKernelState.Stopping => "停止中",
        _ => "启动失败"
    };

    public static bool IsReady(DesktopKernelState state) => state == DesktopKernelState.Ready;
}
