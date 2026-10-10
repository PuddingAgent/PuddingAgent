using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.AgentTools;

namespace PuddingHost.Hosting;

/// <summary>
/// 基于**活动会话**产生能力调用上下文。
///
/// · Desktop 实例 ID 取自活动会话（`DesktopSession` 会校验调用里的 DesktopId 与自身一致，
///   所以这里用会话自己的 ID 而不是配置里的"期望值"——两者不一致时应当由会话拒绝，而不是我们猜）；
/// · 操作 ID 每次新生成（broker 对"同 ID 不同 payload"判重复请求）；
/// · 期限 = 现在 + 本次预算或默认值；
/// · **调用者身份**取自工具调用作用域（方案 §3.1：Host 的 context factory 从 accessor 取，两个传输共用同一投影结果）。
///   没有可信身份时**不生成默认身份**（上下文里就是没有 caller，浏览器能力会因此被拒）。
///
/// 用委托而不是直接依赖 `CapabilityBroker`：本类因此可以脱离宿主单测，
/// 组合根只提供一眼看得懂的"当前活动 Desktop"解析（没有活动会话就返回 <c>null</c>）。
/// </summary>
internal sealed class ConnectedDesktopCallContextFactory(
    Func<DesktopInstanceId?> activeDesktopId,
    TimeProvider? clock = null,
    TimeSpan? defaultTimeout = null,
    IBrowserCallerIdentityAccessor? callerIdentity = null) : IDesktopCapabilityCallContextFactory
{
    /// <summary>默认调用期限：够一次浏览器操作往返，又不至于让调用悬挂。</summary>
    internal static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(120);

    private readonly Func<DesktopInstanceId?> _activeDesktopId =
        activeDesktopId ?? throw new ArgumentNullException(nameof(activeDesktopId));

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private readonly TimeSpan _defaultTimeout = defaultTimeout ?? DefaultCallTimeout;

    private readonly IBrowserCallerIdentityAccessor? _callerIdentity = callerIdentity;

    public DesktopCallContext? TryCreate(string? permissionEvidenceSummary, TimeSpan? timeout = null)
    {
        var call = TryCreate(timeout);
        // 证据是**进程内**字段：不上线缆，Desktop 侧重建上下文时恒为 null（设计意图）。
        return call is null || permissionEvidenceSummary is null
            ? call
            : call with { PermissionEvidenceSummary = permissionEvidenceSummary };
    }

    public DesktopCallContext? TryCreate(TimeSpan? timeout = null)
    {
        if (_activeDesktopId() is not { } desktopId)
        {
            // 没有活动 Desktop：不产生上下文（调用方据此明确失败，不猜实例 ID）。
            return null;
        }

        // §3.1：身份来自**调用级作用域**（工具执行时投影一次），不在这里推断、也不给默认值。
        // 没有身份 ⇒ 上下文里没有 caller ⇒ 浏览器能力会在 Desktop 侧被明确拒绝（fail-closed）。
        return new DesktopCallContext(
            desktopId,
            OperationId.NewId(),
            _clock.GetUtcNow() + (timeout ?? _defaultTimeout),
            correlationId: null,
            browserCaller: _callerIdentity?.Current);
    }
}
