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
        BrowserBridgeCommandNames.ContextCreate,
        BrowserBridgeCommandNames.ContextClose,
        BrowserBridgeCommandNames.PageList,
        BrowserBridgeCommandNames.PageGetInfo,
        BrowserBridgeCommandNames.PageLocate,
        BrowserBridgeCommandNames.PageWaitFor,
        BrowserBridgeCommandNames.PageInteract,
        BrowserBridgeCommandNames.PageSnapshot,
        BrowserBridgeCommandNames.PageGoto,
        BrowserBridgeCommandNames.PageGoBack,
        BrowserBridgeCommandNames.PageGoForward,
        BrowserBridgeCommandNames.PageReload,
        BrowserBridgeCommandNames.PageStop,
        BrowserBridgeCommandNames.PageScreenshot,
        BrowserBridgeCommandNames.PageCreate,
        BrowserBridgeCommandNames.PageActivate,
        BrowserBridgeCommandNames.PageClose,
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

        JsonElement? value;
        try
        {
            // D8：每条命令显式列出响应合同；没有 `_ =>` 兜底（内部能力全集 ≠ 线缆返回形状）。
            switch (command.Name)
            {
                case BrowserBridgeCommandNames.ContextList:
                    value = JsonSerializer.SerializeToElement(
                        BrowserBridgeResultAdapter.FromContexts(
                            response.Contexts ?? throw new InvalidOperationException("executor returned no contexts")),
                        WebJson);
                    break;

                case BrowserBridgeCommandNames.ContextGetInfo:
                {
                    var requestedContextId = RequestedContextIdOrThrow(command);
                    var contextDescriptor = BrowserBridgeResultAdapter.FromContext(
                        response.Contexts ?? throw new InvalidOperationException("executor returned no contexts"),
                        requestedContextId);
                    if (contextDescriptor is null)
                    {
                        return Failure(
                            command.OperationId,
                            BrowserBridgeErrorCodes.BrowserContextNotFound,
                            $"context '{requestedContextId}' was not found");
                    }

                    value = JsonSerializer.SerializeToElement(contextDescriptor, WebJson);
                    break;
                }

                case BrowserBridgeCommandNames.ContextCreate:
                    value = JsonSerializer.SerializeToElement(
                        BrowserBridgeResultAdapter.FromCreatedContext(
                            response.ContextCreated ?? throw new InvalidOperationException("executor returned no created context")),
                        WebJson);
                    break;

                // 空响应合同：只确认命令成功，不反序列化 JsonElement。
                case BrowserBridgeCommandNames.ContextClose:
                case BrowserBridgeCommandNames.PageClose:
                    value = null;
                    break;

                case BrowserBridgeCommandNames.PageList:
                {
                    var requestedContextId = RequestedContextIdOrThrow(command);
                    value = JsonSerializer.SerializeToElement(
                        BrowserBridgeResultAdapter.FromPageList(
                            response.Contexts ?? throw new InvalidOperationException("executor returned no contexts"),
                            requestedContextId),
                        WebJson);
                    break;
                }

                case BrowserBridgeCommandNames.PageGetInfo:
                    value = JsonSerializer.SerializeToElement(
                        BrowserBridgeResultAdapter.FromPageState(
                            response.PageState ?? throw new InvalidOperationException("executor returned no page state")),
                        WebJson);
                    break;

                case BrowserBridgeCommandNames.PageLocate:
                    value = JsonSerializer.SerializeToElement(
                        BrowserBridgeResultAdapter.FromLocate(
                            response.Locate ?? throw new InvalidOperationException("executor returned no locate result")),
                        WebJson);
                    break;

                case BrowserBridgeCommandNames.PageWaitFor:
                    value = JsonSerializer.SerializeToElement(
                        BrowserBridgeResultAdapter.FromWait(
                            response.Wait ?? throw new InvalidOperationException("executor returned no wait result")),
                        WebJson);
                    break;

                case BrowserBridgeCommandNames.PageInteract:
                    value = JsonSerializer.SerializeToElement(
                        BrowserBridgeResultAdapter.FromInteraction(
                            response.Interact ?? throw new InvalidOperationException("executor returned no interaction result")),
                        WebJson);
                    break;

                case BrowserBridgeCommandNames.PageSnapshot:
                    value = JsonSerializer.SerializeToElement(
                        BrowserBridgeResultAdapter.FromSnapshot(
                            response.Snapshot ?? throw new InvalidOperationException("executor returned no snapshot")),
                        WebJson);
                    break;

                // page.goto 的消费者合同是完整导航结果；四个历史动作只消费单页描述符。
                case BrowserBridgeCommandNames.PageGoto:
                    value = JsonSerializer.SerializeToElement(
                        BrowserBridgeResultAdapter.FromNavigate(
                            response.Navigate ?? throw new InvalidOperationException("executor returned no navigation result"),
                            PageTargetOf(command)),
                        WebJson);
                    break;

                case BrowserBridgeCommandNames.PageGoBack:
                case BrowserBridgeCommandNames.PageGoForward:
                case BrowserBridgeCommandNames.PageReload:
                case BrowserBridgeCommandNames.PageStop:
                    value = JsonSerializer.SerializeToElement(
                        BrowserBridgeResultAdapter.FromNavigationPage(
                            response.Navigate ?? throw new InvalidOperationException("executor returned no navigation result"),
                            PageTargetOf(command)),
                        WebJson);
                    break;

                case BrowserBridgeCommandNames.PageScreenshot:
                    value = JsonSerializer.SerializeToElement(
                        BrowserBridgeResultAdapter.FromScreenshot(
                            response.Screenshot ?? throw new InvalidOperationException("executor returned no screenshot result")),
                        WebJson);
                    break;

                case BrowserBridgeCommandNames.PageCreate:
                case BrowserBridgeCommandNames.PageActivate:
                    value = JsonSerializer.SerializeToElement(
                        BrowserBridgeResultAdapter.FromPageState(
                            (response.Tabs ?? throw new InvalidOperationException("executor returned no tabs result")).Page),
                        WebJson);
                    break;

                default:
                    // 路由集合与矩阵必须一一对应；漏列说明内部接线错误，不能回退给旧 handler。
                    return Failure(
                        command.OperationId,
                        BrowserBridgeErrorCodes.BrowserOperationNotSupported,
                        $"command '{command.Name}' is routed but has no response contract");
            }
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

    private static string RequestedContextIdOrThrow(BrowserBridgeCommand command)
    {
        if (!BrowserBridgeCommandAdapter.TryGetRequestedContextId(command, out var contextId, out var reason)
            || contextId is null)
        {
            throw new InvalidOperationException(reason ?? "command has no context id");
        }

        return contextId;
    }

    private static DesktopPageTarget PageTargetOf(BrowserBridgeCommand command) =>
        new(
            command.ContextId ?? throw new InvalidOperationException("command has no context id"),
            command.PageId ?? throw new InvalidOperationException("command has no page id"));

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
