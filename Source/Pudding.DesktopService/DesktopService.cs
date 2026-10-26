using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using PuddingBrowser.Abstractions;
using PuddingBrowser.Automation;

namespace Pudding.DesktopService;

/// <summary>
/// 桌面能力服务（计划 §6）：目标校验 → 准入 → UI 调度 → 竞态复检 → 终态映射。
///
/// 责任边界：
/// · 本组件<b>不</b>触碰任何 UI 类型：所有动作经 <see cref="IDesktopUiDispatcher"/> 调度到 UI 线程，
///   由 <see cref="IDesktopUiSurface"/> 实现方访问 WebView2/窗口/通知；
/// · 网络读写、序列化、重连不在这里发生（属于 Pudding.DesktopConnection）；
/// · 「入队后再校验」是本组件的核心职责：排队期间用户可能关闭页面、接管浏览器、暂停工具，
///   或页面版本已经推进 —— 这些竞态必须在拿到 UI 线程之后重新判定。
/// </summary>
public sealed class DesktopService : IDesktopCapabilityExecutor, IAsyncDisposable
{
    private readonly IDesktopUiDispatcher _dispatcher;
    private readonly IDesktopUiSurface _surface;
    private readonly DesktopServiceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private int _closed;

    public DesktopService(
        IDesktopUiDispatcher dispatcher,
        IDesktopUiSurface surface,
        DesktopTargetRegistry targets,
        DesktopServiceOptions options,
        BrowserAutomationAuthority authority,
        TimeProvider? timeProvider = null,
        // 当前连接世代的提供者（方案 §4.2）。**放在最后**：前面都是既有位置参数调用方。
        // 未提供 ⇒ 不启用 authority 准入闸门（见 ValidateAdmission）；生产装配必须提供，否则等于没有准入。
        Func<ConnectionGeneration>? connectionGeneration = null,
        BrowserTaskBindingService? taskBindings = null,
        // 共享权威的**关闭权**归属（ADR-097 D2）：浏览器工作区执行器拥有它；能力通道执行器只是借用
        // （Core 重启会停通道，若通道关掉共享权威，Bridge 路径会被永久关死且不可重开）。
        bool closesAuthorityOnDispose = true,
        // 迁移期**页级只读基线**（ADR-097 D4 / 审阅 F1/F3）：默认 Bridge 没有 grant 签发者，
        // 但「用户选中的 Agent 目标页」是真实授权来源。只对只读操作生效，且不签发租约；
        // 旧的 grant/租约准入仍是写操作的唯一路径。会话权威（I2）落地后删除本参。
        Func<BrowserCallerIdentity, DesktopPageTarget, bool>? readOnlyPageBaselineAdmission = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        Targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        Authority = authority ?? throw new ArgumentNullException(nameof(authority));
        Interaction = new DesktopInteractionState(Authority);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectionGeneration = connectionGeneration;
        _taskBindings = taskBindings;
        _closesAuthorityOnDispose = closesAuthorityOnDispose;
        _readOnlyPageBaselineAdmission = readOnlyPageBaselineAdmission;
    }

    /// <summary>窗口退出时触发（用于让宿主停止派发新命令）。</summary>
    public event Action? Closed;

    public DesktopTargetRegistry Targets { get; }

    /// <summary>
    /// 进程内**唯一**的控制权/授权事实源（设计方案 §3.1）。Desktop 侧不再各自维护
    /// takeover/paused 标志：<see cref="Interaction"/> 是它的读投影，Shell 的接管开关与
    /// 能力通道准入都写/读同一份状态。
    /// </summary>
    public BrowserAutomationAuthority Authority { get; }

    private readonly Func<ConnectionGeneration>? _connectionGeneration;

    private readonly BrowserTaskBindingService? _taskBindings;

    private readonly bool _closesAuthorityOnDispose;

    private readonly Func<BrowserCallerIdentity, DesktopPageTarget, bool>? _readOnlyPageBaselineAdmission;

    public DesktopInteractionState Interaction { get; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>窗口退出：拒绝新调用，并让排队中的 UI 调用以 UiUnavailable 结束（不悬挂）。</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        // 关闭自动化只在**拥有共享权威关闭权**的执行器上发生：能力通道执行器借用同一权威，
        // 停通道不等于卸载浏览器（ADR-097 D2）。
        if (_closesAuthorityOnDispose)
        {
            Authority.Close("desktop window closed");
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放：状态位已置位，无需再取消。
        }

        try
        {
            Closed?.Invoke();
        }
        catch
        {
            // 订阅者异常不得影响关闭。
        }
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    // ── 执行器接缝 ────────────────────────────────────────────────────────

    public async Task<DesktopCapabilityResponse> ExecuteAsync(
        DesktopCapabilityDescriptor capability,
        DesktopCapabilityRequest request,
        DesktopCallContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var response = await ExecuteCoreAsync(capability, request, context, cancellationToken)
            .ConfigureAwait(false);

        // **唯一的**「记录观察到的页面版本」落点。
        // 为什么必须有：准入按注册表里的版本判 `page_version_mismatch`（见 ValidateAsync），
        // 而 Core 钉的版本来自上一次能力结果。若这里不记录，注册表会永远停在页面创建时的版本，
        // 于是**第一个带版本的操作就会被判版本不符** —— 通道"握手成功却什么也做不了"。
        // 放在唯一出口而不是逐个分支里：新增能力时不会漏改
        //（这类「漏改某个聚合点」的缺陷本系列已踩过多次，探针抓到过两例）。
        RecordObservedPageVersion(response, request);

        return response;
    }

    /// <summary>
    /// 能力分支本体。**不要**在这里把结果直接返回给调用方：
    /// 观察到的页面版本由 <see cref="ExecuteAsync"/> 在唯一出口记录。
    /// </summary>
    private async Task<DesktopCapabilityResponse> ExecuteCoreAsync(
        DesktopCapabilityDescriptor capability,
        DesktopCapabilityRequest request,
        DesktopCallContext context,
        CancellationToken cancellationToken)
    {
        var descriptor = capability.Capability;

        switch (descriptor)
        {
            case DesktopCapability.WebViewNavigate:
            {
                if (request.Navigate is not { } navigate)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("navigate request payload is missing"));
                }

                var result = await RunOnUiAsync(
                    descriptor,
                    navigate.Target,
                    navigate.ExpectedPageVersion,
                    context,
                    token => _surface.NavigateAsync(context, navigate, token),
                    cancellationToken).ConfigureAwait(false);

                if (result.IsFailure)
                {
                    return DesktopCapabilityResponse.Failure(result.Error);
                }

                var navigationViolation = BrowserMutationPostcondition.Validate(new BrowserMutationOutcome(
                    BrowserAutomationOperation.Navigate,
                    request.ExpectedPageVersion,
                    result.Value.PageVersion,
                    BrowserMutationExpectation.DocumentNavigation));
                return navigationViolation is not null
                    ? DesktopCapabilityResponse.Failure(navigationViolation)
                    : DesktopCapabilityResponse.FromNavigate(result.Value);
            }

            case DesktopCapability.WebViewExecuteJavascript:
            {
                if (request.Javascript is not { } javascript)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("execute_javascript request payload is missing"));
                }

                var result = await RunOnUiAsync(
                    descriptor,
                    javascript.Target,
                    javascript.ExpectedPageVersion,
                    context,
                    token => _surface.ExecuteJavascriptAsync(context, javascript, token),
                    cancellationToken).ConfigureAwait(false);

                return result.IsSuccess
                    ? DesktopCapabilityResponse.FromJavascript(
                        DesktopCapabilityBudgets.Apply(result.Value, javascript.MaxResultBytes))
                    : DesktopCapabilityResponse.Failure(result.Error);
            }

            case DesktopCapability.ShellNotification:
            {
                if (request.Notification is not { } notification)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("show_notification request payload is missing"));
                }

                var result = await RunOnUiAsync(
                    descriptor,
                    target: null,
                    DesktopPageVersion.Unknown,
                    context,
                    token => _surface.ShowNotificationAsync(context, notification, token),
                    cancellationToken).ConfigureAwait(false);

                return result.IsSuccess
                    ? DesktopCapabilityResponse.FromNotification(result.Value)
                    : DesktopCapabilityResponse.Failure(result.Error);
            }

            case DesktopCapability.WebViewPageState:
            {
                if (request.PageState is not { } pageTarget)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("page_state request payload is missing"));
                }

                var result = await RunOnUiAsync(
                    descriptor,
                    pageTarget,
                    DesktopPageVersion.Unknown,
                    context,
                    token => _surface.GetPageStateAsync(context, pageTarget, token),
                    cancellationToken).ConfigureAwait(false);

                return result.IsSuccess
                    ? DesktopCapabilityResponse.FromPageState(result.Value)
                    : DesktopCapabilityResponse.Failure(result.Error);
            }

            case DesktopCapability.ShellDocumentPreview:
            {
                if (request.DocumentPreview is not { } preview)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("document_preview request payload is missing"));
                }

                // Shell 作用域：没有页面目标，信任级由服务侧调用方策略判定，不从目标推断。
                var result = await RunOnUiAsync(
                    descriptor,
                    target: null,
                    DesktopPageVersion.Unknown,
                    context,
                    token => _surface.OpenDocumentAsync(context, preview, token),
                    cancellationToken).ConfigureAwait(false);

                return result.IsSuccess
                    ? DesktopCapabilityResponse.FromDocumentPreview(result.Value)
                    : DesktopCapabilityResponse.Failure(result.Error);
            }

            case DesktopCapability.ShellPanelRender:
            {
                if (request.PanelRender is not { } panel)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("panel_render request payload is missing"));
                }

                // 同样是 Shell 作用域：没有页面目标，信任级由服务侧调用方策略判定。
                var result = await RunOnUiAsync(
                    descriptor,
                    target: null,
                    DesktopPageVersion.Unknown,
                    context,
                    token => _surface.RenderPanelAsync(context, panel, token),
                    cancellationToken).ConfigureAwait(false);

                return result.IsSuccess
                    ? DesktopCapabilityResponse.FromPanelRender(result.Value)
                    : DesktopCapabilityResponse.Failure(result.Error);
            }

            case DesktopCapability.ShellFilePicker:
            {
                if (request.FilePicker is not { } picker)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("shell.file_picker request payload is missing"));
                }

                // 与对话框共用同一个交互槽位：单窗口同时最多一个被问的用户交互。
                var pickerOwner = context.OperationId.Value;
                if (!Interaction.TryEnterInteraction(pickerOwner))
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.UiUnavailable("another interactive dialog is already active"));
                }

                try
                {
                    var result = await RunOnUiAsync(
                        descriptor,
                        target: null,
                        DesktopPageVersion.Unknown,
                        context,
                        token => _surface.RequestFilePickerAsync(context, picker, token),
                        cancellationToken).ConfigureAwait(false);

                    return result.IsSuccess
                        ? DesktopCapabilityResponse.FromFilePicker(result.Value)
                        : DesktopCapabilityResponse.Failure(result.Error);
                }
                finally
                {
                    Interaction.ExitInteraction(pickerOwner);
                }
            }

            case DesktopCapability.ShellDialog:
            {
                if (request.Dialog is not { } dialog)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("shell.dialog request payload is missing"));
                }

                // 单窗口同时最多一个对话框：第二个并发请求被拒绝，而不是排队
                // （排队会让"取消的是哪一个"不可判定）。终态一律释放槽位。
                var owner = context.OperationId.Value;
                if (!Interaction.TryEnterInteraction(owner))
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.UiUnavailable("another interactive dialog is already active"));
                }

                try
                {
                    var result = await RunOnUiAsync(
                        descriptor,
                        target: null,
                        DesktopPageVersion.Unknown,
                        context,
                        token => _surface.RequestDialogAsync(context, dialog, token),
                        cancellationToken).ConfigureAwait(false);

                    return result.IsSuccess
                        ? DesktopCapabilityResponse.FromDialog(result.Value)
                        : DesktopCapabilityResponse.Failure(result.Error);
                }
                finally
                {
                    Interaction.ExitInteraction(owner);
                }
            }

            case DesktopCapability.ShellClipboard:
            {
                if (request.Clipboard is not { } clipboard)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("shell.clipboard request payload is missing"));
                }

                var result = await RunOnUiAsync(
                    descriptor,
                    target: null,
                    DesktopPageVersion.Unknown,
                    context,
                    token => _surface.ReadClipboardAsync(context, clipboard, token),
                    cancellationToken).ConfigureAwait(false);

                return result.IsSuccess
                    ? DesktopCapabilityResponse.FromClipboard(
                        DesktopCapabilityBudgets.Apply(result.Value, clipboard.MaxCharacters))
                    : DesktopCapabilityResponse.Failure(result.Error);
            }

            case DesktopCapability.BrowserTabs:
            {
                if (request.Tabs is not { } tabs)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("browser.tabs request payload is missing"));
                }

                // §5.3：request-aware —— new/activate/close 的**最小准入不同**；未登记动作**明确拒绝**
                // （既不得统一按"最严的 TabClose"蒙过去，也不得放行）。
                if (!DesktopCapabilityOperations.TryGetBrowserOperation(request, out var tabsOperation))
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest(
                            $"browser.tabs action '{tabs.Action}' is not registered"));
                }

                var result = await RunOnUiAsync(
                    descriptor,
                    tabs.Target,
                    tabs.ExpectedPageVersion,
                    context,
                    token => _surface.TabsAsync(context, tabs, token),
                    cancellationToken,
                    operationOverride: tabsOperation).ConfigureAwait(false);

                if (result.IsFailure)
                {
                    return DesktopCapabilityResponse.Failure(result.Error);
                }

                // 标签页：不拿「另一页的版本推进」当证据（Activate 切换焦点不会推进版本），
                // 改为校验动作自身的事实 —— close 要求确实关掉了，activate/new 要求回带活版本。
                var tabsViolation = BrowserMutationPostcondition.ValidateTabs(
                    tabs.Action, result.Value.TabClosed, result.Value.Page.Version);
                return tabsViolation is not null
                    ? DesktopCapabilityResponse.Failure(tabsViolation)
                    : DesktopCapabilityResponse.FromTabs(result.Value);
            }

            case DesktopCapability.BrowserContextCreate:
            {
                if (request.ContextCreate is not { } createContext)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("browser.context.create request payload is missing"));
                }

                var result = await RunOnUiAsync(
                    descriptor,
                    target: null,
                    DesktopPageVersion.Unknown,
                    context,
                    token => _surface.CreateContextAsync(context, createContext, token),
                    cancellationToken).ConfigureAwait(false);

                return result.IsSuccess
                    ? DesktopCapabilityResponse.FromContextCreated(result.Value)
                    : DesktopCapabilityResponse.Failure(result.Error);
            }

            case DesktopCapability.BrowserBindingApply:
            {
                if (request.BindingApply is not { } apply)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("browser.binding.apply request payload is missing"));
                }
                if (_taskBindings is null)
                {
                    return DesktopCapabilityResponse.Failure(DesktopCapabilityError.InvalidRequest(
                        "the browser task-binding control plane is not wired on this desktop"));
                }
                var acceptance = _taskBindings.Accept(apply.Binding);
                return DesktopCapabilityResponse.FromBindingAck(new BrowserBindingAck(
                    acceptance.Accepted, acceptance.GrantId, acceptance.Epoch, acceptance.Reason));
            }
            case DesktopCapability.BrowserBindingRevoke:
            {
                if (request.BindingRevoke is not { } revoke)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("browser.binding.revoke request payload is missing"));
                }
                if (_taskBindings is null)
                {
                    return DesktopCapabilityResponse.Failure(DesktopCapabilityError.InvalidRequest(
                        "the browser task-binding control plane is not wired on this desktop"));
                }
                var revoked = _taskBindings.Revoke(revoke.Target, revoke.Task);
                return DesktopCapabilityResponse.FromBindingAck(new BrowserBindingAck(
                    revoked, GrantId: null, Epoch: default,
                    Reason: revoked ? null : "no binding is active for this task and target"));
            }
            case DesktopCapability.BrowserContextClose:
            {
                if (request.ContextClose is not { } closeContext)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("browser.context.close request payload is missing"));
                }

                var result = await RunOnUiAsync(
                    descriptor,
                    target: null,
                    DesktopPageVersion.Unknown,
                    context,
                    token => _surface.CloseContextAsync(context, closeContext, token),
                    cancellationToken).ConfigureAwait(false);

                return result.IsSuccess
                    ? DesktopCapabilityResponse.FromContextClosed(result.Value)
                    : DesktopCapabilityResponse.Failure(result.Error);
            }

            case DesktopCapability.BrowserContexts:
            {
                if (!request.Contexts)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("browser.contexts request payload is missing"));
                }

                var result = await RunOnUiAsync(
                    descriptor,
                    target: null,
                    DesktopPageVersion.Unknown,
                    context,
                    token => _surface.GetContextsAsync(context, token),
                    cancellationToken).ConfigureAwait(false);

                return result.IsSuccess
                    ? DesktopCapabilityResponse.FromContexts(result.Value)
                    : DesktopCapabilityResponse.Failure(result.Error);
            }

            case DesktopCapability.BrowserWaitFor:
            {
                if (request.WaitFor is not { } waitFor)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("browser.wait_for request payload is missing"));
                }

                var result = await RunOnUiAsync(
                    descriptor,
                    waitFor.Target,
                    waitFor.ExpectedPageVersion,
                    context,
                    token => _surface.WaitForAsync(context, waitFor, token),
                    cancellationToken).ConfigureAwait(false);

                return result.IsSuccess
                    ? DesktopCapabilityResponse.FromWait(result.Value)
                    : DesktopCapabilityResponse.Failure(result.Error);
            }

            case DesktopCapability.BrowserInteract:
            {
                if (request.Interact is not { } interact)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("browser.interact request payload is missing"));
                }

                var result = await RunOnUiAsync(
                    descriptor,
                    interact.Target,
                    interact.ExpectedPageVersion,
                    context,
                    token => _surface.InteractAsync(context, interact, token),
                    cancellationToken).ConfigureAwait(false);

                if (result.IsFailure)
                {
                    return DesktopCapabilityResponse.Failure(result.Error);
                }

                // 交互：**不**要求版本推进（fill 只改 value、click 可能只开菜单或触发 SPA 更新）。
                // 只有调用方显式声明的文档导航后置条件未满足时才失败，且如实报「结果不明」。
                var interactionViolation = BrowserMutationPostcondition.Validate(new BrowserMutationOutcome(
                    BrowserAutomationOperation.Interact,
                    interact.ExpectedPageVersion,
                    result.Value.Page.Version));
                return interactionViolation is not null
                    ? DesktopCapabilityResponse.Failure(interactionViolation)
                    : DesktopCapabilityResponse.FromInteract(result.Value);
            }

            case DesktopCapability.BrowserLocate:
            {
                if (request.Locate is not { } locate)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("browser.locate request payload is missing"));
                }

                var result = await RunOnUiAsync(
                    descriptor,
                    locate.Target,
                    locate.ExpectedPageVersion,
                    context,
                    token => _surface.LocateAsync(context, locate, token),
                    cancellationToken).ConfigureAwait(false);

                return result.IsSuccess
                    ? DesktopCapabilityResponse.FromLocate(
                        DesktopCapabilityBudgets.Apply(result.Value, locate.MaxResults))
                    : DesktopCapabilityResponse.Failure(result.Error);
            }

        case DesktopCapability.BrowserScreenshot:
        {
            if (request.Screenshot is not { } screenshot)
            {
                return DesktopCapabilityResponse.Failure(
                    DesktopCapabilityError.InvalidRequest("browser.screenshot request payload is missing"));
            }

            var screenshotResult = await RunOnUiAsync(
                descriptor,
                screenshot.Target,
                screenshot.ExpectedPageVersion,
                context,
                token => _surface.ScreenshotAsync(context, screenshot, token),
                cancellationToken).ConfigureAwait(false);

            return screenshotResult.IsSuccess
                ? DesktopCapabilityResponse.FromScreenshot(screenshotResult.Value)
                : DesktopCapabilityResponse.Failure(screenshotResult.Error);
        }

            case DesktopCapability.BrowserSnapshot:
            {
                if (request.Snapshot is not { } snapshot)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("browser.snapshot request payload is missing"));
                }

                var result = await RunOnUiAsync(
                    descriptor,
                    snapshot.Target,
                    snapshot.ExpectedPageVersion,
                    context,
                    token => _surface.SnapshotAsync(context, snapshot, token),
                    cancellationToken).ConfigureAwait(false);

                return result.IsSuccess
                    ? DesktopCapabilityResponse.FromSnapshot(
                        DesktopCapabilityBudgets.Apply(result.Value, snapshot.Options))
                    : DesktopCapabilityResponse.Failure(result.Error);
            }

            case DesktopCapability.ShellStatus:
            {
                if (!request.ShellStatus)
                {
                    return DesktopCapabilityResponse.Failure(
                        DesktopCapabilityError.InvalidRequest("shell_status request payload is missing"));
                }

                var reported = await RunOnUiAsync(
                    descriptor,
                    target: null,
                    DesktopPageVersion.Unknown,
                    context,
                    token => _surface.GetShellStatusAsync(context, token),
                    cancellationToken).ConfigureAwait(false);

                if (reported.IsFailure)
                {
                    return DesktopCapabilityResponse.Failure(reported.Error);
                }

                // 自动化状态与打开页面数由本服务依自身权威状态补齐：
                // surface 只报告「只有它知道」的窗口/托盘部分，避免两处状态互相漂移。
                var enriched = new DesktopShellStatus(
                    reported.Value.WindowState,
                    reported.Value.TrayVisible,
                    MapAutomation(Interaction),
                    Targets.OpenPageCount);

                return DesktopCapabilityResponse.FromShellStatus(enriched);
            }

            default:
                // 目录里已登记但本切片尚无命令 payload 的能力（dialog/picker/clipboard 属切片 E）。
                return DesktopCapabilityResponse.Failure(
                    DesktopCapabilityError.UnsupportedCapability(capability.Name));
        }
    }

    /// <summary>交互状态 → 线上面状态（暂停与用户接管都表示「不可自动化」）。</summary>
    private static DesktopAutomationState MapAutomation(DesktopInteractionState state) => state switch
    {
        _ when state.IsUserTakeover => DesktopAutomationState.UserTakeover,
        _ when state.IsPaused => DesktopAutomationState.Paused,
        _ => DesktopAutomationState.Free,
    };

    /// <summary>
    /// 把成功结果里携带的页面版本登记进目标注册表——它是准入比较 `expectedPageVersion` 的**基准**。
    ///
    /// 只有「带版本含义」的能力才记录：<c>execute_javascript</c> 的返回值里没有页面版本，
    /// 而按设计脚本**不推进版本**，因此既不需要也不能凭空推一个。
    /// 就绪度只在结果确实携带了页面状态时更新（<c>navigate</c>/<c>snapshot</c>/<c>locate</c> 不带就绪度，
    /// 此时保留原值而不猜）。
    ///
    /// 未登记或已关闭的页面**不补登记**：注册表的存在性只由 Shell 的页面生命周期说了算
    /// （本服务凭空造目标会让准入对一个并不存在的页面放行）。
    /// </summary>
    private void RecordObservedPageVersion(DesktopCapabilityResponse response, DesktopCapabilityRequest request)
    {
        if (response.IsFailure)
        {
            return;
        }

        // 清单（contexts）是「一次调用里所有页面的实时版本」。若不逐页回写，注册表会停在
        // 建页时的版本：agent 从清单读到 v3、准入却拿注册表的 v2 去比，两处互相否决（双源）。
        // 2026-10-26 实测就是被这个卡住的：截图强制要求版本号，且没有绕法。
        if (response.Contexts is { } inventory)
        {
            foreach (var page in inventory.Contexts.SelectMany(context => context.Pages))
            {
                if (Targets.Resolve(page.Target) is not { } known)
                {
                    continue;
                }

                Targets.UpdatePage(
                    page.Target,
                    page.Version,
                    page.IsLoading ? DesktopPageReadiness.Loading : known.Readiness);
            }

            return;
        }

        DesktopPageTarget? target;
        DesktopPageVersion version;
        DesktopPageReadiness? readiness;

        if (response.Navigate is { } navigate)
        {
            // NavigateResult 只带处置结果、URL 与版本（**不带目标**）⇒ 目标取自请求；
            // 目标与请求一致已由准入保证（它就是在请求的目标上校验版本与可信级别的）。
            (target, version, readiness) = (request.Navigate?.Target, navigate.PageVersion, null);
        }
        else if (response.PageState is { } pageState)
        {
            (target, version, readiness) = (pageState.Target, pageState.Version, pageState.Readiness);
        }
        else if (response.Snapshot is { } snapshot)
        {
            (target, version, readiness) = (snapshot.Target, snapshot.PageVersion, null);
        }
        else if (response.Locate is { } locate)
        {
            (target, version, readiness) = (locate.Target, locate.PageVersion, null);
        }
        else if (response.Interact is { } interact)
        {
            (target, version, readiness) = (interact.Target, interact.Page.Version, interact.Page.Readiness);
        }
        else if (response.Wait is { } wait)
        {
            (target, version, readiness) = (wait.Target, wait.Page.Version, wait.Page.Readiness);
        }
        else if (response.Tabs is { } tabs)
        {
            (target, version, readiness) = (tabs.Target, tabs.Page.Version, tabs.Page.Readiness);
        }
        else
        {
            // contexts / clipboard / dialog / file_picker / notification / shell_status：
            // 它们不对应单一页面目标，没有可登记的版本。
            return;
        }

        if (target is null || !version.IsKnown)
        {
            return;
        }

        if (Targets.Resolve(target) is not { } state)
        {
            return;
        }

        // UpdatePage 自身拒绝版本回退：迟到的旧结果不会把版本拉回去。
        Targets.UpdatePage(target, version, readiness ?? state.Readiness);
    }

    // ── 只读直连 API（UI 侧门面复用；本切片不经 wire 命令） ────────────────

    /// <summary>
    /// 读取页面状态：同样经过目标校验、可信级别判定与 UI 调度，供 Desktop 侧门面
    /// （<c>IPuddingDesktopWebViewApi</c> 的实现）直接调用。
    /// </summary>
    public Task<CapabilityResult<DesktopPageState>> GetPageStateAsync(
        DesktopCallContext context,
        DesktopPageTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);

        return RunOnUiAsync(
            DesktopCapability.WebViewPageState,
            target,
            DesktopPageVersion.Unknown,
            context,
            token => _surface.GetPageStateAsync(context, target, token),
            cancellationToken);
    }

    // ── 主管线 ────────────────────────────────────────────────────────────

    private async Task<CapabilityResult<TResult>> RunOnUiAsync<TResult>(
        DesktopCapability capability,
        DesktopPageTarget? target,
        DesktopPageVersion expectedPageVersion,
        DesktopCallContext context,
        Func<CancellationToken, Task<CapabilityResult<TResult>>> action,
        CancellationToken cancellationToken,
        // 方案 §5.3：request-aware 操作覆盖（如 browser.tabs 的具体动作）。放在末尾以保持既有调用点不变。
        BrowserAutomationOperation? operationOverride = null)
    {
        var admission = ValidateAdmission(
            capability, target, expectedPageVersion, context, out _, out var lease,
            operationOverride: operationOverride);

        // 租约一旦在准入时签发，就必须在**所有**退出路径上释放（外部复核 P0：取消与版本拒绝都会泄漏租约）。
        // 因此把"准入失败"和"入队前已取消"也放进同一个 try，由 finally 统一释放原租约。
        try
        {
            if (admission is not null)
            {
                return CapabilityResult<TResult>.Failure(admission);
            }

            // 入队前就取消/关闭的操作绝不进队列（不能依赖调度器替我们检查取消）。
            if (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
            {
                return CapabilityResult<TResult>.Failure(MapCancellation(capability, context, started: false));
            }

            // 交互类能力（对话框/Picker）的单窗口互斥随切片 E 一起落地：届时才有 payload 能端到端验证。
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);

            // 触碰页面前的复检由 `ExecuteOnUiAsync` 负责：它在 UI 线程内、surface 调用前**再跑一次完整准入**
            // （更贴近真实触碰时刻的那个窗口）。这里**不再**另加一份 Revalidate —— 同一判据两处实现就是
            // 设计 §1.1 说的"第二份策略来源"，而且实测证明删掉它也拦得住（round-232 取红）。
            return await _dispatcher.InvokeAsync(
                token => ExecuteOnUiAsync(
                    capability, target, expectedPageVersion, context, action, lease, token, operationOverride),
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 调度器在执行回调前就取消了：动作从未开始。
            return CapabilityResult<TResult>.Failure(MapCancellation(capability, context, started: false));
        }
        catch (ObjectDisposedException)
        {
            return CapabilityResult<TResult>.Failure(DesktopCapabilityError.UiUnavailable("ui dispatcher is disposed"));
        }
        catch (InvalidOperationException ex)
        {
            // DispatcherQueue 拒绝入队：必须让调用方拿到明确终态，而不是悬挂。
            return CapabilityResult<TResult>.Failure(
                DesktopCapabilityError.UiUnavailable($"ui queue rejected the operation ({ex.GetType().Name})"));
        }
        catch (Exception ex)
        {
            return CapabilityResult<TResult>.Failure(
                DesktopCapabilityError.Internal($"ui dispatch failed ({ex.GetType().Name})"));
        }
        finally
        {
            // 变更类操作在准入时拿到租约：**必须**在操作结束时释放（幂等）。
            // 否则租约一直留在权威里，会把后续写操作（包括用户自己的）挡住 —— 即"租约泄漏"。
            if (lease is not null)
            {
                Authority.ReleaseLease(lease.LeaseId, "operation completed");
            }
        }
    }

    /// <summary>在 UI 线程上执行：先复检竞态，再调用 surface，最后把异常映射成领域错误。</summary>
    private async Task<CapabilityResult<TResult>> ExecuteOnUiAsync<TResult>(
        DesktopCapability capability,
        DesktopPageTarget? target,
        DesktopPageVersion expectedPageVersion,
        DesktopCallContext context,
        Func<CancellationToken, Task<CapabilityResult<TResult>>> action,
        BrowserAutomationLease? admittedLease,
        CancellationToken token,
        // 复检必须与准入用**同一个动作**：否则能力级回退映射会重新要求页面目标，
        // 把准入已经放行的动作（如 browser.tabs 的 new）在 UI 线程上再拒一次。
        BrowserAutomationOperation? operationOverride = null)
    {
        // 排队期间的竞态（外部复核 P0）：接管→恢复、断连、撤销、关闭都会推进控制/授权世代。
        // 按"当前状态"重跑准入**看不出**这个中间过程（接管已被撤销 ⇒ 又会放行），所以必须比对
        // **准入当时**的世代：一旦推进，原许可即失效，旧排队操作不得复活。
        if (admittedLease is not null)
        {
            var current = Authority.Capture();
            if (current.ControlEpoch.Value != admittedLease.ControlEpoch.Value
                || current.GrantEpoch.Value != admittedLease.GrantEpoch.Value)
            {
                return CapabilityResult<TResult>.Failure(
                    DesktopCapabilityError.Unauthorized(
                        "the admitted control or grant epoch is stale (takeover/resume/disconnect/revoke since admission)"));
            }
        }

        // 纯复检（不再次 Admit）：窗口/能力/目标/可信级别/页面版本仍在同一 UI 序列内可判。
        var recheck = ValidateAdmission(
            capability, target, expectedPageVersion, context, out _, out _, admitAuthority: false,
            operationOverride: operationOverride);
        if (recheck is not null)
        {
            return CapabilityResult<TResult>.Failure(recheck);
        }

        // 排队期间被取消：必须在触碰 UI 之前结束（可能已经排了很久）。
        if (token.IsCancellationRequested)
        {
            return CapabilityResult<TResult>.Failure(MapCancellation(capability, context, started: false));
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var remaining = context.DeadlineUtc - Now;
        if (remaining <= TimeSpan.Zero)
        {
            return CapabilityResult<TResult>.Failure(DesktopCapabilityError.DeadlineExceeded(mayHaveSideEffects: false));
        }

        // 用显式计时器记录「是不是期限到点」：只按墙钟比较会在定时器抖动时把期限误判为取消。
        var deadlineFired = 0;
        using var deadlineTimer = _timeProvider.CreateTimer(
            _ =>
            {
                Interlocked.Exchange(ref deadlineFired, 1);
                try
                {
                    operation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 操作已经结束。
                }
            },
            null,
            remaining,
            Timeout.InfiniteTimeSpan);

        try
        {
            return await action(operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (Volatile.Read(ref deadlineFired) != 0)
            {
                var sideEffects = DesktopCapabilities.TryGet(capability, out var descriptor)
                    && descriptor.Traits.HasFlag(DesktopCapabilityTraits.HasSideEffects);
                return CapabilityResult<TResult>.Failure(
                    DesktopCapabilityError.DeadlineExceeded(sideEffects));
            }

            // 已经进入 surface：可能已产生副作用（由能力的 Traits 决定）。
            return CapabilityResult<TResult>.Failure(MapCancellation(capability, context, started: true));
        }
        catch (BrowserOperationException ex)
        {
            // 领域异常自带**稳定 code**（BrowserOperationException.Code 的契约就是「让上层按码处理，
            // 不要解析异常文本」）。若像普通异常一样压成 internal，调用方只会看到
            // "ui surface failed (BrowserOperationException)"，「多匹配 / 未命中 / 被遮挡 / 不支持」
            // 这些本可自救的语义会全部丢失（缺陷登记：浏览器工具缺口清单 G6/G31）。
            return CapabilityResult<TResult>.Failure(MapBrowserOperationFailure(ex));
        }
        catch (Exception ex)
        {
            // 不把 surface 的异常消息透出去（可能含页面数据）；只保留异常类型。
            return CapabilityResult<TResult>.Failure(
                DesktopCapabilityError.Internal($"ui surface failed ({ex.GetType().Name})"));
        }
    }

    /// <summary>
    /// <see cref="BrowserOperationException"/> → 能力错误码：把**稳定 code** 翻成语义枚举，消息沿用异常自身的文本
    /// （<see cref="DesktopCapabilityError"/> 构造时会再截断长度并剔除控制字符）。
    /// 未登记的码**不外泄消息**、只回码本身——既保住可诊断性，也不把页面数据带出去。
    /// </summary>
    private static DesktopCapabilityError MapBrowserOperationFailure(BrowserOperationException ex)
    {
        // 把**稳定 code 原文**带进消息：能力层的错误码是枚举，若只给 invalid_request / invalid_target，
        // 工具侧那张按「browser_* 码」写的修复提示表就永远命中不了（缺口 G6）。
        var detail = $"{ex.Code}: {ex.Message}";

        return ex.Code switch
        {
        // 歧义：绝不静默选第一个，也绝不重试（同样的定位一定再次歧义）。
            "browser_locator_ambiguous" => new(DesktopCapabilityErrorCode.InvalidRequest, detail, retryable: false),
        "browser_invalid_arguments" => new(DesktopCapabilityErrorCode.InvalidRequest, detail, retryable: false),
        // 零命中可能只是页面还没渲染完：允许等一等再试。
        "browser_element_not_found" => new(DesktopCapabilityErrorCode.InvalidTarget, detail, retryable: true),
        // 可见性/视口/遮挡/禁用：同一次请求再发一次不会变好 ⇒ 不可自动重试。
        "browser_element_not_visible"
            or "browser_element_not_in_viewport"
            or "browser_element_obstructed"
            or "browser_element_disabled"
            => new(DesktopCapabilityErrorCode.InvalidTarget, detail, retryable: false),
        "browser_operation_not_supported"
            => new(DesktopCapabilityErrorCode.UnsupportedCapability, detail, retryable: false),
        "stale_element_reference" or "page_version_mismatch"
            => new(DesktopCapabilityErrorCode.PageVersionMismatch, detail, retryable: false),
        _ => DesktopCapabilityError.Internal($"browser operation failed ({ex.Code})"),
        };
    }

    /// <summary>
    /// 准入与竞态复检的<b>唯一实现</b>：窗口可用性 → 能力启用 → 目标存在 → 可信级别 → 自动化状态 → 页面版本。
    /// 在入队前与拿到 UI 线程后各调用一次。
    /// </summary>
    private DesktopCapabilityError? ValidateAdmission(
        DesktopCapability capability,
        DesktopPageTarget? target,
        DesktopPageVersion expectedPageVersion,
        DesktopCallContext context,
        out DesktopTargetState? pageState,
        out BrowserAutomationLease? lease,
        // 方案 §3.2：UI 回调里**只做纯复检**，不得再次 Admit（第二次 Admit 会另签一份租约 ⇒ 泄漏，
        // 且"接管→恢复"这类中间变化按当前状态重跑是看不出来的，改由准入世代比对负责）。
        bool admitAuthority = true,
        // 方案 §5.3：**request-aware** 的操作覆盖（同一能力的不同动作最小准入不同，如 tabs 的 new/activate/close）。
        // 为空时回退到能力级映射（其它能力没有"同能力多动作"的分裂）。
        BrowserAutomationOperation? operationOverride = null)
    {
        pageState = null;
        lease = null;

        if (IsClosed)
        {
            return DesktopCapabilityError.UiUnavailable("window is closed");
        }

        if (!_options.AllowedCapabilities.HasFlag(capability))
        {
            return DesktopCapabilityError.UnsupportedCapability(DesktopCapabilities.NameOf(capability));
        }

        // 方案 §4.2：浏览器能力在 Desktop 侧**必须**过 authority 准入（身份 + grant + 控制态/租约）。
        // 判据不在这里重写：能力→操作走唯一映射（DesktopCapabilityOperations），身份走契约唯一规则
        // （BrowserCallerRequirement，经 DesktopCallContext.TryGetBrowserCaller），裁决在唯一 Authority。
        // 未注入世代提供者时不启用这道闸门（见构造器说明）—— 生产装配必须注入，否则等于没有准入。
        // request-aware：优先用调用方给的覆盖值（如 tabs 的具体动作），否则回退能力级映射。
        BrowserAutomationOperation? authorizationOperation = operationOverride;
        if (authorizationOperation is null
            && DesktopCapabilityOperations.TryGetBrowserOperation(capability, out var mappedOperation))
        {
            authorizationOperation = mappedOperation;
        }

        // I2-c D2：该调用已由 Bridge 派发器用**会话权威**完成准入/复检（宿主内部标记，不上线缆）。
        // 执行器不得再用旧 authority 的 grant/lease 重复裁决（否则写串行化会有第二套来源）；
        // 页面存在/信任级/自动化状态/版本检查仍在本方法后续步骤执行。
        // 提到闸门之外：页级「必须由用户指定为目标页」也属**旧授权模型**（审计 2026-10-08 结论 #2），
        // 会话权威已用目标绑定（v1 隐式绑定）表达了同一事实。两处都判会让 V1「浏览器只服务 Agent」成为空话：
        // 隐式绑定只写会话，**不**写桌面注册表的 IsAgentTarget 标志。
        var sessionAdmitted = !string.IsNullOrWhiteSpace(context.SessionPermitMarker);

        // 局部函数（用户裁定 2026-10-25）：**AgentAuthorized 上下文里的页面默认被授权**。
        //
        // 桌面工具面板里的浏览器本来就是给 Agent 用的，要求用户先点一次「设为 Agent 目标」
        // 属于纯摩擦，不是安全增益：真正该守的边界是「哪些上下文可信」，而不是「哪一页被点过」。
        // 因此改为按需签发一张短期页级授权；工作台 / 第三方页面（非 AgentAuthorized）仍走原判据，
        // 默认化不得外溢到用户自己的浏览区。
        void EnsureDefaultAgentPageGrant(BrowserCallerIdentity caller, DesktopPageTarget? page)
        {
            if (_taskBindings is null || page is null)
            {
                return;
            }

            // 默认页级授权只适用于**主 Agent**，不适用于子代理：
            // 子代理的权限必须从父任务派生（§3.2 子集校验）并受级联释放约束。
            // 若在这里给子代理自动补发，子任务结束后它会「自己把自己授权回来」，
            // 这正是 BrowserSubAgentCascadeReleaseTests.EndingTheSubTaskReleasesItsPermission 锁住的不变量。
            if (caller.Kind != BrowserCallerKind.Agent)
            {
                return;
            }

            // 信任级取自页面注册表本身（未登记 ⇒ 不签发）。这里不能用 TrustFor：
            // 那是 BrowserTargetRegistry 的方法，而本类持有的注册表类型不同。
            if (Targets.Resolve(page) is not { } agentPage
                || agentPage.Trust != DesktopContextTrust.AgentAuthorized)
            {
                return;
            }

            // 去重判据必须与消费端（BrowserAutomationAuthority.Admit 的 IsSameActor）**逐字一致**：
            // 主 Agent 的 TaskId 是本次 runId，每轮都会变；用 IsSameTaskScope（只看 CallerId）去重，
            // 会把上一轮的授权误当成「已覆盖」，随后又被 Admit 判为「发给了别的行动者」而拒绝 ——
            // 表现为写操作在授权到期前稳定被拒、读操作照常可用。详见 BrowserDefaultPageGrantPolicy。
            if (!BrowserDefaultPageGrantPolicy.ShouldIssue(
                Authority.ActiveGrants,
                page,
                caller,
                DateTimeOffset.UtcNow))
            {
                return; // 已有同一行动者、未过期的有效授权，不重复签发
            }

            // 签发失败不在这里抛：真正的裁决仍在 Authority.Admit，失败会把原因如实回给调用方。
            _ = _taskBindings.Accept(new BrowserTaskBinding(
                caller,
                page,
                BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
                TimeSpan.FromMinutes(5),
                "desktop:agent-default-page"));
        }

        // 局部函数（用户裁定 2026-10-25）：管理面（新建/关闭标签、建关 context）同样默认授权。
        //
        // 原因：这些操作发生时**还没有页面可以授权**（新建标签更是要创建页面），所以
        // EnsureDefaultAgentPageGrant 的页级判据永远返回（page 为 null）；而消费端
        // BrowserAutomationAuthority.Admit 对 Management 类操作只认「任务级管理授权」，
        // 而生产代码从未签发过它 —— 于是形成一道永远打不开的死门：
        // 面板里没有页面时（pageCount=0），Agent 既无页可驱动，也无法自己开页。
        //
        // 边界：仍然只给**主 Agent**，不给子代理；子代理必须由父任务先持有（判在签发器里），
        // 避免子任务自行开页绕过级联释放。
        void EnsureDefaultAgentManagementGrant(BrowserCallerIdentity caller, BrowserAutomationOperation operation)
        {
            if (_taskBindings is null)
            {
                return;
            }

            if (BrowserOperationPolicy.RequiredGrantKind(operation) != BrowserGrantKind.Management)
            {
                return;
            }

            if (caller.Kind != BrowserCallerKind.Agent)
            {
                return;
            }

            // 去重判据必须与消费端 HasManagementGrant 一致（任务作用域，不含 runId）。
            if (!BrowserDefaultManagementGrantPolicy.ShouldIssue(
                Authority.ActiveManagementGrants,
                caller,
                DateTimeOffset.UtcNow))
            {
                return; // 已有同一任务作用域的有效管理授权
            }

            // 短期（一分钟）：足够完成本次 tabs new / context create，又不留下长期管理能力。
            // 签发失败不在这里抛：真正的裁决仍在 Authority.Admit。
            _ = _taskBindings.AcceptManagement(caller, TimeSpan.FromMinutes(1));
        }

        if (admitAuthority
            && _connectionGeneration is { } generation
            && authorizationOperation is { } automationOperation)
        {
            if (!context.TryGetBrowserCaller(out var browserCaller, out var callerReason))
            {
                return DesktopCapabilityError.Unauthorized(callerReason);
            }

            if (!sessionAdmitted)
            {
                EnsureDefaultAgentPageGrant(browserCaller, target);
                EnsureDefaultAgentManagementGrant(browserCaller, automationOperation);

                var decision = Authority.Admit(new BrowserAutomationRequest(
                    automationOperation,
                    browserCaller,
                    context.OperationId,
                    generation(),
                    target));

                lease = decision.Lease;

                if (!decision.IsAllowed)
                {
                    // 迁移期唯一例外：只读操作 + 用户选中的 Agent 目标页（页级基线，不签发租约）。
                    // 写操作**绝不**走这条路：它们仍必须有 grant/租约（I2 会话权威落地后统一替换）。
                    if (_readOnlyPageBaselineAdmission is not null
                        && target is not null
                        && BrowserOperationPolicy.IsReadOnly(automationOperation)
                        && _readOnlyPageBaselineAdmission(browserCaller, target))
                    {
                        lease = null;
                    }
                    else
                    {
                        return decision.Error
                            ?? DesktopCapabilityError.Unauthorized(decision.Denial.ToString());
                    }
                }
            }
        }

        // 无页面目标的能力有三种来源，可信级别**来源不同**：
        // · 浏览器上下文作用域（Agent 工具层发起、无页面目标）⇒ BrowserContextCallerTrust；
        // · 展示类（文档预览 / 面板渲染：Agent 让 Shell 呈现内容）⇒ PresentationCallerTrust；
        // · Shell 自身能力（对话框 / Picker / 剪贴板）⇒ ShellCallerTrust（默认 Untrusted，fail closed）。
        // 有页面目标的能力稍后会被 pageState.Trust 覆盖。
        // §5.3 request-aware：能力级 trait 取的是该能力**最严动作**的并集，不能当作每个动作的要求。
        // 反例（2026-10-18 实测死锁）：browser.tabs 带 RequiresPageTarget，而它的 new 动作按
        // BrowserOperationPolicy 属 Management 类、**不需要**页面目标 ⇒ 空上下文（零页面）时
        // 「必须先有页面才能新建页面」，Agent 永远拿不到第一个页面。
        // 因此：动作级策略说不需页面目标时，本次调用按**上下文作用域**取信任级，并跳过目标解析。
        var operationNeedsPageTarget = authorizationOperation is not { } scopedOperation
            || BrowserOperationPolicy.RequiresPageTarget(scopedOperation);
        var requiresPageTarget =
            DesktopCapabilityPolicy.RequiresPageTarget(capability) && operationNeedsPageTarget;
        var contextScoped =
            DesktopCapabilityPolicy.IsBrowserContextScoped(capability) || !operationNeedsPageTarget;

        var trust = contextScoped
            ? _options.BrowserContextCallerTrust
            : DesktopCapabilityPolicy.IsPresentationScoped(capability)
                ? _options.PresentationCallerTrust
                : _options.ShellCallerTrust;
        if (requiresPageTarget)
        {
            if (target is null)
            {
                return DesktopCapabilityError.InvalidTarget(
                    $"{DesktopCapabilities.NameOf(capability)} requires an explicit page target");
            }

            pageState = Targets.Resolve(target);
            if (pageState is null)
            {
                return DesktopCapabilityError.InvalidTarget(
                    $"page target '{target.Key}' is not registered or was closed");
            }

            trust = pageState.Trust;
        }

        if (!DesktopCapabilityPolicy.IsAllowedForTrust(capability, trust))
        {
            return DesktopCapabilityError.Unauthorized(
                $"{DesktopCapabilities.NameOf(capability)} is not allowed for {trust} targets");
        }

        // page 级闸门（审计 2026-10-08 结论 #2 / 设计 §3.2）：可信级别是 **context 级**，
        // 不能把整个 AgentAuthorized 上下文当作「里面所有页面都已获授权」——
        // 否则同上下文的非目标页同样能通过准入，UI 上的「Agent 目标」就只是暗示。
        if (!sessionAdmitted
            && DesktopCapabilityPolicy.RequiresAgentTarget(capability)
            && pageState is { } gated
            && !gated.IsAgentTarget)
        {
            return DesktopCapabilityError.Unauthorized(
                $"{DesktopCapabilities.NameOf(capability)} requires the page to be the designated agent target;"
                + $" '{gated.Target.Key}' is in an {trust} context but is not the agent target");
        }

        var automation = ValidateAutomation(capability);
        if (automation is not null)
        {
            return automation;
        }

        if (expectedPageVersion.IsKnown && pageState is { } state && expectedPageVersion.Value != state.Version.Value)
        {
            return new DesktopCapabilityError(
                DesktopCapabilityErrorCode.PageVersionMismatch,
                $"expected page version {expectedPageVersion.Value} but the page is at {state.Version.Value}; re-acquire state");
        }

        return null;
    }

    private DesktopCapabilityError? ValidateAutomation(DesktopCapability capability)
    {
        if (!DesktopCapabilities.TryGet(capability, out var descriptor)
            || !descriptor.Traits.HasFlag(DesktopCapabilityTraits.Mutating))
        {
            // 只读能力在暂停/接管期间仍可用：观测不会打断用户。
            return null;
        }

        if (Interaction.IsUserTakeover)
        {
            return new DesktopCapabilityError(
                DesktopCapabilityErrorCode.UserTakeover,
                $"user took over the browser; automation is stopped ({Interaction.Reason})");
        }

        if (Interaction.IsPaused)
        {
            return new DesktopCapabilityError(
                DesktopCapabilityErrorCode.Paused,
                $"tool runtime is paused ({Interaction.Reason})");
        }

        return null;
    }

    /// <summary>
    /// 取消/期限的领域映射。<paramref name="started"/> 区分「从未执行」（副作用必须为 false）
    /// 与「已进入 surface」（可能已产生副作用，由能力 Traits 决定）。
    /// </summary>
    private DesktopCapabilityError MapCancellation(DesktopCapability capability, DesktopCallContext context, bool started)
    {
        var sideEffects = started
            && DesktopCapabilities.TryGet(capability, out var descriptor)
            && descriptor.Traits.HasFlag(DesktopCapabilityTraits.HasSideEffects);

        if (IsClosed)
        {
            return DesktopCapabilityError.UiUnavailable("window closed while the operation was queued");
        }

        return context.IsExpiredAt(Now)
            ? DesktopCapabilityError.DeadlineExceeded(sideEffects)
            : DesktopCapabilityError.Cancelled(sideEffects);
    }

    private DateTimeOffset Now => _timeProvider.GetUtcNow();
}
