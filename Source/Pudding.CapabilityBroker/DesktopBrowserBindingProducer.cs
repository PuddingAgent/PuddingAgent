using System.Collections.Concurrent;
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
/// 授权判断不在这里（归 Desktop 唯一 authority）；本类保证**如实传递**与**同一 (任务, 目标) 不并发重发**。
/// </summary>
public sealed class DesktopBrowserBindingProducer(
    DesktopCapabilitySender send,
    DesktopInstanceId desktopId,
    DesktopCapabilityDescriptor bindingApplyCapability,
    TimeProvider? clock = null)
{
    private readonly DesktopCapabilitySender _send = send ?? throw new ArgumentNullException(nameof(send));
    private readonly DesktopInstanceId _desktopId = desktopId ?? throw new ArgumentNullException(nameof(desktopId));
    private readonly DesktopCapabilityDescriptor _capability =
        bindingApplyCapability ?? throw new ArgumentNullException(nameof(bindingApplyCapability));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// 同一 (任务, 目标) 在途时，后来者**加入同一次**申请。`Lazy` 默认的 ExecutionAndPublication 保证工厂只跑一次；
    /// 幂等键与 Desktop 受理账本一致（Desktop 也按 (任务, 目标) 记账）。
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<DesktopBindingIssuanceOutcome>>> _inFlight =
        new(StringComparer.Ordinal);

    public Task<DesktopBindingIssuanceOutcome> IssueAsync(
        BrowserTaskBinding binding,
        OperationId operationId,
        TimeSpan? deadline = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(operationId);

        var lazy = _inFlight.GetOrAdd(
            Key(binding),
            _ => new Lazy<Task<DesktopBindingIssuanceOutcome>>(
                () => IssueCoreAsync(binding, operationId, deadline, cancellationToken)));

        return lazy.Value;
    }

    private async Task<DesktopBindingIssuanceOutcome> IssueCoreAsync(
        BrowserTaskBinding binding,
        OperationId operationId,
        TimeSpan? deadline,
        CancellationToken cancellationToken)
    {
        try
        {
            var call = new DesktopCallContext(
                _desktopId,
                operationId,
                _clock.GetUtcNow() + (deadline ?? TimeSpan.FromSeconds(30)),
                correlationId: null,
                browserCaller: binding.Task);

            CapabilityResult<DesktopCapabilityResponse> result;
            try
            {
                result = await _send(_capability, DesktopCapabilityRequest.ForBindingApply(binding), call, cancellationToken)
                    .ConfigureAwait(false);
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

            // 拒绝**不自动重发**：授权被拒是权威判断，重试必须由调用方显式发起。
            return new DesktopBindingIssuanceOutcome(ack.Accepted, ack.GrantId, ack.Epoch, ack.Reason);
        }
        finally
        {
            // 记账只在**本次申请真正结束**时清理（无论成功、失败还是异常）。
            _inFlight.TryRemove(Key(binding), out _);
        }
    }

    private static string Key(BrowserTaskBinding binding) =>
        $"{binding.Target.ContextId}/{binding.Target.PageId}|{binding.Task.CallerId}|{binding.Task.TaskId}|{binding.Task.ParentTaskId}";
}