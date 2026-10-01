using Microsoft.UI.Dispatching;
using Pudding.Contracts.Desktop;

namespace PuddingDesktop.CapabilityHost;

/// <summary>
/// 把桌面能力动作调度到 WinUI UI 线程（计划 §6：所有触及 WebView2/窗口/剪贴板/Picker 的动作
/// 最终进入所属窗口的 DispatcherQueue）。
///
/// 契约（与 <see cref="IDesktopUiDispatcher"/> 一致，并与仓库既有 WinUiDispatcher/WpfUiDispatcher 同形）：
/// · 已在 UI 线程时同步执行，不额外排队；
/// · 入队失败（窗口销毁/队列关闭）必须让任务以异常结束 —— <b>绝不悬挂</b>；
/// · 回调内的取消与异常必须传播到返回的 Task。
///
/// 本类没有可独立单测的逻辑（需要真实 DispatcherQueue）：其契约由 <c>Pudding.DesktopServiceTests</c>
/// 用假调度器验证的「拒绝入队 ⇒ ui_unavailable、窗口退出 ⇒ 任务完成」覆盖，本类只保证编译期符合接缝。
/// </summary>
public sealed class WinUiDesktopUiDispatcher : IDesktopUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public WinUiDesktopUiDispatcher(DispatcherQueue queue) =>
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));

    public bool HasThreadAccess => _queue.HasThreadAccess;

    public Task InvokeAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) =>
        InvokeAsync(
            async token =>
            {
                await action(token).ConfigureAwait(true);
                return true;
            },
            cancellationToken);

    public Task<T> InvokeAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        if (_queue.HasThreadAccess)
        {
            return action(cancellationToken);
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var enqueued = _queue.TryEnqueue(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(await action(cancellationToken).ConfigureAwait(true));
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        if (!enqueued)
        {
            // 窗口正在销毁：让服务侧把它折叠成 ui_unavailable，而不是让调用方永久等待。
            completion.TrySetException(
                new InvalidOperationException("DispatcherQueue rejected the callback; the window is shutting down."));
        }

        return completion.Task;
    }
}
