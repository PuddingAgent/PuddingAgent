using System.Security.Cryptography;
using System.Text;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Proto = Pudding.Rpc.Protocol.V1;

namespace Pudding.CapabilityBroker;

/// <summary>
/// Core 侧命令编码（领域 → wire）。依赖方向：本适配器 → Contracts/Rpc.Protocol。
/// 与 Desktop 侧解码器互为逆映射，两端各有一份实现（两侧是独立程序集），一致性由
/// 「真实端点探针」的端到端往返与各自的往返测试共同保证。
/// </summary>
internal static class CoreCommandEncoder
{
    public static Proto.CapabilityCommand Encode(
        OperationId operationId,
        ConnectionGeneration generation,
        DesktopCapabilityDescriptor capability,
        DesktopCapabilityRequest request,
        DesktopCallContext call,
        string? traceId)
    {
        var command = new Proto.CapabilityCommand
        {
            OperationId = operationId.Value,
            Generation = (ulong)generation.Value,
            Capability = capability.Name,
            TraceId = traceId ?? string.Empty,
            CorrelationId = call.CorrelationId?.Value ?? string.Empty,
            Deadline = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(call.DeadlineUtc),
        };

        switch (capability.Capability)
        {
            case DesktopCapability.WebViewNavigate when request.Navigate is { } navigate:
                command.Navigate = new Proto.NavigateCommand
                {
                    Target = EncodeTarget(navigate.Target),
                    Url = navigate.Url.AbsoluteUri,
                    ExpectedPageVersion = navigate.ExpectedPageVersion.Value,
                };
                break;

            case DesktopCapability.WebViewExecuteJavascript when request.Javascript is { } javascript:
                command.ExecuteJavascript = new Proto.ExecuteJavascriptCommand
                {
                    Target = EncodeTarget(javascript.Target),
                    Script = javascript.Script,
                    ExpectedPageVersion = javascript.ExpectedPageVersion.Value,
                    MaxResultBytes = (uint)javascript.MaxResultBytes,
                };
                break;

            case DesktopCapability.ShellNotification when request.Notification is { } notification:
                command.ShowNotification = new Proto.ShowNotificationCommand
                {
                    Title = notification.Title,
                    Message = notification.Message,
                    Priority = notification.Priority switch
                    {
                        DesktopNotificationPriority.Low => Proto.NotificationPriority.Low,
                        DesktopNotificationPriority.High => Proto.NotificationPriority.High,
                        _ => Proto.NotificationPriority.Normal,
                    },
                };
                break;

            case DesktopCapability.WebViewPageState when request.PageState is { } target:
                command.GetPageState = new Proto.GetPageStateCommand { Target = EncodeTarget(target) };
                break;

            default:
                throw new ArgumentException(
                    $"Request does not carry a payload for capability '{capability.Name}'.", nameof(request));
        }

        return command;
    }

    /// <summary>
    /// 指纹：只覆盖能力与业务 payload（不含 deadline/trace），用于 Core 侧「重复 OperationId」判定。
    /// 相同的 typed 请求必须得到相同指纹；payload 任一项变化必须得到不同指纹。
    /// </summary>
    public static string Fingerprint(DesktopCapabilityDescriptor capability, DesktopCapabilityRequest request)
    {
        var canonical = new StringBuilder(capability.Name);
        canonical.Append('|').Append(request switch
        {
            _ when request.Navigate is { } navigate =>
                $"navigate:{navigate.Target.Key}:{navigate.Url.AbsoluteUri}:{navigate.ExpectedPageVersion.Value}",
            _ when request.Javascript is { } javascript =>
                $"javascript:{javascript.Target.Key}:{javascript.Script}:{javascript.ExpectedPageVersion.Value}:{javascript.MaxResultBytes}",
            _ when request.Notification is { } notification =>
                $"notification:{notification.Title}:{notification.Message}:{notification.Priority}",
            _ when request.PageState is { } target => $"page_state:{target.Key}",
            _ => "empty",
        });

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static Proto.CommandTarget EncodeTarget(DesktopPageTarget target) =>
        new() { ContextId = target.ContextId, PageId = target.PageId };
}

/// <summary>Core 侧结果解码（wire → 领域），fail closed：payload 与命令能力不一致即 internal_error。</summary>
internal static class DesktopResultDecoder
{
    /// <summary>
    /// 解码结果。<paramref name="requestedTarget"/> 由调用方（Core 的待完成表）提供：
    /// 结果帧不重复携带目标，领域 DTO 的目标由请求关联而来。
    /// </summary>
    public static CapabilityResult<DesktopCapabilityResponse> Decode(
        Proto.OperationResult result, DesktopCapability expectedCapability, DesktopPageTarget? requestedTarget)
    {
        if (result.OutcomeCase == Proto.OperationResult.OutcomeOneofCase.None)
        {
            return CapabilityResult<DesktopCapabilityResponse>.Failure(
                DesktopCapabilityError.Internal("desktop returned a result with no outcome"));
        }

        if (result.OutcomeCase == Proto.OperationResult.OutcomeOneofCase.Error)
        {
            return CapabilityResult<DesktopCapabilityResponse>.Failure(DecodeError(result.Error));
        }

        var matches = result.OutcomeCase switch
        {
            Proto.OperationResult.OutcomeOneofCase.Navigate => expectedCapability == DesktopCapability.WebViewNavigate,
            Proto.OperationResult.OutcomeOneofCase.ExecuteJavascript => expectedCapability == DesktopCapability.WebViewExecuteJavascript,
            Proto.OperationResult.OutcomeOneofCase.ShowNotification => expectedCapability == DesktopCapability.ShellNotification,
            Proto.OperationResult.OutcomeOneofCase.PageState => expectedCapability == DesktopCapability.WebViewPageState,
            _ => false,
        };

        if (!matches)
        {
            return CapabilityResult<DesktopCapabilityResponse>.Failure(
                DesktopCapabilityError.Internal("desktop result payload does not match the commanded capability"));
        }

        switch (result.OutcomeCase)
        {
            case Proto.OperationResult.OutcomeOneofCase.Navigate:
            {
                var disposition = result.Navigate.Disposition switch
                {
                    Proto.NavigateDisposition.Accepted => NavigateDisposition.Accepted,
                    Proto.NavigateDisposition.Completed => NavigateDisposition.Completed,
                    _ => (NavigateDisposition?)null,
                };

                if (disposition is null)
                {
                    return CapabilityResult<DesktopCapabilityResponse>.Failure(
                        DesktopCapabilityError.Internal("navigate outcome has no disposition"));
                }

                Uri? currentUrl = null;
                if (!string.IsNullOrEmpty(result.Navigate.CurrentUrl)
                    && !Uri.TryCreate(result.Navigate.CurrentUrl, UriKind.Absolute, out currentUrl))
                {
                    return CapabilityResult<DesktopCapabilityResponse>.Failure(
                        DesktopCapabilityError.Internal("navigate outcome carries a non-absolute url"));
                }

                return CapabilityResult<DesktopCapabilityResponse>.Success(
                    DesktopCapabilityResponse.FromNavigate(new NavigateResult(
                        disposition.Value, currentUrl, ToPageVersion(result.Navigate.PageVersion))));
            }

            case Proto.OperationResult.OutcomeOneofCase.ExecuteJavascript:
            {
                var kind = result.ExecuteJavascript.Kind switch
                {
                    Proto.JavascriptValueKind.Undefined => JavascriptValueKind.Undefined,
                    Proto.JavascriptValueKind.Null => JavascriptValueKind.Null,
                    Proto.JavascriptValueKind.Boolean => JavascriptValueKind.Boolean,
                    Proto.JavascriptValueKind.Number => JavascriptValueKind.Number,
                    Proto.JavascriptValueKind.String => JavascriptValueKind.String,
                    Proto.JavascriptValueKind.Json => JavascriptValueKind.Json,
                    _ => (JavascriptValueKind?)null,
                };

                if (kind is null)
                {
                    return CapabilityResult<DesktopCapabilityResponse>.Failure(
                        DesktopCapabilityError.Internal("javascript outcome has no value kind"));
                }

                return CapabilityResult<DesktopCapabilityResponse>.Success(
                    DesktopCapabilityResponse.FromJavascript(new JavascriptResult(
                        kind.Value,
                        string.IsNullOrEmpty(result.ExecuteJavascript.JsonValue) ? null : result.ExecuteJavascript.JsonValue,
                        result.ExecuteJavascript.Truncated)));
            }

            case Proto.OperationResult.OutcomeOneofCase.ShowNotification:
                return CapabilityResult<DesktopCapabilityResponse>.Success(
                    DesktopCapabilityResponse.FromNotification(new DesktopNotificationResult(
                        result.ShowNotification.Shown,
                        string.IsNullOrEmpty(result.ShowNotification.NotificationId)
                            ? null
                            : result.ShowNotification.NotificationId)));

            default:
            {
                if (requestedTarget is null)
                {
                    return CapabilityResult<DesktopCapabilityResponse>.Failure(
                        DesktopCapabilityError.Internal("page_state result cannot be correlated without the requested target"));
                }

                Uri? url = null;
                if (!string.IsNullOrEmpty(result.PageState.Url)
                    && !Uri.TryCreate(result.PageState.Url, UriKind.Absolute, out url))
                {
                    return CapabilityResult<DesktopCapabilityResponse>.Failure(
                        DesktopCapabilityError.Internal("page_state outcome carries a non-absolute url"));
                }

                return CapabilityResult<DesktopCapabilityResponse>.Success(
                    DesktopCapabilityResponse.FromPageState(new DesktopPageState(
                        requestedTarget,
                        url,
                        ToPageVersion(result.PageState.PageVersion),
                        PageReadinessWire.Parse(result.PageState.Readiness))));
            }
        }
    }

    private static DesktopCapabilityError DecodeError(Proto.ErrorOutcome? outcome)
    {
        if (outcome is null)
        {
            return DesktopCapabilityError.Internal("desktop returned an empty error outcome");
        }

        var code = DesktopCapabilityErrorCodes.ParseOrInternalError(outcome.Code);
        return new DesktopCapabilityError(code, outcome.Message, outcome.Retryable, outcome.MayHaveSideEffects);
    }

    private static DesktopPageVersion ToPageVersion(long value) =>
        value > 0 ? DesktopPageVersion.Require(value) : DesktopPageVersion.Unknown;
}

/// <summary>
/// 页面就绪度线名（与 Desktop 侧同一套名字；未知线名折叠为 unknown）。
/// 快照由本组件的映射测试与真实端点探针共同保证。
/// </summary>
internal static class PageReadinessWire
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
