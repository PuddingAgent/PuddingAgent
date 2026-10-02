using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;

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
        TimeProvider? timeProvider = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        Targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>窗口退出时触发（用于让宿主停止派发新命令）。</summary>
    public event Action? Closed;

    public DesktopTargetRegistry Targets { get; }

    public DesktopInteractionState Interaction { get; } = new();

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>窗口退出：拒绝新调用，并让排队中的 UI 调用以 UiUnavailable 结束（不悬挂）。</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
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

                var navigationViolation = DesktopMutationInvariants.RequireVersionAdvanced(
                    capability.Capability, request.ExpectedPageVersion, result.Value.PageVersion);
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

                var result = await RunOnUiAsync(
                    descriptor,
                    tabs.Target,
                    tabs.ExpectedPageVersion,
                    context,
                    token => _surface.TabsAsync(context, tabs, token),
                    cancellationToken).ConfigureAwait(false);

                if (result.IsFailure)
                {
                    return DesktopCapabilityResponse.Failure(result.Error);
                }

                var tabsViolation = DesktopMutationInvariants.RequireVersionAdvanced(
                    capability.Capability, tabs.ExpectedPageVersion, result.Value.Page.Version);
                return tabsViolation is not null
                    ? DesktopCapabilityResponse.Failure(tabsViolation)
                    : DesktopCapabilityResponse.FromTabs(result.Value);
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

                var interactionViolation = DesktopMutationInvariants.RequireVersionAdvanced(
                    capability.Capability, interact.ExpectedPageVersion, result.Value.Page.Version);
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
        CancellationToken cancellationToken)
    {
        var admission = ValidateAdmission(capability, target, expectedPageVersion, context, out _);
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
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);

            return await _dispatcher.InvokeAsync(
                token => ExecuteOnUiAsync(capability, target, expectedPageVersion, context, action, token),
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
    }

    /// <summary>在 UI 线程上执行：先复检竞态，再调用 surface，最后把异常映射成领域错误。</summary>
    private async Task<CapabilityResult<TResult>> ExecuteOnUiAsync<TResult>(
        DesktopCapability capability,
        DesktopPageTarget? target,
        DesktopPageVersion expectedPageVersion,
        DesktopCallContext context,
        Func<CancellationToken, Task<CapabilityResult<TResult>>> action,
        CancellationToken token)
    {
        var recheck = ValidateAdmission(capability, target, expectedPageVersion, context, out _);
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
        catch (Exception ex)
        {
            // 不把 surface 的异常消息透出去（可能含页面数据）；只保留异常类型。
            return CapabilityResult<TResult>.Failure(
                DesktopCapabilityError.Internal($"ui surface failed ({ex.GetType().Name})"));
        }
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
        out DesktopTargetState? pageState)
    {
        pageState = null;

        if (IsClosed)
        {
            return DesktopCapabilityError.UiUnavailable("window is closed");
        }

        if (!_options.AllowedCapabilities.HasFlag(capability))
        {
            return DesktopCapabilityError.UnsupportedCapability(DesktopCapabilities.NameOf(capability));
        }

        var trust = _options.ShellCallerTrust;
        if (DesktopCapabilityPolicy.RequiresPageTarget(capability))
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
