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
        DesktopNotificationRequest? notification)
    {
        Navigate = navigate;
        Javascript = javascript;
        Notification = notification;
    }

    public NavigateRequest? Navigate { get; }

    public JavascriptRequest? Javascript { get; }

    public DesktopNotificationRequest? Notification { get; }

    public static DesktopCapabilityRequest ForNavigate(NavigateRequest request) =>
        new(request ?? throw new ArgumentNullException(nameof(request)), null, null);

    public static DesktopCapabilityRequest ForJavascript(JavascriptRequest request) =>
        new(null, request ?? throw new ArgumentNullException(nameof(request)), null);

    public static DesktopCapabilityRequest ForNotification(DesktopNotificationRequest request) =>
        new(null, null, request ?? throw new ArgumentNullException(nameof(request)));

    /// <summary>页面目标；通知类能力没有页面目标，返回 <c>null</c>。</summary>
    public DesktopPageTarget? Target => Navigate?.Target ?? Javascript?.Target;

    /// <summary>期望页面版本；无页面目标时为 <see cref="DesktopPageVersion.Unknown"/>。</summary>
    public DesktopPageVersion ExpectedPageVersion =>
        Navigate?.ExpectedPageVersion ?? Javascript?.ExpectedPageVersion ?? DesktopPageVersion.Unknown;

    public override string ToString() =>
        Navigate is not null ? $"navigate {Navigate.Url} @{Navigate.Target}"
        : Javascript is not null ? $"execute_javascript @{Javascript.Target} ({Javascript.Script.Length} chars)"
        : Notification is not null ? "notification"
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
        DesktopCapabilityError? error)
    {
        var payloadCount = (navigate is null ? 0 : 1) + (javascript is null ? 0 : 1) + (notification is null ? 0 : 1);
        if (error is null ? payloadCount != 1 : payloadCount != 0)
        {
            throw new ArgumentException(
                "Exactly one of the typed outputs must be set, unless an error is supplied (then none may be set).");
        }

        Navigate = navigate;
        Javascript = javascript;
        Notification = notification;
        Error = error;
    }

    public NavigateResult? Navigate { get; }

    public JavascriptResult? Javascript { get; }

    public DesktopNotificationResult? Notification { get; }

    public DesktopCapabilityError? Error { get; }

    public bool IsFailure => Error is not null;

    public static DesktopCapabilityResponse FromNavigate(NavigateResult result) =>
        new(result ?? throw new ArgumentNullException(nameof(result)), null, null, null);

    public static DesktopCapabilityResponse FromJavascript(JavascriptResult result) =>
        new(null, result ?? throw new ArgumentNullException(nameof(result)), null, null);

    public static DesktopCapabilityResponse FromNotification(DesktopNotificationResult result) =>
        new(null, null, result ?? throw new ArgumentNullException(nameof(result)), null);

    public static DesktopCapabilityResponse Failure(DesktopCapabilityError error) =>
        new(null, null, null, error ?? throw new ArgumentNullException(nameof(error)));

    public override string ToString() =>
        Error is not null ? $"failure({Error.WireCode})"
        : Navigate is not null ? $"navigate({Navigate.Disposition})"
        : Javascript is not null ? $"javascript({Javascript.Kind})"
        : Notification is not null ? $"notification(shown={Notification.Shown})"
        : "empty";
}
