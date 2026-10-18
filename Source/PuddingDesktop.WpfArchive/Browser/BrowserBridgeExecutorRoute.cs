using System.Text.Json;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Protocol;

namespace PuddingDesktop.Browser;

/// <summary>
/// 执行器调用缝（避免 WpfArchive 依赖 DesktopConnection）：Shell 传入工作区执行器的
/// <c>ExecuteAsync</c>；测试可传替身。
/// </summary>
public delegate Task<DesktopCapabilityResponse> BrowserCapabilityInvoker(
    DesktopCapabilityDescriptor capability,
    DesktopCapabilityRequest request,
    DesktopCallContext context,
    CancellationToken cancellationToken);

/// <summary>
/// I2-c 的**真实执行器路由**：把已通过会话准入、且已有结果反投影的命令交给工作区执行器执行。
///
/// 边界（ADR-097 D2）：
/// · 准入/复检由派发器完成；本类**不**再碰授权闸门，只携带 `SessionPermitMarker` 表明
///   "已由会话权威准入"，执行器因此跳过旧 authority 的 grant/lease 重复裁决；
/// · 结果必须来自执行器事实（不做"看起来成功"的补写）；能力错误映射为稳定 Bridge 错误码；
/// · 本路由只覆盖已有可靠反投影的命令；其余命令返回 null 让派发器回退 handler（迁移期）。
/// </summary>
public sealed class BrowserBridgeExecutorRoute
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    // 已有结果反投影、可以安全路由的命令（其余命令仍由 controller 执行）。
    private static readonly HashSet<string> RoutedCommands = new(StringComparer.Ordinal)
    {
        BrowserBridgeCommandNames.ContextList,
        BrowserBridgeCommandNames.ContextGetInfo,
        BrowserBridgeCommandNames.PageList,
        BrowserBridgeCommandNames.PageGetInfo,
    };

    private readonly BrowserCapabilityInvoker _invoker;
    private readonly string _sessionPermitMarker;

    public BrowserBridgeExecutorRoute(
        BrowserCapabilityInvoker invoker,
        string sessionPermitMarker = "bridge-session")
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _sessionPermitMarker = string.IsNullOrWhiteSpace(sessionPermitMarker)
            ? throw new ArgumentException("Session permit marker must be non-empty.", nameof(sessionPermitMarker))
            : sessionPermitMarker;
    }

    /// <summary>路由入口：返回 null 表示"不由执行器处理"（回退 handler）。</summary>
    public async Task<BrowserBridgeCommandResult?> RouteAsync(
        BrowserBridgeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!RoutedCommands.Contains(command.Name))
        {
            return null;
        }

        if (!BrowserBridgeCommandAdapter.TryMapReadCommand(
                command, out var descriptor, out var request, out var mapReason))
        {
            return Failure(command.OperationId, BrowserBridgeErrorCodes.BrowserInvalidCommand, mapReason!);
        }

        if (!TryMapCaller(command.Caller, out var caller, out var callerReason))
        {
            return Failure(command.OperationId, BrowserBridgeErrorCodes.BrowserUnauthorized, callerReason);
        }

        var context = new DesktopCallContext(
            new DesktopInstanceId("bridge-dispatcher"),
            new OperationId(command.OperationId.ToString("N")),
            command.DeadlineUtc,
            browserCaller: caller)
        {
            // 宿主内部标记：执行器见它即跳过旧 authority 裁决（不上线缆，见守卫测试）。
            SessionPermitMarker = _sessionPermitMarker,
        };

        DesktopCapabilityResponse response;
        try
        {
            response = await _invoker(descriptor!, request!, context, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 结果未知：如实报失败，绝不静默回退（否则可能双执行）。
            return Failure(command.OperationId, BrowserBridgeErrorCodes.BrowserOperationFailed, ex.Message);
        }

        if (response.IsFailure)
        {
            return Failure(
                command.OperationId,
                MapErrorCode(response.Error!),
                response.Error!.Message);
        }

        JsonElement value;
        try
        {
            value = command.Name switch
            {
                BrowserBridgeCommandNames.PageGetInfo => JsonSerializer.SerializeToElement(
                    BrowserBridgeResultAdapter.FromPageState(
                        response.PageState ?? throw new InvalidOperationException("executor returned no page state")),
                    WebJson),
                BrowserBridgeCommandNames.PageList => JsonSerializer.SerializeToElement(
                    BrowserBridgeResultAdapter.FromPageList(
                        response.Contexts ?? throw new InvalidOperationException("executor returned no contexts")),
                    WebJson),
                _ => JsonSerializer.SerializeToElement(
                    BrowserBridgeResultAdapter.FromContexts(
                        response.Contexts ?? throw new InvalidOperationException("executor returned no contexts")),
                    WebJson),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failure(command.OperationId, BrowserBridgeErrorCodes.BrowserOperationFailed, ex.Message);
        }

        return new BrowserBridgeCommandResult
        {
            OperationId = command.OperationId,
            Success = true,
            Value = value,
        };
    }

    private static bool TryMapCaller(
        BrowserBridgeCallerIdentity? wireCaller,
        out BrowserCallerIdentity? caller,
        out string reason)
    {
        caller = null;
        reason = string.Empty;

        if (wireCaller is null)
        {
            reason = "the bridge command carries no trusted caller identity";
            return false;
        }

        if (!TryMapKind(wireCaller.Kind, out var kind))
        {
            reason = $"caller kind '{wireCaller.Kind}' is not registered";
            return false;
        }

        try
        {
            caller = new BrowserCallerIdentity(kind, wireCaller.CallerId, wireCaller.TaskId, wireCaller.ParentTaskId);
            return true;
        }
        catch (ArgumentException ex)
        {
            reason = $"caller identity is not usable: {ex.Message}";
            return false;
        }
    }

    private static bool TryMapKind(string? kind, out BrowserCallerKind mapped)
    {
        switch (kind)
        {
            case "user":
                mapped = BrowserCallerKind.User;
                return true;
            case "agent":
                mapped = BrowserCallerKind.Agent;
                return true;
            case "sub_agent":
                mapped = BrowserCallerKind.SubAgent;
                return true;
            default:
                mapped = default;
                return false;
        }
    }

    private static string MapErrorCode(DesktopCapabilityError error) => error.Code switch
    {
        DesktopCapabilityErrorCode.Unauthorized => BrowserBridgeErrorCodes.BrowserUnauthorized,
        DesktopCapabilityErrorCode.Paused => BrowserBridgeErrorCodes.BrowserPaused,
        DesktopCapabilityErrorCode.UserTakeover => BrowserBridgeErrorCodes.BrowserUserTakeover,
        DesktopCapabilityErrorCode.InvalidRequest => BrowserBridgeErrorCodes.BrowserInvalidCommand,
        DesktopCapabilityErrorCode.InvalidTarget => BrowserBridgeErrorCodes.BrowserPageNotFound,
        DesktopCapabilityErrorCode.PageVersionMismatch => BrowserBridgeErrorCodes.StaleElementReference,
        DesktopCapabilityErrorCode.NotConnected => BrowserBridgeErrorCodes.BrowserBridgeDisconnected,
        DesktopCapabilityErrorCode.UiUnavailable => BrowserBridgeErrorCodes.BrowserNotAvailable,
        DesktopCapabilityErrorCode.UnsupportedCapability => BrowserBridgeErrorCodes.BrowserOperationNotSupported,
        _ => BrowserBridgeErrorCodes.BrowserOperationFailed,
    };

    private static BrowserBridgeCommandResult Failure(Guid operationId, string code, string message) => new()
    {
        OperationId = operationId,
        Success = false,
        ErrorCode = code,
        ErrorMessage = message,
    };
}
