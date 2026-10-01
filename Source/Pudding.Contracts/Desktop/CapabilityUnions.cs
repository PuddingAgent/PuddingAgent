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
        NavigateRequest? navigate = null,
        JavascriptRequest? javascript = null,
        DesktopNotificationRequest? notification = null,
        DesktopPageTarget? pageState = null,
        BrowserSnapshotRequest? snapshot = null,
        BrowserLocateRequest? locate = null,
        BrowserInteractRequest? interact = null,
        BrowserWaitForRequest? waitFor = null,
        BrowserTabsRequest? tabs = null,
        ClipboardReadRequest? clipboard = null,
        DesktopDialogRequest? dialog = null,
        DesktopFilePickerRequest? filePicker = null,
        bool contexts = false,
        bool shellStatus = false)
    {
        Navigate = navigate;
        Javascript = javascript;
        Notification = notification;
        PageState = pageState;
        Snapshot = snapshot;
        Locate = locate;
        Interact = interact;
        WaitFor = waitFor;
        Tabs = tabs;
        Clipboard = clipboard;
        Dialog = dialog;
        FilePicker = filePicker;
        Contexts = contexts;
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

    /// <summary>条件等待请求（只读）。</summary>
    public BrowserWaitForRequest? WaitFor { get; }

    /// <summary>标签页操作请求（变更类）。</summary>
    public BrowserTabsRequest? Tabs { get; }

    /// <summary>剪贴板读取请求（v1 只读）。</summary>
    public ClipboardReadRequest? Clipboard { get; }

    /// <summary>对话框请求（交互类：单窗口同时最多一个）。</summary>
    public DesktopDialogRequest? Dialog { get; }

    /// <summary>文件选择器请求（交互类：与对话框共用同一个槽位）。</summary>
    public DesktopFilePickerRequest? FilePicker { get; }

    /// <summary>浏览器上下文清单查询：无参数（浏览器作用域，不接受调用方指定目标）。</summary>
    public bool Contexts { get; }

    /// <summary>只读 Shell 状态查询：无参数（Shell 是窗口作用域）。</summary>
    public bool ShellStatus { get; }

    public static DesktopCapabilityRequest ForNavigate(NavigateRequest request) =>
        new(navigate: request ?? throw new ArgumentNullException(nameof(request)));

    public static DesktopCapabilityRequest ForJavascript(JavascriptRequest request) =>
        new(javascript: request ?? throw new ArgumentNullException(nameof(request)));

    public static DesktopCapabilityRequest ForNotification(DesktopNotificationRequest request) =>
        new(notification: request ?? throw new ArgumentNullException(nameof(request)));

    public static DesktopCapabilityRequest ForPageState(DesktopPageTarget target) =>
        new(pageState: target ?? throw new ArgumentNullException(nameof(target)));

    public static DesktopCapabilityRequest ForSnapshot(BrowserSnapshotRequest request) =>
        new(snapshot: request ?? throw new ArgumentNullException(nameof(request)));

    public static DesktopCapabilityRequest ForLocate(BrowserLocateRequest request) =>
        new(locate: request ?? throw new ArgumentNullException(nameof(request)));

    public static DesktopCapabilityRequest ForInteract(BrowserInteractRequest request) =>
        new(interact: request ?? throw new ArgumentNullException(nameof(request)));

    public static DesktopCapabilityRequest ForWaitFor(BrowserWaitForRequest request) =>
        new(waitFor: request ?? throw new ArgumentNullException(nameof(request)));

    public static DesktopCapabilityRequest ForTabs(BrowserTabsRequest request) =>
        new(tabs: request ?? throw new ArgumentNullException(nameof(request)));

    public static DesktopCapabilityRequest ForClipboard(ClipboardReadRequest request) =>
        new(clipboard: request ?? throw new ArgumentNullException(nameof(request)));

    public static DesktopCapabilityRequest ForDialog(DesktopDialogRequest request) =>
        new(dialog: request ?? throw new ArgumentNullException(nameof(request)));

    public static DesktopCapabilityRequest ForFilePicker(DesktopFilePickerRequest request) =>
        new(filePicker: request ?? throw new ArgumentNullException(nameof(request)));

    public static DesktopCapabilityRequest ForContexts() => new(contexts: true);

    public static DesktopCapabilityRequest ForShellStatus() => new(shellStatus: true);

    /// <summary>页面目标；通知类与 Shell 状态查询没有页面目标，返回 <c>null</c>。</summary>
    public DesktopPageTarget? Target =>
        Navigate?.Target ?? Javascript?.Target ?? PageState ?? Snapshot?.Target ?? Locate?.Target ?? Interact?.Target
        ?? WaitFor?.Target ?? Tabs?.Target;

    /// <summary>期望页面版本；无页面目标或只读查询时为 <see cref="DesktopPageVersion.Unknown"/>。</summary>
    public DesktopPageVersion ExpectedPageVersion =>
        Navigate?.ExpectedPageVersion
        ?? Javascript?.ExpectedPageVersion
        ?? Snapshot?.ExpectedPageVersion
        ?? Locate?.ExpectedPageVersion
        ?? Interact?.ExpectedPageVersion
        ?? WaitFor?.ExpectedPageVersion
        ?? Tabs?.ExpectedPageVersion
        ?? DesktopPageVersion.Unknown;

    public override string ToString() =>
        Navigate is not null ? $"navigate {Navigate.Url} @{Navigate.Target}"
        : Javascript is not null ? $"execute_javascript @{Javascript.Target} ({Javascript.Script.Length} chars)"
        : Notification is not null ? "notification"
        : PageState is not null ? $"page_state @{PageState}"
        : Snapshot is not null ? $"snapshot @{Snapshot.Target}"
        : Locate is not null ? $"locate {Locate.Locator} @{Locate.Target}"
        : Interact is not null ? $"interact {Interact}"
        : WaitFor is not null ? $"wait_for {WaitFor.Condition} @{WaitFor.Target}"
        : Tabs is not null ? $"tabs {Tabs}"
        : Clipboard is not null ? $"clipboard(max={Clipboard.MaxCharacters})"
        : Dialog is not null ? $"dialog {Dialog}"
        : FilePicker is not null ? $"file_picker {FilePicker}"
        : Contexts ? "contexts"
        : ShellStatus ? "shell_status"
        : "empty";
}

/// <summary>
/// 能力执行结果判别联合：三选一的类型化输出，或一个领域错误。
/// 恰好一个分支非空，构造期即校验（不允许「成功 + 错误」这种歧义结果）。
/// </summary>
public sealed record DesktopCapabilityResponse
{
    /// <summary>
    /// 全部参数可空且有默认值：新增分支时不需要改动既有工厂（此前的参数计数陷阱已多次造成返工），
    /// 正确性仍由下面「恰好一个分支非空」的不变量兜底。
    /// </summary>
    private DesktopCapabilityResponse(
        NavigateResult? navigate = null,
        JavascriptResult? javascript = null,
        DesktopNotificationResult? notification = null,
        DesktopPageState? pageState = null,
        DesktopShellStatus? shellStatus = null,
        DesktopSnapshot? snapshot = null,
        DesktopLocateResult? locate = null,
        DesktopInteractionResult? interact = null,
        DesktopWaitResult? wait = null,
        DesktopContexts? contexts = null,
        DesktopTabsResult? tabs = null,
        DesktopClipboardContent? clipboard = null,
        DesktopDialogResult? dialog = null,
        DesktopFilePickerResult? filePicker = null,
        DesktopCapabilityError? error = null)
    {
        var payloadCount = (navigate is null ? 0 : 1)
            + (javascript is null ? 0 : 1)
            + (notification is null ? 0 : 1)
            + (pageState is null ? 0 : 1)
            + (shellStatus is null ? 0 : 1)
            + (snapshot is null ? 0 : 1)
            + (locate is null ? 0 : 1)
            + (interact is null ? 0 : 1)
            + (wait is null ? 0 : 1)
            + (contexts is null ? 0 : 1)
            + (tabs is null ? 0 : 1)
            + (clipboard is null ? 0 : 1)
            + (dialog is null ? 0 : 1)
            + (filePicker is null ? 0 : 1);
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
        Wait = wait;
        Contexts = contexts;
        Tabs = tabs;
        Clipboard = clipboard;
        Dialog = dialog;
        FilePicker = filePicker;
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

    /// <summary>条件等待结果（超时也带页面状态）。</summary>
    public DesktopWaitResult? Wait { get; }

    /// <summary>上下文与页面清单。</summary>
    public DesktopContexts? Contexts { get; }

    /// <summary>标签页操作结果（含剩余清单）。</summary>
    public DesktopTabsResult? Tabs { get; }

    /// <summary>剪贴板内容（只读；内容不进日志）。</summary>
    public DesktopClipboardContent? Clipboard { get; }

    /// <summary>对话框结果（取消是结果而不是失败）。</summary>
    public DesktopDialogResult? Dialog { get; }

    /// <summary>文件选择结果（取消是结果而不是失败）。</summary>
    public DesktopFilePickerResult? FilePicker { get; }

    public DesktopCapabilityError? Error { get; }

    public bool IsFailure => Error is not null;

    public static DesktopCapabilityResponse FromNavigate(NavigateResult result) =>
        new(navigate: result ?? throw new ArgumentNullException(nameof(result)));

    public static DesktopCapabilityResponse FromJavascript(JavascriptResult result) =>
        new(javascript: result ?? throw new ArgumentNullException(nameof(result)));

    public static DesktopCapabilityResponse FromNotification(DesktopNotificationResult result) =>
        new(notification: result ?? throw new ArgumentNullException(nameof(result)));

    public static DesktopCapabilityResponse FromPageState(DesktopPageState state) =>
        new(pageState: state ?? throw new ArgumentNullException(nameof(state)));

    public static DesktopCapabilityResponse FromShellStatus(DesktopShellStatus status) =>
        new(shellStatus: status ?? throw new ArgumentNullException(nameof(status)));

    public static DesktopCapabilityResponse FromSnapshot(DesktopSnapshot snapshot) =>
        new(snapshot: snapshot ?? throw new ArgumentNullException(nameof(snapshot)));

    public static DesktopCapabilityResponse FromLocate(DesktopLocateResult result) =>
        new(locate: result ?? throw new ArgumentNullException(nameof(result)));

    public static DesktopCapabilityResponse FromInteract(DesktopInteractionResult result) =>
        new(interact: result ?? throw new ArgumentNullException(nameof(result)));

    public static DesktopCapabilityResponse FromWait(DesktopWaitResult result) =>
        new(wait: result ?? throw new ArgumentNullException(nameof(result)));

    public static DesktopCapabilityResponse FromContexts(DesktopContexts contexts) =>
        new(contexts: contexts ?? throw new ArgumentNullException(nameof(contexts)));

    public static DesktopCapabilityResponse FromTabs(DesktopTabsResult result) =>
        new(tabs: result ?? throw new ArgumentNullException(nameof(result)));

    public static DesktopCapabilityResponse FromClipboard(DesktopClipboardContent content) =>
        new(clipboard: content ?? throw new ArgumentNullException(nameof(content)));

    public static DesktopCapabilityResponse FromDialog(DesktopDialogResult result) =>
        new(dialog: result ?? throw new ArgumentNullException(nameof(result)));

    public static DesktopCapabilityResponse FromFilePicker(DesktopFilePickerResult result) =>
        new(filePicker: result ?? throw new ArgumentNullException(nameof(result)));

    public static DesktopCapabilityResponse Failure(DesktopCapabilityError error) =>
        new(error: error ?? throw new ArgumentNullException(nameof(error)));
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
        : Wait is not null ? $"wait_for({Wait})"
        : Contexts is not null ? $"contexts({Contexts})"
        : Tabs is not null ? $"tabs({Tabs})"
        : Clipboard is not null ? $"clipboard({Clipboard})"
        : Dialog is not null ? $"dialog({Dialog})"
        : FilePicker is not null ? $"file_picker({FilePicker})"
        : "empty";
}
