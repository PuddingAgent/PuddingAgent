using Microsoft.Extensions.Logging;

namespace PuddingCode.Observability;

/// <summary>
/// 可观测性安全记录器 — 消除各处重复的 try-catch 包裹代码。
/// Timeline / Telemetry 是副作用，不应阻断主流程。
/// </summary>
public static class SafeRecorder
{
    /// <summary>安全执行 action，异常记 Warning 但不抛出。</summary>
    public static async Task RunAsync(
        Func<CancellationToken, Task> action,
        ILogger logger,
        string operationDescription,
        CancellationToken ct = default)
    {
        try
        {
            await action(ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("[Obs] {Operation} cancelled", operationDescription);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Obs] {Operation} failed", operationDescription);
        }
    }

    /// <summary>同步安全执行（适用于 Telemetry Fire-and-Forget）。</summary>
    public static void FireAndForget(
        Action action,
        ILogger logger,
        string operationDescription)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Obs] {Operation} failed (fire-and-forget)", operationDescription);
        }
    }
}
