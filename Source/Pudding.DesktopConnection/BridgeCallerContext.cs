using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.DesktopConnection;

/// <summary>
/// **Bridge 传输的可信调用上下文适配**：把认证 WebSocket 上收到的调用者身份，按与
/// 能力通道**同一套判据**投影成 <see cref="DesktopCallContext"/>。
///
/// 为什么必须与 <c>CoreFrameMapping.Decode</c> 逐条一致：身份判据只有一份真源
/// （<see cref="BrowserCallerIdentity"/> 记录自身的校验 + <see cref="BrowserCallerKindWire"/> 的 kind 解析）。
/// 这里**不接收**工具参数、不推导身份、也不为缺失身份补默认值 —— 缺即拒。
///
/// 传输差异只体现在 `connectionGeneration` 上（由调用方按 Bridge 的认证连接代际提供），
/// 因此本方法**不**自行构造代际，避免把"连接换代"语义在第二个地方重写。
/// </summary>
internal static class BridgeCallerContext
{
    /// <summary>按能力通道同一判据建立上下文；失败时给出**具体原因**（调用方必须如实上报）。</summary>
    public static CapabilityResult<DesktopCallContext> Create(
        DesktopInstanceId desktopId,
        OperationId operationId,
        DateTimeOffset deadline,
        DesktopCorrelationId? correlationId,
        string? kind,
        string? callerId,
        string? taskId,
        string? parentTaskId)
    {
        BrowserCallerIdentity? browserCaller = null;

        if (!string.IsNullOrWhiteSpace(kind) || !string.IsNullOrWhiteSpace(callerId))
        {
            if (!BrowserCallerKindWire.TryParse(kind, out var callerKind))
            {
                return CapabilityResult<DesktopCallContext>.Failure(
                    DesktopCapabilityError.InvalidRequest(
                        $"caller.kind '{WireText.Truncate(kind, 32)}' is not a registered caller kind"));
            }

            try
            {
                browserCaller = new BrowserCallerIdentity(
                    callerKind,
                    callerId!,
                    string.IsNullOrEmpty(taskId) ? null : taskId,
                    string.IsNullOrEmpty(parentTaskId) ? null : parentTaskId);
            }
            catch (ArgumentException ex)
            {
                return CapabilityResult<DesktopCallContext>.Failure(
                    DesktopCapabilityError.InvalidRequest($"caller identity is invalid: {ex.Message}"));
            }
        }

        var context = new DesktopCallContext(desktopId, operationId, deadline, correlationId, browserCaller);

        // 浏览器自动化能力在 Desktop 侧**同样**要求可信身份（与能力通道解码处同一条强制）。
        if (!context.TryGetBrowserCaller(out _, out var callerReason))
        {
            return CapabilityResult<DesktopCallContext>.Failure(
                DesktopCapabilityError.InvalidRequest(callerReason ?? "no trusted caller identity"));
        }

        return CapabilityResult<DesktopCallContext>.Success(context);
    }
}