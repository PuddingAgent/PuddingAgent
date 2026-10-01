namespace Pudding.Contracts.Desktop;

/// <summary>
/// 能力请求判别联合：把「能力 + 类型化 payload」作为一个整体传递。
///
/// 目的：让执行器接缝（<c>IDesktopCapabilityExecutor</c>）与用户界面/传输实现<b>都不依赖 proto</b>。
/// proto ↔ 本类型的映射是传输适配器的职责，因此换传输不会传染到业务实现。
/// </summary>
public sealed record DesktopCapabilityRequest
{
    private DesktopCapabilityRequest(
        NavigateRequest? navigate,
        JavascriptRequest? javascript,
        DesktopNotificationRequest? notification,
        DesktopPageTarget? pageState,
        BrowserSnapshotRequest? snapshot,
        BrowserLocateRequest? locate,
        BrowserInteractRequest? interact,
        bool shellStatus)
    {
        Navigate = navigate;
        Javascript = javascript;
        Notification = notification;
        PageState = pageState;
        Snapshot = snapshot;
        Locate = locate;
        Interact = interact;
        ShellStatus = shellStatus;
    }

    public NavigateRequest? Navigate { get; }

    public JavascriptRequest? Javascript { get; }

    public DesktopNotificationRequest? Notification { get; }

    /// <summary>只读页面状态查询的目标（该能力不需要其它参数）。</summary>
    public DesktopPageTarget? PageState { get; }

    /// <summary>页面快照请求（含目标、期望页面版本与预算）。</summary>
    public BrowserSnapshotRequest? Snapshot { get; }

    /// <summary>元素定位请求。</summary>
    public BrowserLocateRequest? Locate { get; }

    /// <summary>页面元素交互请求（变更类）。</summary>
    public BrowserInteractRequest? Interact { get; }

    /// <summary>只读 Shell 状态查询：无参数（Shell 是窗口作用域）。</summary>
    public bool ShellStatus { get; }

    public static DesktopCapabilityRequest ForNavigate(NavigateRequest request) =>
        new(request ?? throw new ArgumentNullException(nameof(request)), null, null, null, null, null, null, false);

    public static DesktopCapabilityRequest ForJavascript(JavascriptRequest request) =>
        new(null, request ?? throw new ArgumentNullException(nameof(request)), null, null, null, null, null, false);

    public static DesktopCapabilityRequest ForNotification(DesktopNotificationRequest request) =>
        new(null, null, request ?? throw new ArgumentNullException(nameof(request)), null, null, null, null, false);

    public static DesktopCapabilityRequest ForPageState(DesktopPageTarget target) =>
        new(null, null, null, target ?? throw new ArgumentNullException(nameof(target)), null, null, null, false);

    public static DesktopCapabilityRequest ForSnapshot(BrowserSnapshotRequest request) =>
        new(null, null, null, null, request ?? throw new ArgumentNullException(nameof(request)), null, null, false);

    public static DesktopCapabilityRequest ForLocate(BrowserLocateRequest request) =>
        new(null, null, null, null, null, request ?? throw new ArgumentNullException(nameof(request)), null, false);

    public static DesktopCapabilityRequest ForInteract(BrowserInteractRequest request) =>
        new(null, null, null, null, null, null, request ?? throw new ArgumentNullException(nameof(request)), false);

    public static DesktopCapabilityRequest ForShellStatus() => new(null, null, null, null, null, null, null, true);

    /// <summary>页面目标；通知类与 Shell 状态查询没有页面目标，返回 <c>null</c>。</summary>
    public DesktopPageTarget? Target =>
        Navigate?.Target ?? Javascript?.Target ?? PageState ?? Snapshot?.Target ?? Locate?.Target ?? Interact?.Target;

    /// <summary>期望页面版本；无页面目标或只读查询时为 <see cref="DesktopPageVersion.Unknown"/>。</summary>
    public DesktopPageVersion ExpectedPageVersion =>
        Navigate?.ExpectedPageVersion
        ?? Javascript?.ExpectedPageVersion
        ?? Snapshot?.ExpectedPageVersion
        ?? Locate?.ExpectedPageVersion
        ?? Interact?.ExpectedPageVersion
        ?? DesktopPageVersion.Unknown;

    public override string ToString() =>
        Navigate is not null ? $"navigate {Navigate.Url} @{Navigate.Target}"
        : Javascript is not null ? $"execute_javascript @{Javascript.Target} ({Javascript.Script.Length} chars)"
        : Notification is not null ? "notification"
        : PageState is not null ? $"page_state @{PageState}"
        : Snapshot is not null ? $"snapshot @{Snapshot.Target}"
        : Locate is not null ? $"locate {Locate.Locator} @{Locate.Target}"
        : Interact is not null ? $"interact {Interact}"
        : ShellStatus ? "shell_status"
        : "empty";
}

/// <summary>
/// 能力执行结果判别联合：三选一的类型化输出，或一个领域错误。
/// 恰好一个分支非空，构造期即校验（不允许「成功 + 错误」这种歧义结果）。
/// </summary>
public sealed record DesktopCapabilityResponse
{
    private DesktopCapabilityResponse(
        NavigateResult? navigate,
        JavascriptResult? javascript,
        DesktopNotificationResult? notification,
        DesktopPageState? pageState,
        DesktopShellStatus? shellStatus,
        DesktopSnapshot? snapshot,
        DesktopLocateResult? locate,
        DesktopInteractionResult? interact,
        DesktopCapabilityError? error)
    {
        var payloadCount = (navigate is null ? 0 : 1)
            + (javascript is null ? 0 : 1)
            + (notification is null ? 0 : 1)
            + (pageState is null ? 0 : 1)
            + (shellStatus is null ? 0 : 1)
            + (snapshot is null ? 0 : 1)
            + (locate is null ? 0 : 1)
            + (interact is null ? 0 : 1);
        if (error is null ? payloadCount != 1 : payloadCount != 0)
        {
            throw new ArgumentException(
                "Exactly one of the typed outputs must be set, unless an error is supplied (then none may be set).");
        }

        Navigate = navigate;
        Javascript = javascript;
        Notification = notification;
        PageState = pageState;
        ShellStatus = shellStatus;
        Snapshot = snapshot;
        Locate = locate;
        Interact = interact;
        Error = error;
    }

    public NavigateResult? Navigate { get; }

    public JavascriptResult? Javascript { get; }

    public DesktopNotificationResult? Notification { get; }

    public DesktopPageState? PageState { get; }

    public DesktopShellStatus? ShellStatus { get; }

    /// <summary>页面快照（含 PageVersion 的 DOM/可访问性树）。</summary>
    public DesktopSnapshot? Snapshot { get; }

    /// <summary>元素定位结果（命中的 Ref 携带 PageVersion）。</summary>
    public DesktopLocateResult? Locate { get; }

    /// <summary>元素交互结果（含交互后的页面状态；旧 Ref 自此作废）。</summary>
    public DesktopInteractionResult? Interact { get; }

    public DesktopCapabilityError? Error { get; }

    public bool IsFailure => Error is not null;

    public static DesktopCapabilityResponse FromNavigate(NavigateResult result) =>
        new(result ?? throw new ArgumentNullException(nameof(result)), null, null, null, null, null, null, null, null);

    public static DesktopCapabilityResponse FromJavascript(JavascriptResult result) =>
        new(null, result ?? throw new ArgumentNullException(nameof(result)), null, null, null, null, null, null, null);

    public static DesktopCapabilityResponse FromNotification(DesktopNotificationResult result) =>
        new(null, null, result ?? throw new ArgumentNullException(nameof(result)), null, null, null, null, null, null);

    public static DesktopCapabilityResponse FromPageState(DesktopPageState state) =>
        new(null, null, null, state ?? throw new ArgumentNullException(nameof(state)), null, null, null, null, null);

    public static DesktopCapabilityResponse FromShellStatus(DesktopShellStatus status) =>
        new(null, null, null, null, status ?? throw new ArgumentNullException(nameof(status)), null, null, null, null);

    public static DesktopCapabilityResponse FromSnapshot(DesktopSnapshot snapshot) =>
        new(null, null, null, null, null, snapshot ?? throw new ArgumentNullException(nameof(snapshot)), null, null, null);

    public static DesktopCapabilityResponse FromLocate(DesktopLocateResult result) =>
        new(null, null, null, null, null, null, result ?? throw new ArgumentNullException(nameof(result)), null, null);

    public static DesktopCapabilityResponse FromInteract(DesktopInteractionResult result) =>
        new(null, null, null, null, null, null, null, result ?? throw new ArgumentNullException(nameof(result)), null);

    public static DesktopCapabilityResponse Failure(DesktopCapabilityError error) =>
        new(null, null, null, null, null, null, null, null, error ?? throw new ArgumentNullException(nameof(error)));

    public override string ToString() =>
        Error is not null ? $"failure({Error.WireCode})"
        : Navigate is not null ? $"navigate({Navigate.Disposition})"
        : Javascript is not null ? $"javascript({Javascript.Kind})"
        : Notification is not null ? $"notification(shown={Notification.Shown})"
        : PageState is not null ? $"page_state({PageState.Readiness})"
        : ShellStatus is not null ? $"shell_status({ShellStatus})"
        : Snapshot is not null ? $"snapshot({Snapshot})"
        : Locate is not null ? $"locate({Locate})"
        : Interact is not null ? $"interact({Interact})"
        : "empty";
}
