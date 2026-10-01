namespace Pudding.Contracts.Desktop;

/// <summary>
/// 页面就绪度的线名真源（两端适配器共用同一张表，避免两份手写映射漂移）。
/// 未知线名折叠为 <see cref="DesktopPageReadiness.Unknown"/>（只读观测，fail soft）。
/// </summary>
public static class DesktopPageReadinessWire
{
    public static string NameOf(DesktopPageReadiness readiness) => readiness switch
    {
        DesktopPageReadiness.Loading => "loading",
        DesktopPageReadiness.Interactive => "interactive",
        DesktopPageReadiness.Complete => "complete",
        DesktopPageReadiness.Failed => "failed",
        _ => "unknown",
    };

    public static DesktopPageReadiness Parse(string? name) => name switch
    {
        "loading" => DesktopPageReadiness.Loading,
        "interactive" => DesktopPageReadiness.Interactive,
        "complete" => DesktopPageReadiness.Complete,
        "failed" => DesktopPageReadiness.Failed,
        _ => DesktopPageReadiness.Unknown,
    };
}
