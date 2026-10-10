using System.Collections.Concurrent;
using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;
using PuddingBrowser.Protocol;

namespace PuddingHost.BrowserBridge;

/// <summary>
/// 触发点的**默认实现**：把"发命令前过门"接到 Core 侧签发生产者，并按 (调用者, 任务, 页面) 记住已签发的授权。
///
/// 语义：
/// <list type="bullet">
///   <item>首次遇到某 (调用者, 任务, 页面) ⇒ 调 <see cref="DesktopBrowserBindingProducer.IssueAsync"/>；</item>
///   <item>已在有效期内 ⇒ **不重复申请**（Desktop 的受理账本也按 (任务, 目标) 记账，重发只会抖动）；</item>
///   <item>Desktop 拒绝（ack accepted=false）或调用失败 ⇒ **抛 <see cref="BrowserOperationException"/>**（fail-closed），
///         调用方因此**不会发出浏览器命令**。</item>
/// </list>
///
/// 已知边界：本类**不观察** Desktop 侧的撤销（切目标/撤权/换代）—— 那会让缓存偏乐观，届时 Desktop 的准入会拒绝该命令，
/// 表现为操作失败（不会静默放行）。按需在下一次申请时收敛，属已登记的后续项。
/// </summary>
public sealed class DesktopBrowserBindingGate(
    DesktopBrowserBindingProducer producer,
    BrowserPageGrantScope scope,
    TimeSpan validity,
    string source,
    TimeProvider? clock = null) : IBrowserBindingGate
{
    private readonly DesktopBrowserBindingProducer _producer = producer ?? throw new ArgumentNullException(nameof(producer));
    private readonly BrowserPageGrantScope _scope = scope;
    private readonly TimeSpan _validity = validity;
    private readonly string _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _issuedAt = new(StringComparer.Ordinal);

    public async Task EnsureAsync(
        BrowserCallerIdentity caller,
        string contextId,
        string pageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var key = $"{contextId}/{pageId}|{caller.CallerId}|{caller.TaskId}|{caller.ParentTaskId}";
        var now = _clock.GetUtcNow();
        if (_issuedAt.TryGetValue(key, out var issuedAt) && now - issuedAt < _validity)
        {
            return; // 有效期内不重复申请
        }

        var binding = new BrowserTaskBinding(
            caller,
            new DesktopPageTarget(contextId, pageId),
            _scope,
            _validity,
            _source);

        var outcome = await _producer
            .IssueAsync(binding, new OperationId(Guid.NewGuid().ToString("N")), deadline: null, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Issued)
        {
            throw new BrowserOperationException(
                BrowserBridgeErrorCodes.BrowserUnauthorized,
                outcome.Reason ?? "the desktop refused the browser binding");
        }

        _issuedAt[key] = now;
    }
}