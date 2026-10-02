using Serilog.Core;
using Serilog.Events;

namespace PuddingCode.Observability;

/// <summary>
/// 运行时可切换的日志级别（单一真源，供所有 Pudding 进程复用）。
///
/// <para>
/// 为什么需要它：此前级别只在启动时读环境变量 <c>PUDDING_LOG_LEVEL</c> 且**只支持 Debug/Information
/// 两档**，因此"临时打开调试日志排查问题"必须重启进程 —— 而 Pudding 的 Desktop/Core 是常驻进程，
/// 重启会打断用户会话。改为 <see cref="LoggingLevelSwitch"/> 后，Debug 按钮可以在**不重启**的情况下
/// 切换级别（读取端由 Serilog 的 <c>MinimumLevel.ControlledBy</c> 接入）。
/// </para>
/// <para>
/// 级别解析覆盖 Serilog 全部六档（Verbose/Debug/Information/Warning/Error/Fatal）；
/// 无法识别时**不猜测**：返回 false，调用方保留当前级别（启动时即 Information）。
/// </para>
/// </summary>
public static class PuddingLogLevelSwitch
{
    /// <summary>全进程共享的级别开关（Serilog 的 <c>MinimumLevel.ControlledBy</c> 期望单例）。</summary>
    public static LoggingLevelSwitch Instance { get; } = new(LogEventLevel.Information);

    /// <summary>环境变量名（保留既有行为：老部署靠它控制级别）。</summary>
    public const string EnvironmentVariableName = "PUDDING_LOG_LEVEL";

    /// <summary>
    /// 解析级别名。大小写不敏感；支持 Serilog 六档 + 常见别名（<c>info</c>/<c>warn</c>/<c>trace</c>）。
    /// </summary>
    /// <param name="value">级别名；null/空 ⇒ 返回 false（调用方保留当前级别）。</param>
    /// <param name="level">解析结果；失败时为 <see cref="LogEventLevel.Information"/>。</param>
    public static bool TryParse(string? value, out LogEventLevel level)
    {
        level = LogEventLevel.Information;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        level = value.Trim().ToLowerInvariant() switch
        {
            "verbose" or "trace" or "vrb" => LogEventLevel.Verbose,
            "debug" or "dbg" => LogEventLevel.Debug,
            "information" or "info" => LogEventLevel.Information,
            "warning" or "warn" => LogEventLevel.Warning,
            "error" or "err" => LogEventLevel.Error,
            "fatal" or "critical" or "crit" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information,
        };

        // 上面把未知值也映射成了 Information，因此需要重新判定"是否真的认识"。
        var known = value.Trim().ToLowerInvariant() switch
        {
            "verbose" or "trace" or "vrb" => true,
            "debug" or "dbg" => true,
            "information" or "info" => true,
            "warning" or "warn" => true,
            "error" or "err" => true,
            "fatal" or "critical" or "crit" => true,
            _ => false,
        };

        if (!known)
        {
            level = LogEventLevel.Information;
        }

        return known;
    }

    /// <summary>
    /// 按"配置优先、环境变量兜底"的顺序解析启动级别。
    /// 两者都没有或都无法识别 ⇒ 返回 <see cref="LogEventLevel.Information"/>（不放大日志量）。
    /// </summary>
    /// <param name="configured">配置里的级别名（例如 appsettings/system.json 的 <c>Logging:Level</c>）。</param>
    public static LogEventLevel ResolveStartupLevel(string? configured)
    {
        if (TryParse(configured, out var fromConfig))
        {
            return fromConfig;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        return TryParse(fromEnvironment, out var fromEnv) ? fromEnv : LogEventLevel.Information;
    }
}
