using System.Security.Cryptography;
using Google.Protobuf;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Proto = Pudding.Rpc.Protocol.V1;

namespace Pudding.DesktopConnection;

/// <summary>已解码且通过结构校验的命令（领域侧表示，不含任何 proto 类型）。</summary>
internal sealed record DecodedCommand(
    OperationId OperationId,
    ConnectionGeneration Generation,
    DesktopCapabilityDescriptor Capability,
    DesktopCapabilityRequest Request,
    DesktopCallContext Context,
    string? TraceId,
    string Fingerprint);

/// <summary>
/// Core → Desktop 帧的解码与<b>结构校验</b>（fail closed）。
/// 校验失败返回领域错误，由连接层回一条错误结果帧 —— 不关闭整条通道。
/// </summary>
internal static class CoreFrameMapping
{
    private const int MaxTraceIdLength = 128;

    private const int MaxScriptLength = 256 * 1024;

    public static CapabilityResult<DecodedCommand> Decode(Proto.CapabilityCommand? command, DesktopInstanceId desktopId)
    {
        if (command is null)
        {
            return Fail("command payload is missing");
        }

        if (!OperationId.IsValid(command.OperationId))
        {
            return Fail("operation_id is missing or is not a valid identifier");
        }

        if (command.Generation == 0 || command.Generation > long.MaxValue)
        {
            return Fail("generation must be a positive 63-bit value");
        }

        if (command.Deadline is null)
        {
            return Fail("deadline is required");
        }

        if (!DesktopCapabilities.TryGetByName(command.Capability, out var descriptor))
        {
            return CapabilityResult<DecodedCommand>.Failure(
                DesktopCapabilityError.UnsupportedCapability(WireText.Truncate(command.Capability, 64)));
        }

        var request = BuildRequest(command, descriptor);
        if (!request.IsSuccess)
        {
            return CapabilityResult<DecodedCommand>.Failure(request.Error);
        }

        DesktopCorrelationId? correlationId = null;
        if (!string.IsNullOrEmpty(command.CorrelationId) && DesktopCorrelationId.IsValid(command.CorrelationId))
        {
            correlationId = new DesktopCorrelationId(command.CorrelationId);
        }

        var operationId = new OperationId(command.OperationId);
        var context = new DesktopCallContext(
            desktopId, operationId, command.Deadline.ToDateTimeOffset(), correlationId);
        var traceId = WireText.Truncate(command.TraceId, MaxTraceIdLength);

        return CapabilityResult<DecodedCommand>.Success(new DecodedCommand(
            operationId,
            ConnectionGeneration.Require((long)command.Generation),
            descriptor,
            request.Value,
            context,
            string.IsNullOrEmpty(traceId) ? null : traceId,
            Fingerprint(command, descriptor)));
    }

    private static CapabilityResult<DesktopCapabilityRequest> BuildRequest(
        Proto.CapabilityCommand command, DesktopCapabilityDescriptor descriptor)
    {
        switch (descriptor.Capability)
        {
            case DesktopCapability.WebViewNavigate:
            {
                if (command.PayloadCase != Proto.CapabilityCommand.PayloadOneofCase.Navigate)
                {
                    return Mismatch(descriptor);
                }

                var payload = command.Navigate;
                var target = DecodeTarget(payload.Target);
                if (target is null)
                {
                    return CapabilityResult<DesktopCapabilityRequest>.Failure(
                        DesktopCapabilityError.InvalidTarget("webview.navigate requires an explicit context_id/page_id target"));
                }

                if (!DesktopNavigationActionWire.TryParse(payload.Action, out var navigationAction))
                {
                    return FailRequest($"navigate action '{payload.Action}' is not registered");
                }

                // 只有 goto 需要地址；其余动作作用于当前页（地址由页面自己决定）。
                Uri? navigateUrl = null;
                if (navigationAction == DesktopNavigationAction.Goto
                    && (string.IsNullOrWhiteSpace(payload.Url)
                        || !Uri.TryCreate(payload.Url, UriKind.Absolute, out navigateUrl)))
                {
                    return FailRequest("navigate url must be an absolute URL");
                }

                if (payload.ExpectedPageVersion < 0)
                {
                    return FailRequest("expected_page_version must not be negative");
                }

                try
                {
                    return CapabilityResult<DesktopCapabilityRequest>.Success(
                        DesktopCapabilityRequest.ForNavigate(new NavigateRequest(
                            target,
                            navigateUrl,
                            ToPageVersion(payload.ExpectedPageVersion),
                            navigationAction,
                            payload.TimeoutMs == 0 ? NavigateRequest.DefaultTimeoutMs : payload.TimeoutMs)));
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    return FailRequest($"navigate timeout is out of range ({ex.ParamName})");
                }
            }

            case DesktopCapability.WebViewExecuteJavascript:
            {
                if (command.PayloadCase != Proto.CapabilityCommand.PayloadOneofCase.ExecuteJavascript)
                {
                    return Mismatch(descriptor);
                }

                var payload = command.ExecuteJavascript;
                var target = DecodeTarget(payload.Target);
                if (target is null)
                {
                    return CapabilityResult<DesktopCapabilityRequest>.Failure(
                        DesktopCapabilityError.InvalidTarget("webview.execute_javascript requires an explicit context_id/page_id target"));
                }

                if (string.IsNullOrWhiteSpace(payload.Script))
                {
                    return FailRequest("script must be non-empty");
                }

                if (payload.Script.Length > MaxScriptLength)
                {
                    return FailRequest($"script exceeds the {MaxScriptLength} character transport limit");
                }

                if (payload.MaxResultBytes > JavascriptRequest.MaxResultBytesLimit)
                {
                    return FailRequest($"max_result_bytes exceeds the {JavascriptRequest.MaxResultBytesLimit} byte limit");
                }

                if (payload.ExpectedPageVersion < 0)
                {
                    return FailRequest("expected_page_version must not be negative");
                }

                var maxResultBytes = payload.MaxResultBytes == 0
                    ? JavascriptRequest.DefaultMaxResultBytes
                    : (int)payload.MaxResultBytes;

                return CapabilityResult<DesktopCapabilityRequest>.Success(
                    DesktopCapabilityRequest.ForJavascript(new JavascriptRequest(
                        target,
                        payload.Script,
                        ToPageVersion(payload.ExpectedPageVersion),
                        maxResultBytes)));
            }

            case DesktopCapability.WebViewPageState:
            {
                if (command.PayloadCase != Proto.CapabilityCommand.PayloadOneofCase.GetPageState)
                {
                    return Mismatch(descriptor);
                }

                var target = DecodeTarget(command.GetPageState.Target);
                if (target is null)
                {
                    return CapabilityResult<DesktopCapabilityRequest>.Failure(
                        DesktopCapabilityError.InvalidTarget("webview.page_state requires an explicit context_id/page_id target"));
                }

                return CapabilityResult<DesktopCapabilityRequest>.Success(DesktopCapabilityRequest.ForPageState(target));
            }

            case DesktopCapability.ShellNotification:
            {
                if (command.PayloadCase != Proto.CapabilityCommand.PayloadOneofCase.ShowNotification)
                {
                    return Mismatch(descriptor);
                }

                var payload = command.ShowNotification;
                if (string.IsNullOrWhiteSpace(payload.Title) || string.IsNullOrWhiteSpace(payload.Message))
                {
                    return FailRequest("notification title and message must be non-empty");
                }

                if (payload.Title.Length > DesktopNotificationRequest.MaxTitleLength
                    || payload.Message.Length > DesktopNotificationRequest.MaxMessageLength)
                {
                    return FailRequest("notification title or message exceeds the maximum length");
                }

                return CapabilityResult<DesktopCapabilityRequest>.Success(
                    DesktopCapabilityRequest.ForNotification(new DesktopNotificationRequest(
                        payload.Title, payload.Message, ToDomain(payload.Priority))));
            }

            case DesktopCapability.BrowserSnapshot:
            {
                if (command.PayloadCase != Proto.CapabilityCommand.PayloadOneofCase.Snapshot)
                {
                    return Mismatch(descriptor);
                }

                var target = DecodeTarget(command.Snapshot.Target);
                if (target is null)
                {
                    return CapabilityResult<DesktopCapabilityRequest>.Failure(
                        DesktopCapabilityError.InvalidTarget("browser.snapshot requires an explicit context_id/page_id target"));
                }

                if (command.Snapshot.ExpectedPageVersion < 0)
                {
                    return FailRequest("expected_page_version must not be negative");
                }

                var budget = command.Snapshot.Budget;
                DesktopSnapshotOptions options;
                try
                {
                    options = new DesktopSnapshotOptions(
                        includeDom: budget?.IncludeDom ?? true,
                        includeAccessibilityTree: budget?.IncludeAccessibilityTree ?? true,
                        includeHtml: budget?.IncludeHtml ?? false,
                        maxNodes: budget is null || budget.MaxNodes == 0
                            ? DesktopSnapshotOptions.DefaultMaxNodes
                            : budget.MaxNodes,
                        maxTextLength: budget is null || budget.MaxTextLength == 0
                            ? DesktopSnapshotOptions.DefaultMaxTextLength
                            : budget.MaxTextLength,
                        includeHidden: budget?.IncludeHidden ?? false,
                        includeIframes: budget?.IncludeIframes ?? true,
                        includeShadowDom: budget?.IncludeShadowDom ?? true,
                        maxDepth: budget is null || budget.MaxDepth == 0 ? 24 : budget.MaxDepth);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    return FailRequest($"snapshot budget is out of range ({ex.ParamName})");
                }

                if (!options.HasContent)
                {
                    return FailRequest("snapshot must request at least one of dom/accessibility_tree/html");
                }

                return CapabilityResult<DesktopCapabilityRequest>.Success(
                    DesktopCapabilityRequest.ForSnapshot(new BrowserSnapshotRequest(
                        target, ToPageVersion(command.Snapshot.ExpectedPageVersion), options)));
            }
            case DesktopCapability.BrowserLocate:
            {
                if (command.PayloadCase != Proto.CapabilityCommand.PayloadOneofCase.Locate)
                {
                    return Mismatch(descriptor);
                }

                var locateTarget = DecodeTarget(command.Locate.Target);
                if (locateTarget is null)
                {
                    return CapabilityResult<DesktopCapabilityRequest>.Failure(
                        DesktopCapabilityError.InvalidTarget("browser.locate requires an explicit context_id/page_id target"));
                }

                if (command.Locate.ExpectedPageVersion < 0)
                {
                    return FailRequest("expected_page_version must not be negative");
                }

                var spec = command.Locate.Locator;
                if (spec is null || !DesktopLocatorKindWire.TryParse(spec.Kind, out var locatorKind))
                {
                    return FailRequest("locator kind is missing or not registered");
                }

                DesktopLocator locator;
                try
                {
                    locator = new DesktopLocator(
                        locatorKind,
                        spec.Value,
                        string.IsNullOrEmpty(spec.Name) ? null : spec.Name,
                        spec.Exact,
                        spec.Nth < 0 ? null : spec.Nth,
                        string.IsNullOrEmpty(spec.HasText) ? null : spec.HasText);
                }
                catch (ArgumentException ex)
                {
                    return FailRequest($"locator is not usable ({ex.ParamName})");
                }

                try
                {
                    return CapabilityResult<DesktopCapabilityRequest>.Success(
                        DesktopCapabilityRequest.ForLocate(new BrowserLocateRequest(
                            locateTarget,
                            locator,
                            ToPageVersion(command.Locate.ExpectedPageVersion),
                            command.Locate.MaxResults == 0
                                ? BrowserLocateRequest.DefaultMaxResults
                                : command.Locate.MaxResults)));
                }
                catch (ArgumentOutOfRangeException)
                {
                    return FailRequest("max_results is out of range");
                }
            }

            case DesktopCapability.BrowserInteract:
            {
                if (command.PayloadCase != Proto.CapabilityCommand.PayloadOneofCase.Interact)
                {
                    return Mismatch(descriptor);
                }

                var interactTarget = DecodeTarget(command.Interact.Target);
                if (interactTarget is null)
                {
                    return CapabilityResult<DesktopCapabilityRequest>.Failure(
                        DesktopCapabilityError.InvalidTarget("browser.interact requires an explicit context_id/page_id target"));
                }

                if (command.Interact.ExpectedPageVersion <= 0)
                {
                    return FailRequest("interaction must pin a valid expected_page_version");
                }

                if (!DesktopInteractionActionWire.TryParse(command.Interact.Action, out var action))
                {
                    return FailRequest("interaction action is missing or not registered");
                }

                DesktopLocator? locator = null;
                if (command.Interact.Locator is { } spec)
                {
                    if (!DesktopLocatorKindWire.TryParse(spec.Kind, out var locatorKind) || string.IsNullOrEmpty(spec.Value))
                    {
                        return FailRequest("interaction locator is missing or not registered");
                    }

                    try
                    {
                        locator = new DesktopLocator(
                            locatorKind,
                            spec.Value,
                            string.IsNullOrEmpty(spec.Name) ? null : spec.Name,
                            spec.Exact,
                            spec.Nth < 0 ? null : spec.Nth,
                            string.IsNullOrEmpty(spec.HasText) ? null : spec.HasText);
                    }
                    catch (ArgumentException ex)
                    {
                        return FailRequest($"interaction locator is not usable ({ex.ParamName})");
                    }
                }

                try
                {
                    // 按动作的参数要求在这里就校验（缺/多参数 ⇒ invalid_request，而不是发出去让页面猜）。
                    return CapabilityResult<DesktopCapabilityRequest>.Success(
                        DesktopCapabilityRequest.ForInteract(new BrowserInteractRequest(
                            interactTarget,
                            action,
                            ToPageVersion(command.Interact.ExpectedPageVersion),
                            locator,
                            string.IsNullOrEmpty(command.Interact.Text) ? null : command.Interact.Text,
                            command.Interact.Values.Count == 0 ? null : command.Interact.Values.ToArray(),
                            command.Interact.HasChecked ? command.Interact.Checked : null,
                            command.Interact.DeltaX == 0 ? null : command.Interact.DeltaX,
                            command.Interact.DeltaY == 0 ? null : command.Interact.DeltaY)));
                }
                catch (ArgumentException ex)
                {
                    return FailRequest($"interaction parameters do not match the action ({ex.ParamName})");
                }
            }

            case DesktopCapability.BrowserWaitFor:
            {
                if (command.PayloadCase != Proto.CapabilityCommand.PayloadOneofCase.WaitFor)
                {
                    return Mismatch(descriptor);
                }

                var waitTarget = DecodeTarget(command.WaitFor.Target);
                if (waitTarget is null)
                {
                    return CapabilityResult<DesktopCapabilityRequest>.Failure(
                        DesktopCapabilityError.InvalidTarget("browser.wait_for requires an explicit context_id/page_id target"));
                }

                if (!DesktopWaitConditionKindWire.TryParse(command.WaitFor.ConditionKind, out var waitKind))
                {
                    return FailRequest("wait condition kind is missing or not registered");
                }

                try
                {
                    return CapabilityResult<DesktopCapabilityRequest>.Success(
                        DesktopCapabilityRequest.ForWaitFor(new BrowserWaitForRequest(
                            waitTarget,
                            new DesktopWaitCondition(waitKind, command.WaitFor.ConditionValue),
                            command.WaitFor.TimeoutMs == 0
                                ? BrowserWaitForRequest.DefaultTimeoutMs
                                : command.WaitFor.TimeoutMs,
                            ToPageVersion(command.WaitFor.ExpectedPageVersion))));
                }
                catch (ArgumentException ex)
                {
                    return FailRequest($"wait condition is not usable ({ex.ParamName})");
                }
            }

            case DesktopCapability.BrowserContexts:
            {
                if (command.PayloadCase != Proto.CapabilityCommand.PayloadOneofCase.Contexts)
                {
                    return Mismatch(descriptor);
                }

                // 无参数能力：不接受调用方指定目标（浏览器作用域）。
                return CapabilityResult<DesktopCapabilityRequest>.Success(DesktopCapabilityRequest.ForContexts());
            }

            case DesktopCapability.BrowserTabs:
            {
                if (command.PayloadCase != Proto.CapabilityCommand.PayloadOneofCase.Tabs)
                {
                    return Mismatch(descriptor);
                }

                if (!DesktopTabActionWire.TryParse(command.Tabs.Action, out var tabAction))
                {
                    return FailRequest("tab action is missing or not registered");
                }

                if (tabAction == DesktopTabAction.New)
                {
                    // 新建：没有目标页、也不钉版本；上下文由 context_id 指明。
                    if (string.IsNullOrWhiteSpace(command.Tabs.ContextId))
                    {
                        return FailRequest("creating a tab requires a context_id");
                    }

                    if (command.Tabs.ExpectedPageVersion != 0)
                    {
                        return FailRequest("creating a tab must not pin an expected_page_version");
                    }

                    Uri? newTabUrl = null;
                    if (!string.IsNullOrEmpty(command.Tabs.Url)
                        && !Uri.TryCreate(command.Tabs.Url, UriKind.Absolute, out newTabUrl))
                    {
                        return FailRequest("tab url must be an absolute URL");
                    }

                    return CapabilityResult<DesktopCapabilityRequest>.Success(
                        DesktopCapabilityRequest.ForTabs(BrowserTabsRequest.New(
                            command.Tabs.ContextId, newTabUrl, command.Tabs.Activate)));
                }

                var tabsTarget = DecodeTarget(command.Tabs.Target);
                if (tabsTarget is null)
                {
                    return CapabilityResult<DesktopCapabilityRequest>.Failure(
                        DesktopCapabilityError.InvalidTarget("browser.tabs requires an explicit context_id/page_id target"));
                }

                if (command.Tabs.ExpectedPageVersion <= 0)
                {
                    return FailRequest("tab operation must pin a valid expected_page_version");
                }

                return CapabilityResult<DesktopCapabilityRequest>.Success(
                    DesktopCapabilityRequest.ForTabs(new BrowserTabsRequest(
                        tabsTarget, tabAction, ToPageVersion(command.Tabs.ExpectedPageVersion), activate: command.Tabs.Activate)));
            }

            case DesktopCapability.ShellClipboard:
            {
                if (command.PayloadCase != Proto.CapabilityCommand.PayloadOneofCase.ReadClipboard)
                {
                    return Mismatch(descriptor);
                }

                try
                {
                    return CapabilityResult<DesktopCapabilityRequest>.Success(
                        DesktopCapabilityRequest.ForClipboard(new ClipboardReadRequest(
                            command.ReadClipboard.MaxCharacters == 0
                                ? ClipboardReadRequest.DefaultMaxCharacters
                                : command.ReadClipboard.MaxCharacters)));
                }
                catch (ArgumentOutOfRangeException)
                {
                    return FailRequest("clipboard budget is out of range");
                }
            }

            case DesktopCapability.ShellDialog:
            {
                if (command.PayloadCase != Proto.CapabilityCommand.PayloadOneofCase.ShowDialog)
                {
                    return Mismatch(descriptor);
                }

                if (!Enum.TryParse<DesktopDialogButtons>(command.ShowDialog.Buttons, ignoreCase: true, out var buttons))
                {
                    return FailRequest("dialog button set is missing or not registered");
                }

                try
                {
                    return CapabilityResult<DesktopCapabilityRequest>.Success(
                        DesktopCapabilityRequest.ForDialog(new DesktopDialogRequest(
                            command.ShowDialog.Title, command.ShowDialog.Message, buttons)));
                }
                catch (ArgumentException ex)
                {
                    return FailRequest($"dialog request is not usable ({ex.ParamName})");
                }
            }

            case DesktopCapability.ShellFilePicker:
            {
                if (command.PayloadCase != Proto.CapabilityCommand.PayloadOneofCase.ShowFilePicker)
                {
                    return Mismatch(descriptor);
                }

                try
                {
                    return CapabilityResult<DesktopCapabilityRequest>.Success(
                        DesktopCapabilityRequest.ForFilePicker(new DesktopFilePickerRequest(
                            command.ShowFilePicker.Title,
                            command.ShowFilePicker.AllowMultiple,
                            command.ShowFilePicker.Extensions.Count == 0 ? null : command.ShowFilePicker.Extensions.ToArray())));
                }
                catch (ArgumentException ex)
                {
                    return FailRequest($"file picker request is not usable ({ex.ParamName})");
                }
            }

            case DesktopCapability.ShellStatus:
            {
                if (command.PayloadCase != Proto.CapabilityCommand.PayloadOneofCase.GetShellStatus)
                {
                    return Mismatch(descriptor);
                }

                // 无参数能力：payload 必须存在但为空，调用方不能借它夹带目标。
                return CapabilityResult<DesktopCapabilityRequest>.Success(DesktopCapabilityRequest.ForShellStatus());
            }

            default:
                // 目录里存在但本切片尚无 payload 的能力（dialog/picker/clipboard 属切片 E）。
                return CapabilityResult<DesktopCapabilityRequest>.Failure(
                    DesktopCapabilityError.UnsupportedCapability(descriptor.Name));
        }
    }

    private static DesktopPageTarget? DecodeTarget(Proto.CommandTarget? target)
    {
        if (target is null || string.IsNullOrEmpty(target.ContextId) || string.IsNullOrEmpty(target.PageId))
        {
            return null;
        }

        try
        {
            return new DesktopPageTarget(target.ContextId, target.PageId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Fingerprint(Proto.CapabilityCommand command, DesktopCapabilityDescriptor descriptor)
    {
        var payload = command.PayloadCase switch
        {
            Proto.CapabilityCommand.PayloadOneofCase.Navigate => command.Navigate.ToByteArray(),
            Proto.CapabilityCommand.PayloadOneofCase.ExecuteJavascript => command.ExecuteJavascript.ToByteArray(),
            Proto.CapabilityCommand.PayloadOneofCase.ShowNotification => command.ShowNotification.ToByteArray(),
            _ => [],
        };

        return string.Concat(descriptor.Name, ":", Convert.ToHexString(SHA256.HashData(payload)));
    }

    private static DesktopPageVersion ToPageVersion(long value) =>
        value > 0 ? DesktopPageVersion.Require(value) : DesktopPageVersion.Unknown;

    private static DesktopNotificationPriority ToDomain(Proto.NotificationPriority priority) => priority switch
    {
        Proto.NotificationPriority.Low => DesktopNotificationPriority.Low,
        Proto.NotificationPriority.High => DesktopNotificationPriority.High,
        _ => DesktopNotificationPriority.Normal,
    };

    private static CapabilityResult<DecodedCommand> Fail(string message) =>
        CapabilityResult<DecodedCommand>.Failure(DesktopCapabilityError.InvalidRequest(message));

    private static CapabilityResult<DesktopCapabilityRequest> FailRequest(string message) =>
        CapabilityResult<DesktopCapabilityRequest>.Failure(DesktopCapabilityError.InvalidRequest(message));

    private static CapabilityResult<DesktopCapabilityRequest> Mismatch(DesktopCapabilityDescriptor descriptor) =>
        FailRequest($"payload does not match capability '{descriptor.Name}'");
}

/// <summary>领域 → wire 的编码。领域真源在 Contracts，这里只做机械映射。</summary>
internal static class DesktopFrameMapping
{
    /// <summary>单条快照文本字段的硬上限（超过即截断并标注 truncated）。</summary>
    private const int SnapshotTextLimit = 2_000_000;

    /// <summary>契约包围盒 → 线缆包围盒（null 就是 null——0 值不等于"没有"）。</summary>
    private static Proto.ElementBox? ToWireBox(DesktopElementBox? box) => box is null
        ? null
        : new Proto.ElementBox { X = box.X, Y = box.Y, Width = box.Width, Height = box.Height };

    private static Proto.ContextsOutcome BuildContexts(DesktopContexts contexts)
    {
        var outcome = new Proto.ContextsOutcome();
        foreach (var context in contexts.Contexts)
        {
            var wireContext = new Proto.ContextInfo
            {
                ContextId = context.ContextId,
                Trust = context.Trust.ToString(),
                Persistent = context.Persistent,
            };

            foreach (var page in context.Pages)
            {
                wireContext.Pages.Add(new Proto.PageInfo
                {
                    ContextId = page.Target.ContextId,
                    PageId = page.Target.PageId,
                    PageVersion = page.Version.Value,
                    Title = WireText.Truncate(page.Title, 512),
                    Url = page.Url?.AbsoluteUri ?? string.Empty,
                    IsActive = page.IsActive,
                    IsAgentTarget = page.IsAgentTarget,
                    CanGoBack = page.CanGoBack,
                    CanGoForward = page.CanGoForward,
                    IsLoading = page.IsLoading,
                });
            }

            outcome.Contexts.Add(wireContext);
        }

        return outcome;
    }

    public static Proto.DesktopFrame Hello(
        DesktopConnectionOptions options, IReadOnlyList<DesktopCapabilityDeclaration> declarations)
    {
        var hello = new Proto.DesktopHello
        {
            DesktopId = options.DesktopId.Value,
            ProcessInstanceId = options.ProcessInstanceId.Value,
            SupportedVersions = new Proto.ProtocolRange
            {
                Minimum = (uint)DesktopProtocolVersion.Minimum,
                Maximum = (uint)DesktopProtocolVersion.Current,
            },
            RequestedMaxFrameBytes = (uint)options.RequestedMaxFrameBytes,
        };

        foreach (var declaration in declarations)
        {
            hello.Capabilities.Add(new Proto.CapabilityDeclaration
            {
                Capability = declaration.Name,
                Version = (uint)declaration.Version,
            });
        }

        return new Proto.DesktopFrame { Hello = hello };
    }

    public static Proto.DesktopFrame HeartbeatAck(long sequence) =>
        new() { HeartbeatAck = new Proto.HeartbeatAck { Sequence = sequence } };

    public static Proto.DesktopFrame ChannelStatus(string state, string detail) =>
        new()
        {
            Event = new Proto.DesktopEvent
            {
                EventId = OperationId.NewId().Value,
                ChannelStatus = new Proto.ChannelStatusChanged
                {
                    State = WireText.Truncate(state, 64),
                    Detail = WireText.Truncate(detail, 256),
                },
            },
        };

    public static Proto.DesktopFrame ErrorResult(OperationId operationId, ConnectionGeneration generation, DesktopCapabilityError error) =>
        new()
        {
            Result = new Proto.OperationResult
            {
                OperationId = operationId.Value,
                Generation = (ulong)generation.Value,
                Error = ToWire(error),
            },
        };

    public static Proto.DesktopFrame Result(
        OperationId operationId,
        ConnectionGeneration generation,
        DesktopCapabilityDescriptor capability,
        DesktopCapabilityResponse response)
    {
        var result = new Proto.OperationResult
        {
            OperationId = operationId.Value,
            Generation = (ulong)generation.Value,
        };

        if (response.Error is { } error)
        {
            result.Error = ToWire(error);
        }
        else
        {
            switch (capability.Capability)
            {
                case DesktopCapability.WebViewNavigate when response.Navigate is { } navigate:
                    var wireNavigate = new Proto.NavigateOutcome
                    {
                        Disposition = ToWire(navigate.Disposition),
                        CurrentUrl = navigate.CurrentUrl?.AbsoluteUri ?? string.Empty,
                        PageVersion = navigate.PageVersion.Value,
                        ErrorText = WireText.Truncate(navigate.ErrorText, 512),
                        Title = WireText.Truncate(navigate.Title, 512),
                    };

                    // proto3 optional：只有确实知道结果时才设 presence（不知道 ≠ false）。
                    if (navigate.Ok is { } navigateOk)
                    {
                        wireNavigate.NavigationOk = navigateOk;
                    }

                    if (navigate.StatusCode is { } navigateStatus)
                    {
                        wireNavigate.StatusCode = navigateStatus;
                    }

                    result.Navigate = wireNavigate;
                    break;

                case DesktopCapability.WebViewExecuteJavascript when response.Javascript is { } javascript:
                    result.ExecuteJavascript = new Proto.JavascriptOutcome
                    {
                        Kind = ToWire(javascript.Kind),
                        JsonValue = javascript.JsonValue ?? string.Empty,
                        Truncated = javascript.Truncated,
                    };
                    break;

                case DesktopCapability.ShellNotification when response.Notification is { } notification:
                    result.ShowNotification = new Proto.NotificationOutcome
                    {
                        Shown = notification.Shown,
                        NotificationId = notification.NotificationId ?? string.Empty,
                    };
                    break;

                case DesktopCapability.WebViewPageState when response.PageState is { } pageState:
                    result.PageState = new Proto.PageStateOutcome
                    {
                        Url = pageState.Url?.AbsoluteUri ?? string.Empty,
                        PageVersion = pageState.Version.Value,
                        Readiness = DesktopPageReadinessWire.NameOf(pageState.Readiness),
                        Title = WireText.Truncate(pageState.Title, 512),
                    };
                    break;

                case DesktopCapability.BrowserSnapshot when response.Snapshot is { } snapshot:
                    result.Snapshot = new Proto.SnapshotOutcome
                    {
                        DomText = WireText.Truncate(snapshot.DomText, SnapshotTextLimit),
                        AccessibilityTree = WireText.Truncate(snapshot.AccessibilityTree, SnapshotTextLimit),
                        Html = WireText.Truncate(snapshot.Html, SnapshotTextLimit),
                        Truncated = snapshot.Truncated,
                        NodeCount = snapshot.NodeCount,
                        PageVersion = snapshot.PageVersion.Value,
                    };
                    break;
                case DesktopCapability.BrowserLocate when response.Locate is { } locate:
                    result.Locate = new Proto.LocateOutcome
                    {
                        Truncated = locate.Truncated,
                        PageVersion = locate.PageVersion.Value,
                    };
                    foreach (var element in locate.Elements)
                    {
                        var wireElement = new Proto.ElementRef
                        {
                            Ref = element.Reference,
                            Tag = element.Tag,
                            Role = element.Role ?? string.Empty,
                            Name = WireText.Truncate(element.Name, 512),
                            Text = WireText.Truncate(element.Text, 2048),
                            Visible = element.Visible,
                            Enabled = element.Enabled,
                            PageVersion = element.PageVersion.Value,
                            BoundingBox = ToWireBox(element.BoundingBox),
                        };

                        // proto3 optional：只有确实知道勾选状态时才设 presence（不知道 ≠ false）。
                        if (element.IsChecked is { } isChecked)
                        {
                            wireElement.Checked = isChecked;
                        }

                        result.Locate.Elements.Add(wireElement);
                    }

                    break;

                case DesktopCapability.BrowserInteract when response.Interact is { } interaction:
                    result.Interact = new Proto.InteractionOutcome
                    {
                        Page = new Proto.PageStateOutcome
                        {
                            Url = interaction.Page.Url?.AbsoluteUri ?? string.Empty,
                            PageVersion = interaction.Page.Version.Value,
                            Readiness = DesktopPageReadinessWire.NameOf(interaction.Page.Readiness),
                            Title = WireText.Truncate(interaction.Page.Title, 512),
                        },
                    };

                    if (interaction.Element is { } interacted)
                    {
                        var wireElement = new Proto.ElementRef
                        {
                            Ref = interacted.Reference,
                            Tag = interacted.Tag,
                            Role = interacted.Role ?? string.Empty,
                            Name = WireText.Truncate(interacted.Name, 512),
                            Text = WireText.Truncate(interacted.Text, 2048),
                            Visible = interacted.Visible,
                            Enabled = interacted.Enabled,
                            PageVersion = interacted.PageVersion.Value,
                            BoundingBox = ToWireBox(interacted.BoundingBox),
                        };

                        if (interacted.IsChecked is { } isChecked)
                        {
                            wireElement.Checked = isChecked;
                        }

                        result.Interact.Element = wireElement;
                    }

                    break;

                case DesktopCapability.BrowserWaitFor when response.Wait is { } wait:
                    result.WaitFor = new Proto.WaitOutcome
                    {
                        TimedOut = wait.TimedOut,
                        ConditionKind = DesktopWaitConditionKindWire.NameOf(wait.Condition.Kind),
                        ConditionValue = WireText.Truncate(wait.Condition.Value, 2048),
                        Error = WireText.Truncate(wait.Error, 512),
                        Page = new Proto.PageStateOutcome
                        {
                            Url = wait.Page.Url?.AbsoluteUri ?? string.Empty,
                            PageVersion = wait.Page.Version.Value,
                            Readiness = DesktopPageReadinessWire.NameOf(wait.Page.Readiness),
                            Title = WireText.Truncate(wait.Page.Title, 512),
                        },
                    };
                    break;

                case DesktopCapability.BrowserContexts when response.Contexts is { } contexts:
                    result.Contexts = new Proto.ContextsOutcome();
                    foreach (var context in contexts.Contexts)
                    {
                        var wireContext = new Proto.ContextInfo
                        {
                            ContextId = context.ContextId,
                            Trust = context.Trust.ToString(),
                Persistent = context.Persistent,
                        };

                        foreach (var page in context.Pages)
                        {
                            wireContext.Pages.Add(new Proto.PageInfo
                            {
                                ContextId = page.Target.ContextId,
                                PageId = page.Target.PageId,
                                PageVersion = page.Version.Value,
                                Title = WireText.Truncate(page.Title, 512),
                                Url = page.Url?.AbsoluteUri ?? string.Empty,
                                IsActive = page.IsActive,
                                IsAgentTarget = page.IsAgentTarget,
                                CanGoBack = page.CanGoBack,
                                CanGoForward = page.CanGoForward,
                                IsLoading = page.IsLoading,
                            });
                        }

                        result.Contexts.Contexts.Add(wireContext);
                    }

                    break;

                case DesktopCapability.BrowserTabs when response.Tabs is { } tabs:
                    result.Tabs = new Proto.TabsOutcome
                    {
                        Action = DesktopTabActionWire.NameOf(tabs.Action),
                        TabClosed = tabs.TabClosed,
                        Page = new Proto.PageStateOutcome
                        {
                            Url = tabs.Page.Url?.AbsoluteUri ?? string.Empty,
                            PageVersion = tabs.Page.Version.Value,
                            Readiness = DesktopPageReadinessWire.NameOf(tabs.Page.Readiness),
                            Title = WireText.Truncate(tabs.Page.Title, 512),
                        },
                        Remaining = BuildContexts(tabs.Remaining),
                    };

                    break;

                case DesktopCapability.ShellClipboard when response.Clipboard is { } clipboard:
                    result.Clipboard = new Proto.ClipboardOutcome
                    {
                        Text = WireText.Truncate(clipboard.Text, 1_000_000),
                        Truncated = clipboard.Truncated,
                    };
                    break;

                case DesktopCapability.ShellDialog when response.Dialog is { } dialog:
                    result.Dialog = new Proto.DialogOutcome { Choice = dialog.Choice.ToString() };
                    break;

                case DesktopCapability.ShellFilePicker when response.FilePicker is { } picker:
                    result.FilePicker = new Proto.FilePickerOutcome { Canceled = picker.Canceled };
                    // 路径只回传调用方：不进日志/审计。
                    result.FilePicker.Paths.AddRange(picker.Paths.Select(path => WireText.Truncate(path, 1024)));
                    break;

                case DesktopCapability.ShellStatus when response.ShellStatus is { } shellStatus:
                    result.ShellStatus = new Proto.ShellStatusOutcome
                    {
                        WindowState = DesktopShellStatusWire.NameOf(shellStatus.WindowState),
                        TrayVisible = shellStatus.TrayVisible,
                        AutomationState = DesktopShellStatusWire.NameOf(shellStatus.Automation),
                        OpenPageCount = (uint)shellStatus.OpenPageCount,
                    };
                    break;

                default:
                    result.Error = ToWire(DesktopCapabilityError.Internal(
                        "executor returned a payload that does not match the commanded capability"));
                    break;
            }
        }

        return new Proto.DesktopFrame { Result = result };
    }

    public static Proto.ErrorOutcome ToWire(DesktopCapabilityError error) => new()
    {
        Code = error.WireCode,
        Message = error.Message,
        Retryable = error.Retryable,
        MayHaveSideEffects = error.MayHaveSideEffects,
    };

    private static Proto.NavigateDisposition ToWire(Pudding.Contracts.Desktop.NavigateDisposition disposition) =>
        disposition switch
        {
            Pudding.Contracts.Desktop.NavigateDisposition.Accepted => Proto.NavigateDisposition.Accepted,
            Pudding.Contracts.Desktop.NavigateDisposition.Completed => Proto.NavigateDisposition.Completed,
            _ => Proto.NavigateDisposition.Unspecified,
        };

    private static Proto.JavascriptValueKind ToWire(Pudding.Contracts.Desktop.JavascriptValueKind kind) => kind switch
    {
        Pudding.Contracts.Desktop.JavascriptValueKind.Undefined => Proto.JavascriptValueKind.Undefined,
        Pudding.Contracts.Desktop.JavascriptValueKind.Null => Proto.JavascriptValueKind.Null,
        Pudding.Contracts.Desktop.JavascriptValueKind.Boolean => Proto.JavascriptValueKind.Boolean,
        Pudding.Contracts.Desktop.JavascriptValueKind.Number => Proto.JavascriptValueKind.Number,
        Pudding.Contracts.Desktop.JavascriptValueKind.String => Proto.JavascriptValueKind.String,
        Pudding.Contracts.Desktop.JavascriptValueKind.Json => Proto.JavascriptValueKind.Json,
        _ => Proto.JavascriptValueKind.Unspecified,
    };
}

/// <summary>
/// 就绪度线名真源已上移到 <see cref="DesktopPageReadinessWire"/>（契约层）：
/// 两端适配器共用同一张表，避免两份手写映射漂移。
/// </summary>
internal static class WireText
{    public static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
