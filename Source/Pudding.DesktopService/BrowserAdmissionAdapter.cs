using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Automation;

namespace Pudding.DesktopService;

/// <summary>一次准入/复检的结果。拒绝时给出**可诊断的原因**（不回退成"放行"）。</summary>
public sealed record BrowserAdmissionOutcome(
    bool Accepted,
    BrowserAutomationLease? Lease,
    DesktopCapabilityError? Error,
    string? Reason);

/// <summary>
/// **两传输共用的准入适配器**（方案 §4.2）。
///
/// 为什么不各自实现：能力通道（gRPC）与 Bridge（WebSocket）若各自校验，就会出现"两条路径各自验出相反结论"
/// （设计 §1.1）。本类把两条传输收敛到**同一个** <see cref="BrowserPageLifecycle"/> 与**同一份**请求形状：
/// <list type="number">
///   <item>入队前：<see cref="Admit"/>（写操作在这里拿到租约）；</item>
///   <item>真正触碰页面前：<see cref="Revalidate"/>（排队期间可能被撤权/接管/关页）；</item>
///   <item><c>finally</c>：<see cref="Release"/> 释放本操作租约（幂等，允许重复调用）。</item>
/// </list>
///
/// 本类**不产生**调用者身份（身份来自上游已校验的作用域），也**不**自己写拒绝策略 —— 判据都在唯一 authority 里。
/// </summary>
public sealed class BrowserAdmissionAdapter(BrowserPageLifecycle lifecycle)
{
    private readonly BrowserPageLifecycle _lifecycle =
        lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

    /// <summary>入队**之前**的准入：拒绝 ⇒ 不排队、不执行。</summary>
    public BrowserAdmissionOutcome Admit(
        BrowserCallerIdentity caller,
        BrowserAutomationOperation operation,
        OperationId operationId,
        ConnectionGeneration connectionGeneration,
        DesktopPageTarget? target = null,
        string? frameId = null)
        => Decide(_lifecycle.Authority.Admit(
            Build(caller, operation, operationId, connectionGeneration, target, frameId)));

    /// <summary>执行**之前**的复检：租约+请求一起复查（排队期间撤权/接管/换代都会在这里被拦下）。</summary>
    public BrowserAdmissionOutcome Revalidate(
        BrowserAutomationLease lease,
        BrowserCallerIdentity caller,
        BrowserAutomationOperation operation,
        OperationId operationId,
        ConnectionGeneration connectionGeneration,
        DesktopPageTarget? target = null,
        string? frameId = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return Decide(_lifecycle.Authority.Revalidate(
            lease, Build(caller, operation, operationId, connectionGeneration, target, frameId)));
    }

    /// <summary>释放本操作租约。传 <c>null</c>（未获租约，例如只读）或重复调用都安全。</summary>
    public void Release(BrowserAutomationLease? lease, string reason = "operation completed")
    {
        if (lease is null)
        {
            return;
        }

        _lifecycle.Authority.ReleaseLease(lease.LeaseId, reason);
    }

    private static BrowserAutomationRequest Build(
        BrowserCallerIdentity caller,
        BrowserAutomationOperation operation,
        OperationId operationId,
        ConnectionGeneration connectionGeneration,
        DesktopPageTarget? target,
        string? frameId)
        => new(operation, caller, operationId, connectionGeneration, target, frameId);

    private static BrowserAdmissionOutcome Decide(BrowserAuthorizationDecision decision)
        => decision.IsAllowed
            ? new BrowserAdmissionOutcome(true, decision.Lease, null, null)
            : new BrowserAdmissionOutcome(
                false,
                null,
                decision.Error,
                decision.Error?.Message ?? decision.Denial.ToString());
}
