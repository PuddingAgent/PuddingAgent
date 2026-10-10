using System.Collections.Concurrent;
using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;
using PuddingBrowser.Protocol;

namespace PuddingHost.BrowserBridge;

/// <summary>
/// 触发点门的**组合根实现**：会话**按调用解析**（能力通道由 Desktop 侧按 desktop.json 启用，可能后连上）；
/// 解析不到会话 ⇒ **直接放行**（通道未启用是正常配置；最终判据始终在 Desktop 的唯一 authority）。
/// </summary>
public sealed class CapabilityChannelBindingGate(
    Func<DesktopSession?> sessionResolver,
    DesktopCapabilityDescriptor bindingApplyCapability,
    BrowserPageGrantScope scope,
    TimeSpan validity,
    string source,
    TimeProvider? clock = null) : IBrowserBindingGate
{
    private readonly Func<DesktopSession?> _sessionResolver =
        sessionResolver ?? throw new ArgumentNullException(nameof(sessionResolver));
    private readonly DesktopCapabilityDescriptor _capability =
        bindingApplyCapability ?? throw new ArgumentNullException(nameof(bindingApplyCapability));
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

        var session = _sessionResolver();
        if (session is null)
        {
            return;
        }

        var key = $"{session.DesktopId.Value}|{contextId}/{pageId}|{caller.CallerId}|{caller.TaskId}|{caller.ParentTaskId}";
        var now = _clock.GetUtcNow();
        if (_issuedAt.TryGetValue(key, out var issuedAt) && now - issuedAt < _validity)
        {
            return;
        }

        var producer = new DesktopBrowserBindingProducer(
            (capability, request, call, innerToken) => session.SendAsync(capability, request, call, innerToken),
            session.DesktopId,
            _capability,
            _clock);

        var outcome = await producer
            .IssueAsync(
                new BrowserTaskBinding(caller, new DesktopPageTarget(contextId, pageId), _scope, _validity, _source),
                new OperationId(Guid.NewGuid().ToString("N")),
                deadline: null,
                cancellationToken)
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