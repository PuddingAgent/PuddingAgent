namespace PuddingDesktop.Runtime;

/// <summary>
/// 运行中心内存卡片的显示口径与文案（纯映射，可独立断言）。
///
/// 不能把 Process.WorkingSet64 直接当作「进程占用」展示：它是含共享页的总工作集，
/// 而任务管理器「内存」列显示的是专用工作集。二者在 .NET 进程上可相差一倍，
/// 面板一旦混用就会出现「面板 1007 MiB / 任务管理器 730 MB」这类互相矛盾的读数。
/// </summary>
public static class CoreMemoryDisplay
{
    public const string UnknownValue = "—";
    public const string IdleDetail = "任务管理器口径 · 仅 Core";
    public const string WorkingSetFallbackDetail = "工作集（含共享页）· 专用工作集不可读";

    public static (string Value, string Detail) Format(CoreProcessMetrics? metrics)
    {
        if (metrics is null) return (UnknownValue, IdleDetail);

        if (metrics.PrivateWorkingSetBytes is { } privateWorkingSet and > 0)
        {
            return (
                $"{privateWorkingSet / 1048576d:F0} MiB",
                $"专用工作集（任务管理器口径）· 工作集 {metrics.WorkingSetBytes / 1048576d:F0} MiB");
        }

        // 专用工作集读不到时如实降级，并把「这一行是工作集口径」写在副标题里。
        return ($"{metrics.WorkingSetBytes / 1048576d:F0} MiB", WorkingSetFallbackDetail);
    }
}
