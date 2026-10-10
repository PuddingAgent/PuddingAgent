using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.CapabilityBroker;

/// <summary>Core 侧"发一次能力调用"的形状（可指真实 DesktopSession，也可替身化）。</summary>
public delegate Task<CapabilityResult<DesktopCapabilityResponse>> DesktopCapabilitySender(
    DesktopCapabilityDescriptor capability,
    DesktopCapabilityRequest request,
    DesktopCallContext call,
    CancellationToken cancellationToken);

/// <summary>签发结果（只陈述事实：accepted 才带 grant；拒绝必带可区分原因）。</summary>
public sealed record DesktopBindingIssuanceOutcome(
    bool Issued,
    BrowserPageGrantId? GrantId,
    BrowserPageGrantEpoch Epoch,
    string? Reason);

/// <summary>
/// Core 侧签发生产者（§4.1）：把"某任务被授权操作某页"经既有控制面发出 browser.binding.apply，并把 ack 如实还原。
/// Desktop 侧整套已就绪却没有生产调用方 ⇒ 没有 grant ⇒ 开闸门后浏览器能力会被全线拒绝；本类是那个缺失调用方的形状。
/// 授权判断不在这里（归 Desktop 唯一 authority）。
/// </summary>
public sealed class DesktopBrowserBindingProducer(
    DesktopCapabilitySender send,
    DesktopInstanceId desktopId,
    DesktopCapabilityDescriptor bindingApplyCapability,
    TimeProvider? clock = null)
{
    private readonly DesktopCapabilitySender _send = send ?? throw new ArgumentNullException(nameof(send));
    private readonly DesktopInstanceId _desktopId = desktopId ?? throw new ArgumentNullException(nameof(desktopId));
    private readonly DesktopCapabilityDescriptor _capability = bindingApplyCapability ?? throw new ArgumentNullException(nameof(bindingApplyCapability));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<DesktopBindingIssuanceOutcome> IssueAsync(
        BrowserTaskBinding binding,
        OperationId operationId,
        TimeSpan? deadline = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(operationId);

        var call = new DesktopCallContext(
            _desktopId,
            operationId,
            _clock.GetUtcNow() + (deadline ?? TimeSpan.FromSeconds(30)),
            correlationId: null,
            browserCaller: binding.Task);

        CapabilityResult<DesktopCapabilityResponse> result;
        try
        {
            result = await _send(_capability, DesktopCapabilityRequest.ForBindingApply(binding), call, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DesktopBindingIssuanceOutcome(false, null, default, $"the desktop call threw {ex.GetType().Name}");
        }

        if (result.IsFailure)
        {
            return new DesktopBindingIssuanceOutcome(false, null, default, result.Error?.Message ?? "the desktop call failed");
        }

        var ack = result.Value.BindingAck;
        if (ack is null)
        {
            return new DesktopBindingIssuanceOutcome(false, null, default, "the desktop replied without a binding ack");
        }

        return new DesktopBindingIssuanceOutcome(ack.Accepted, ack.GrantId, ack.Epoch, ack.Reason);
    }
}