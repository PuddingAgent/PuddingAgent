using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Automation;

namespace Pudding.DesktopService;

/// <summary>
/// 一次授权签发请求（方案 §4.1）。**只接受可信输入**：
/// 行动者必须来自已校验的身份（不是工具参数、不是模型自报），目标是**已登记**的页面目标，
/// 范围与有效期由调用方按用户目标绑定给出。本类型**没有**"owner/epoch"之类的模型可控字段。
/// </summary>
public sealed record BrowserGrantIssuanceRequest(
    BrowserCallerIdentity Caller,
    DesktopPageTarget Target,
    BrowserPageGrantScope Scope,
    TimeSpan Validity,
    string Source,
    /// <summary>子代理必须提供**父任务已授权**的范围：签发范围只能取它的子集。</summary>
    BrowserPageGrantScope? ParentScope = null);

/// <summary>签发上限（由 Desktop 侧策略决定，不接受调用方抬价）。</summary>
public sealed record BrowserGrantIssuanceLimits(TimeSpan MaxValidity)
{
    public static readonly BrowserGrantIssuanceLimits Default = new(TimeSpan.FromMinutes(30));
}

/// <summary>
/// **授权签发的生产路径**（方案 §4.1）：此前 <c>BrowserAutomationAuthority.IssueGrant</c> 没有任何生产调用方，
/// "页面已授权"只能靠人记得。本类把"用户目标绑定 → 校验本地事实 → 调唯一 authority 签发"收成一个可判定入口。
///
/// 拒绝面（缺一即拒，**不**签发一个"差不多"的授权）：
/// <list type="bullet">
///   <item>行动者不可信（缺身份 / 冒充 User / 无运行身份）；</item>
///   <item>有效期 &lt;= 0 或超过 Desktop 策略上限（不接受调用方抬价）；</item>
///   <item>范围为空或含未登记位；</item>
///   <item>子代理：未提供父范围，或请求范围**不是**父范围的子集（子代理不得扩大父授权）；</item>
///   <item>目标未被登记为本 Desktop 的页面目标（禁止对"当前激活 Tab"之类隐式目标签发）。</item>
/// </list>
/// </summary>
public sealed class BrowserGrantIssuer(
    BrowserPageLifecycle lifecycle,
    DesktopInstanceId desktopId,
    DesktopProcessInstanceId processInstanceId,
    Func<ConnectionGeneration> currentConnectionGeneration,
    TimeProvider? clock = null,
    BrowserGrantIssuanceLimits? limits = null)
{
    private readonly BrowserPageLifecycle _lifecycle =
        lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

    private readonly DesktopInstanceId _desktopId =
        desktopId ?? throw new ArgumentNullException(nameof(desktopId));

    private readonly DesktopProcessInstanceId _processInstanceId =
        processInstanceId ?? throw new ArgumentNullException(nameof(processInstanceId));

    private readonly Func<ConnectionGeneration> _currentConnectionGeneration =
        currentConnectionGeneration ?? throw new ArgumentNullException(nameof(currentConnectionGeneration));

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private readonly BrowserGrantIssuanceLimits _limits = limits ?? BrowserGrantIssuanceLimits.Default;

    /// <summary>尝试签发；失败时给出**具体原因**（调用方如实上报，绝不改用默认授权）。</summary>
    public bool TryIssue(
        BrowserGrantIssuanceRequest request,
        out BrowserPageGrant? grant,
        out BrowserPageGrantEpoch epoch,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(request);
        grant = null;
        epoch = default;

        if (!BrowserCallerRequirement.TryRequireCaller(request.Caller, out var caller, out reason))
        {
            return false;
        }

        if (request.Scope == BrowserPageGrantScope.None)
        {
            reason = "a grant with no scope must not be issued";
            return false;
        }

        if (request.Validity <= TimeSpan.Zero)
        {
            reason = "grant validity must be positive";
            return false;
        }

        if (request.Validity > _limits.MaxValidity)
        {
            // Desktop 侧策略定上限：调用方不能靠"我要一小时"把授权拉长。
            reason = $"grant validity {request.Validity} exceeds the desktop limit {_limits.MaxValidity}";
            return false;
        }

        if (caller.Kind == BrowserCallerKind.SubAgent)
        {
            if (request.ParentScope is not { } parentScope
                || (request.Scope & ~parentScope) != 0)
            {
                // §3.2/§4.1：子代理只能取得父授权的子集 —— 这里也挡一次，不依赖上游自觉。
                reason = "a sub-agent grant must be a subset of its parent's granted scope";
                return false;
            }
        }

        if (_lifecycle.PageTargets.Resolve(request.Target) is null)
        {
            // 目标必须**已登记**（用户选定/已建页），禁止对隐式或未登记目标签发。
            reason = $"page target {request.Target} is not registered on this desktop";
            return false;
        }

        var generation = _currentConnectionGeneration();
        if (!generation.IsLive)
        {
            reason = "the desktop connection is not live; a grant issued now could not be validated";
            return false;
        }

        var issuedAt = _clock.GetUtcNow();
        grant = new BrowserPageGrant(
            new BrowserPageGrantId(Guid.NewGuid().ToString("N")),
            new BrowserPageGrantBinding(_desktopId, _processInstanceId, generation),
            request.Target,
            caller,
            request.Scope,
            epoch: default,
            issuedAt,
            expiresAtUtc: issuedAt + request.Validity);

        epoch = _lifecycle.Authority.IssueGrant(grant);
        reason = string.Empty;
        return true;
    }
}
