namespace Pudding.Contracts.Desktop;

/// <summary>
/// **任务绑定**（方案 §4.1）：Core 从**可信元数据**得到的"哪个任务被授权操作哪个页面"这一事实，
/// 交给 Desktop 作为签发的依据。
///
/// 为什么必须有它：用户"把某页选定给 Agent"只说明**页面**，不说明**哪个任务**；
/// 而普通能力调用**不能**把自己授权成目标页 owner（§4.1 明禁）⇒ grantee 必须来自这份绑定，
/// 且绑定只接受**Core 可信元数据**（不是模型参数、不是工具入参）。
///
/// 本类型只做**结构校验**（fail-closed）；签发时的 Desktop 侧策略（有效期上限、目标已登记、连接世代存活、
/// 子代理子集）仍由 <c>BrowserGrantIssuer</c> 与唯一 authority 判定。
/// </summary>
public sealed record BrowserTaskBinding
{
    public const int MaxSourceLength = 128;

    public BrowserTaskBinding(
        BrowserCallerIdentity task,
        DesktopPageTarget target,
        BrowserPageGrantScope scope,
        TimeSpan validity,
        string source)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        Target = target ?? throw new ArgumentNullException(nameof(target));

        // grantee 必须是**可信**任务身份：复用契约里的唯一规则（缺身份/冒充 User/无运行身份都拒绝）。
        if (!BrowserCallerRequirement.TryRequireCaller(task, out _, out var callerReason))
        {
            throw new ArgumentException(callerReason, nameof(task));
        }

        if (scope == BrowserPageGrantScope.None)
        {
            throw new ArgumentException("A task binding with no scope must not be created.", nameof(scope));
        }

        if (validity <= TimeSpan.Zero)
        {
            throw new ArgumentException("Task binding validity must be positive.", nameof(validity));
        }

        Scope = scope;
        Validity = validity;
        Source = ContractText.RequireIdentifier(source, MaxSourceLength, nameof(source));
    }

    /// <summary>被授权的任务运行身份（grantee）。</summary>
    public BrowserCallerIdentity Task { get; }

    /// <summary>用户选定的目标页。</summary>
    public DesktopPageTarget Target { get; }

    /// <summary>允许的范围（签发时仍受 Desktop 上限与父授权子集约束）。</summary>
    public BrowserPageGrantScope Scope { get; }

    /// <summary>期限（签发时仍受 Desktop 上限约束；超限**拒绝**而非截断）。</summary>
    public TimeSpan Validity { get; }

    /// <summary>授权来源标识（审计用；不接受用户文本授予能力）。</summary>
    public string Source { get; }
}
