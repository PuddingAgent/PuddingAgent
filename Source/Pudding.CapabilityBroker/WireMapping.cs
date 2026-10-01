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

            case DesktopCapability.ShellStatus when request.ShellStatus:
                command.GetShellStatus = new Proto.GetShellStatusCommand();
                break;

            case DesktopCapability.BrowserLocate when request.Locate is { } locate:
                command.Locate = new Proto.LocateCommand
                {
                    Target = EncodeTarget(locate.Target),
                    ExpectedPageVersion = locate.ExpectedPageVersion.Value,
                    MaxResults = locate.MaxResults,
                    Locator = new Proto.LocatorSpec
                    {
                        Kind = DesktopLocatorKindWire.NameOf(locate.Locator.Kind),
                        Value = locate.Locator.Value,
                        Name = locate.Locator.Name ?? string.Empty,
                        Exact = locate.Locator.Exact,
                        Nth = locate.Locator.Nth ?? -1,
                        HasText = locate.Locator.HasText ?? string.Empty,
                    },
                };
                break;

            case DesktopCapability.BrowserInteract when request.Interact is { } interact:
                command.Interact = new Proto.InteractCommand
                {
                    Target = EncodeTarget(interact.Target),
                    ExpectedPageVersion = interact.ExpectedPageVersion.Value,
                    Action = DesktopInteractionActionWire.NameOf(interact.Action),
                };

                if (interact.Locator is { } interactLocator)
                {
                    command.Interact.Locator = new Proto.LocatorSpec
                    {
                        Kind = DesktopLocatorKindWire.NameOf(interactLocator.Kind),
                        Value = interactLocator.Value,
                        Name = interactLocator.Name ?? string.Empty,
                        Exact = interactLocator.Exact,
                        Nth = interactLocator.Nth ?? -1,
                        HasText = interactLocator.HasText ?? string.Empty,
                    };
                }

                if (interact.Text is { } text)
                {
                    command.Interact.Text = text;
                }

                if (interact.Values is { Count: > 0 } values)
                {
                    command.Interact.Values.AddRange(values);
                }

                if (interact.IsChecked is { } isChecked)
                {
                    command.Interact.Checked = isChecked;
                }

                command.Interact.DeltaX = interact.DeltaX ?? 0;
                command.Interact.DeltaY = interact.DeltaY ?? 0;
                break;

            case DesktopCapability.BrowserWaitFor when request.WaitFor is { } waitFor:
                command.WaitFor = new Proto.WaitForCommand
                {
                    Target = EncodeTarget(waitFor.Target),
                    ExpectedPageVersion = waitFor.ExpectedPageVersion.Value,
                    ConditionKind = DesktopWaitConditionKindWire.NameOf(waitFor.Condition.Kind),
                    ConditionValue = waitFor.Condition.Value,
                    TimeoutMs = waitFor.TimeoutMs,
                };
                break;

            case DesktopCapability.BrowserContexts when request.Contexts:
                command.Contexts = new Proto.ContextsCommand();
                break;

            case DesktopCapability.BrowserTabs when request.Tabs is { } requestTabs:
                command.Tabs = new Proto.TabsCommand
                {
                    Target = EncodeTarget(requestTabs.Target),
                    ExpectedPageVersion = requestTabs.ExpectedPageVersion.Value,
                    Action = DesktopTabActionWire.NameOf(requestTabs.Action),
                };
                break;

            case DesktopCapability.ShellClipboard when request.Clipboard is { } clipboard:
                command.ReadClipboard = new Proto.ClipboardReadCommand { MaxCharacters = clipboard.MaxCharacters };
                break;

            case DesktopCapability.ShellDialog when request.Dialog is { } dialog:
                command.ShowDialog = new Proto.ShowDialogCommand
                {
                    Title = dialog.Title,
                    Message = dialog.Message,
                    Buttons = dialog.Buttons.ToString(),
                };
                break;

            case DesktopCapability.ShellFilePicker when request.FilePicker is { } picker:
                command.ShowFilePicker = new Proto.ShowFilePickerCommand
                {
                    Title = picker.Title,
                    AllowMultiple = picker.AllowMultiple,
                };
                command.ShowFilePicker.Extensions.AddRange(picker.Extensions);
                break;

            case DesktopCapability.BrowserSnapshot when request.Snapshot is { } snapshot:
                command.Snapshot = new Proto.SnapshotCommand
                {
                    Target = EncodeTarget(snapshot.Target),
                    ExpectedPageVersion = snapshot.ExpectedPageVersion.Value,
                    Budget = new Proto.SnapshotBudget
                    {
                        IncludeDom = snapshot.Options.IncludeDom,
                        IncludeAccessibilityTree = snapshot.Options.IncludeAccessibilityTree,
                        IncludeHtml = snapshot.Options.IncludeHtml,
                        MaxNodes = snapshot.Options.MaxNodes,
                        MaxTextLength = snapshot.Options.MaxTextLength,
                    },
                };
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
            _ when request.Tabs is { } requestTabs =>
                $"tabs:{requestTabs.Action}:{requestTabs.Target.Key}:{requestTabs.ExpectedPageVersion.Value}",
            _ when request.Clipboard is { } clipboard => $"clipboard:{clipboard.MaxCharacters}",
            _ when request.Dialog is { } dialog =>
                $"dialog:{dialog.Buttons}:{dialog.Title}:{dialog.Message}",
            _ when request.FilePicker is { } picker =>
                $"file_picker:{picker.AllowMultiple}:{picker.Title}:{string.Join(",", picker.Extensions)}",
            _ when request.Contexts => "contexts",
            _ when request.WaitFor is { } waitFor =>
                $"wait_for:{waitFor.Target.Key}:{waitFor.Condition.Kind}:{waitFor.Condition.Value}:{waitFor.TimeoutMs}:{waitFor.ExpectedPageVersion.Value}",
            _ when request.Interact is { } interact =>
                $"interact:{interact.Action}:{interact.Target.Key}:{interact.ExpectedPageVersion.Value}:{interact.Locator}:"
                + $"{interact.Text}:{string.Join(",", interact.Values ?? [])}:{interact.IsChecked}:{interact.DeltaX}:{interact.DeltaY}",
            _ when request.Locate is { } locate =>
                $"locate:{locate.Target.Key}:{locate.Locator}:{locate.ExpectedPageVersion.Value}:{locate.MaxResults}",
            _ when request.Snapshot is { } snapshot =>
                $"snapshot:{snapshot.Target.Key}:{snapshot.ExpectedPageVersion.Value}:"
                + $"{snapshot.Options.IncludeDom}:{snapshot.Options.IncludeAccessibilityTree}:{snapshot.Options.IncludeHtml}:"
                + $"{snapshot.Options.MaxNodes}:{snapshot.Options.MaxTextLength}",
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
        Proto.OperationResult result, DesktopCapability expectedCapability, DesktopPageTarget? requestedTarget, DesktopLocator? requestedLocator = null, DesktopWaitCondition? requestedWaitCondition = null, DesktopTabAction? requestedTabAction = null)
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
            Proto.OperationResult.OutcomeOneofCase.ShellStatus => expectedCapability == DesktopCapability.ShellStatus,
            Proto.OperationResult.OutcomeOneofCase.Snapshot => expectedCapability == DesktopCapability.BrowserSnapshot,
            Proto.OperationResult.OutcomeOneofCase.Locate => expectedCapability == DesktopCapability.BrowserLocate,
            Proto.OperationResult.OutcomeOneofCase.Interact => expectedCapability == DesktopCapability.BrowserInteract,
            Proto.OperationResult.OutcomeOneofCase.WaitFor => expectedCapability == DesktopCapability.BrowserWaitFor,
            Proto.OperationResult.OutcomeOneofCase.Contexts => expectedCapability == DesktopCapability.BrowserContexts,
            Proto.OperationResult.OutcomeOneofCase.Tabs => expectedCapability == DesktopCapability.BrowserTabs,
            Proto.OperationResult.OutcomeOneofCase.Clipboard => expectedCapability == DesktopCapability.ShellClipboard,
            Proto.OperationResult.OutcomeOneofCase.Dialog => expectedCapability == DesktopCapability.ShellDialog,
            Proto.OperationResult.OutcomeOneofCase.FilePicker => expectedCapability == DesktopCapability.ShellFilePicker,
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
                if (result.OutcomeCase == Proto.OperationResult.OutcomeOneofCase.Contexts)
                {
                    var contexts = new List<DesktopContextInfo>(result.Contexts.Contexts.Count);
                    foreach (var context in result.Contexts.Contexts)
                    {
                        var pages = new List<DesktopPageInfo>(context.Pages.Count);
                        foreach (var page in context.Pages)
                        {
                            DesktopPageTarget? pageTarget;
                            try
                            {
                                pageTarget = new DesktopPageTarget(page.ContextId, page.PageId);
                            }
                            catch (ArgumentException)
                            {
                                return CapabilityResult<DesktopCapabilityResponse>.Failure(
                                    DesktopCapabilityError.Internal("contexts outcome carries an unusable page target"));
                            }

                            Uri? pageUrl = null;
                            if (!string.IsNullOrEmpty(page.Url)
                                && !Uri.TryCreate(page.Url, UriKind.Absolute, out pageUrl))
                            {
                                return CapabilityResult<DesktopCapabilityResponse>.Failure(
                                    DesktopCapabilityError.Internal("contexts outcome carries a non-absolute page url"));
                            }

                            try
                            {
                                pages.Add(new DesktopPageInfo(
                                    pageTarget,
                                    ToPageVersion(page.PageVersion),
                                    NullIfEmpty(page.Title),
                                    pageUrl,
                                    page.IsActive,
                                    page.IsAgentTarget,
                                    page.CanGoBack,
                                    page.CanGoForward,
                                    page.IsLoading));
                            }
                            catch (ArgumentException)
                            {
                                return CapabilityResult<DesktopCapabilityResponse>.Failure(
                                    DesktopCapabilityError.Internal("contexts outcome carries a page without a live version"));
                            }
                        }

                        var trust = Enum.TryParse<DesktopContextTrust>(context.Trust, ignoreCase: false, out var parsedTrust)
                            ? parsedTrust
                            : DesktopContextTrust.Untrusted;

                        try
                        {
                            contexts.Add(new DesktopContextInfo(context.ContextId, trust, pages));
                        }
                        catch (ArgumentException)
                        {
                            return CapabilityResult<DesktopCapabilityResponse>.Failure(
                                DesktopCapabilityError.Internal("contexts outcome carries an unusable context"));
                        }
                    }

                    return CapabilityResult<DesktopCapabilityResponse>.Success(
                        DesktopCapabilityResponse.FromContexts(new DesktopContexts(contexts)));
                }

                if (result.OutcomeCase == Proto.OperationResult.OutcomeOneofCase.FilePicker)
                {
                    return CapabilityResult<DesktopCapabilityResponse>.Success(
                        DesktopCapabilityResponse.FromFilePicker(new DesktopFilePickerResult(
                            result.FilePicker.Canceled,
                            result.FilePicker.Paths.Count == 0 ? null : result.FilePicker.Paths.ToArray())));
                }

                if (result.OutcomeCase == Proto.OperationResult.OutcomeOneofCase.Dialog)
                {
                    // 未登记的选择按"用户没有肯定"处理（fail safe），而不是猜测成确定。
                    var choice = Enum.TryParse<DesktopDialogChoice>(result.Dialog.Choice, ignoreCase: true, out var parsed)
                        ? parsed
                        : DesktopDialogChoice.Cancel;

                    return CapabilityResult<DesktopCapabilityResponse>.Success(
                        DesktopCapabilityResponse.FromDialog(new DesktopDialogResult(choice)));
                }

                if (result.OutcomeCase == Proto.OperationResult.OutcomeOneofCase.Clipboard)
                {
                    // 剪贴板内容只回传给调用方：不写日志、不进审计（审计记的是操作与形状）。
                    return CapabilityResult<DesktopCapabilityResponse>.Success(
                        DesktopCapabilityResponse.FromClipboard(new DesktopClipboardContent(
                            NullIfEmpty(result.Clipboard.Text), result.Clipboard.Truncated)));
                }

                if (result.OutcomeCase == Proto.OperationResult.OutcomeOneofCase.Tabs)
                {
                    if (requestedTarget is null)
                    {
                        return CapabilityResult<DesktopCapabilityResponse>.Failure(
                            DesktopCapabilityError.Internal("tabs result cannot be correlated without the requested target"));
                    }

                    var remaining = DecodeContexts(result.Tabs.Remaining);
                    if (remaining.IsFailure)
                    {
                        return CapabilityResult<DesktopCapabilityResponse>.Failure(remaining.Error);
                    }

                    var tabAction = DesktopTabActionWire.TryParse(result.Tabs.Action, out var parsedAction)
                        ? parsedAction
                        : requestedTabAction ?? DesktopTabAction.Activate;

                    Uri? tabUrl = null;
                    if (!string.IsNullOrEmpty(result.Tabs.Page?.Url)
                        && !Uri.TryCreate(result.Tabs.Page.Url, UriKind.Absolute, out tabUrl))
                    {
                        return CapabilityResult<DesktopCapabilityResponse>.Failure(
                            DesktopCapabilityError.Internal("tabs outcome carries a non-absolute url"));
                    }

                    return CapabilityResult<DesktopCapabilityResponse>.Success(
                        DesktopCapabilityResponse.FromTabs(new DesktopTabsResult(
                            requestedTarget,
                            tabAction,
                            new DesktopPageState(
                                requestedTarget,
                                tabUrl,
                                ToPageVersion(result.Tabs.Page?.PageVersion ?? 0),
                                DesktopPageReadinessWire.Parse(result.Tabs.Page?.Readiness)),
                            result.Tabs.TabClosed,
                            remaining.Value)));
                }

                if (result.OutcomeCase == Proto.OperationResult.OutcomeOneofCase.WaitFor)
                {
                    if (requestedTarget is null)
                    {
                        return CapabilityResult<DesktopCapabilityResponse>.Failure(
                            DesktopCapabilityError.Internal("wait_for result cannot be correlated without the requested target"));
                    }

                    Uri? waitUrl = null;
                    if (!string.IsNullOrEmpty(result.WaitFor.Page?.Url)
                        && !Uri.TryCreate(result.WaitFor.Page.Url, UriKind.Absolute, out waitUrl))
                    {
                        return CapabilityResult<DesktopCapabilityResponse>.Failure(
                            DesktopCapabilityError.Internal("wait_for outcome carries a non-absolute url"));
                    }

                    // 线名未登记时按请求里的条件回填（结果帧只是回显；真源是请求）。
                    var waitKind = DesktopWaitConditionKindWire.TryParse(result.WaitFor.ConditionKind, out var parsedKind)
                        ? parsedKind
                        : requestedWaitCondition?.Kind ?? DesktopWaitConditionKind.Selector;
                    var waitValue = string.IsNullOrEmpty(result.WaitFor.ConditionValue)
                        ? requestedWaitCondition?.Value ?? "*"
                        : result.WaitFor.ConditionValue;

                    return CapabilityResult<DesktopCapabilityResponse>.Success(
                        DesktopCapabilityResponse.FromWait(new DesktopWaitResult(
                            requestedTarget,
                            new DesktopWaitCondition(waitKind, waitValue),
                            result.WaitFor.TimedOut,
                            new DesktopPageState(
                                requestedTarget,
                                waitUrl,
                                ToPageVersion(result.WaitFor.Page?.PageVersion ?? 0),
                                DesktopPageReadinessWire.Parse(result.WaitFor.Page?.Readiness)),
                            NullIfEmpty(result.WaitFor.Error))));
                }

                if (result.OutcomeCase == Proto.OperationResult.OutcomeOneofCase.Interact)
                {
                    if (requestedTarget is null)
                    {
                        return CapabilityResult<DesktopCapabilityResponse>.Failure(
                            DesktopCapabilityError.Internal(
                                "interaction result cannot be correlated without the requested target"));
                    }

                    Uri? interactUrl = null;
                    if (!string.IsNullOrEmpty(result.Interact.Page?.Url)
                        && !Uri.TryCreate(result.Interact.Page.Url, UriKind.Absolute, out interactUrl))
                    {
                        return CapabilityResult<DesktopCapabilityResponse>.Failure(
                            DesktopCapabilityError.Internal("interaction outcome carries a non-absolute url"));
                    }

                    DesktopElementRef? element = null;
                    if (result.Interact.Element is { } interacted)
                    {
                        try
                        {
                            element = new DesktopElementRef(
                                interacted.Ref,
                                interacted.Tag,
                                ToPageVersion(interacted.PageVersion),
                                NullIfEmpty(interacted.Role),
                                NullIfEmpty(interacted.Name),
                                NullIfEmpty(interacted.Text),
                                interacted.Visible,
                                interacted.Enabled,
                                interacted.HasChecked ? interacted.Checked : null);
                        }
                        catch (ArgumentException)
                        {
                            return CapabilityResult<DesktopCapabilityResponse>.Failure(
                                DesktopCapabilityError.Internal("interaction outcome carries an unusable element reference"));
                        }
                    }

                    var pageState = new DesktopPageState(
                        requestedTarget,
                        interactUrl,
                        ToPageVersion(result.Interact.Page?.PageVersion ?? 0),
                        DesktopPageReadinessWire.Parse(result.Interact.Page?.Readiness));

                    return CapabilityResult<DesktopCapabilityResponse>.Success(
                        DesktopCapabilityResponse.FromInteract(new DesktopInteractionResult(
                            requestedTarget, pageState, element)));
                }

                if (result.OutcomeCase == Proto.OperationResult.OutcomeOneofCase.Locate)
                {
                    if (requestedTarget is null)
                    {
                        return CapabilityResult<DesktopCapabilityResponse>.Failure(
                            DesktopCapabilityError.Internal(
                                "locate result cannot be correlated without the requested target"));
                    }

                    var elements = new List<DesktopElementRef>(result.Locate.Elements.Count);
                    foreach (var element in result.Locate.Elements)
                    {
                        try
                        {
                            elements.Add(new DesktopElementRef(
                                element.Ref,
                                element.Tag,
                                ToPageVersion(element.PageVersion),
                                NullIfEmpty(element.Role),
                                NullIfEmpty(element.Name),
                                NullIfEmpty(element.Text),
                                element.Visible,
                                element.Enabled,
                                element.HasChecked ? element.Checked : null));
                        }
                        catch (ArgumentException)
                        {
                            // 引用形态非法（空 Ref/Tag 或没有有效版本）：fail closed，不把坏引用交给上层。
                            return CapabilityResult<DesktopCapabilityResponse>.Failure(
                                DesktopCapabilityError.Internal("locate outcome carries an unusable element reference"));
                        }
                    }

                    return CapabilityResult<DesktopCapabilityResponse>.Success(
                        DesktopCapabilityResponse.FromLocate(new DesktopLocateResult(
                            requestedTarget,
                            requestedLocator ?? new DesktopLocator(DesktopLocatorKind.Css, "*"),
                            elements,
                            result.Locate.Truncated,
                            ToPageVersion(result.Locate.PageVersion))));
                }

                if (result.OutcomeCase == Proto.OperationResult.OutcomeOneofCase.Snapshot)
                {
                    if (requestedTarget is null)
                    {
                        return CapabilityResult<DesktopCapabilityResponse>.Failure(
                            DesktopCapabilityError.Internal(
                                "snapshot result cannot be correlated without the requested target"));
                    }

                    return CapabilityResult<DesktopCapabilityResponse>.Success(
                        DesktopCapabilityResponse.FromSnapshot(new DesktopSnapshot(
                            requestedTarget,
                            NullIfEmpty(result.Snapshot.DomText),
                            NullIfEmpty(result.Snapshot.AccessibilityTree),
                            NullIfEmpty(result.Snapshot.Html),
                            result.Snapshot.Truncated,
                            result.Snapshot.NodeCount,
                            ToPageVersion(result.Snapshot.PageVersion))));
                }

                if (result.OutcomeCase == Proto.OperationResult.OutcomeOneofCase.ShellStatus)
                {
                    // 只读 Shell 状态：线名未知按保守值处理（自动化状态折叠为 user_takeover）。
                    return CapabilityResult<DesktopCapabilityResponse>.Success(
                        DesktopCapabilityResponse.FromShellStatus(new DesktopShellStatus(
                            DesktopShellStatusWire.ParseWindowState(result.ShellStatus.WindowState),
                            result.ShellStatus.TrayVisible,
                            DesktopShellStatusWire.ParseAutomationState(result.ShellStatus.AutomationState),
                            (int)Math.Min(result.ShellStatus.OpenPageCount, int.MaxValue))));
                }

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
                        DesktopPageReadinessWire.Parse(result.PageState.Readiness))));
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

    private static CapabilityResult<DesktopContexts> DecodeContexts(Proto.ContextsOutcome? outcome)
    {
        var contexts = new List<DesktopContextInfo>(outcome?.Contexts.Count ?? 0);

        foreach (var context in outcome?.Contexts ?? [])
        {
            var pages = new List<DesktopPageInfo>(context.Pages.Count);
            foreach (var page in context.Pages)
            {
                DesktopPageTarget pageTarget;
                try
                {
                    pageTarget = new DesktopPageTarget(page.ContextId, page.PageId);
                }
                catch (ArgumentException)
                {
                    return CapabilityResult<DesktopContexts>.Failure(
                        DesktopCapabilityError.Internal("contexts outcome carries an unusable page target"));
                }

                Uri? pageUrl = null;
                if (!string.IsNullOrEmpty(page.Url) && !Uri.TryCreate(page.Url, UriKind.Absolute, out pageUrl))
                {
                    return CapabilityResult<DesktopContexts>.Failure(
                        DesktopCapabilityError.Internal("contexts outcome carries a non-absolute page url"));
                }

                try
                {
                    pages.Add(new DesktopPageInfo(
                        pageTarget,
                        ToPageVersion(page.PageVersion),
                        NullIfEmpty(page.Title),
                        pageUrl,
                        page.IsActive,
                        page.IsAgentTarget,
                        page.CanGoBack,
                        page.CanGoForward,
                        page.IsLoading));
                }
                catch (ArgumentException)
                {
                    return CapabilityResult<DesktopContexts>.Failure(
                        DesktopCapabilityError.Internal("contexts outcome carries a page without a live version"));
                }
            }

            var trust = Enum.TryParse<DesktopContextTrust>(context.Trust, ignoreCase: false, out var parsedTrust)
                ? parsedTrust
                : DesktopContextTrust.Untrusted;

            try
            {
                contexts.Add(new DesktopContextInfo(context.ContextId, trust, pages));
            }
            catch (ArgumentException)
            {
                return CapabilityResult<DesktopContexts>.Failure(
                    DesktopCapabilityError.Internal("contexts outcome carries an unusable context"));
            }
        }

        return CapabilityResult<DesktopContexts>.Success(new DesktopContexts(contexts));
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static DesktopPageVersion ToPageVersion(long value) =>
        value > 0 ? DesktopPageVersion.Require(value) : DesktopPageVersion.Unknown;
}

