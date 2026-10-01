using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.DesktopService;

/// <summary>
/// 把 <see cref="IDesktopShellFacilities"/>（WinUI 实现）适配成 Desktop 表面的 Shell 部分。
///
/// 适配层只做三件平台无关的事，因而可以脱离 UI 环境测试：
/// ① <b>预算纵深防御</b>：即使设施实现没守预算，剪贴板也会被截断并如实标注；
/// ② <b>异常不越界</b>：设施抛出的非取消异常折叠为 <c>internal_error</c>（取消照常传播）；
/// ③ <b>取消语义归一</b>：不变（取消由结果表达），但确保不会因为适配层把取消变成失败。
/// </summary>
public sealed class DesktopShellSurface
{
    private readonly IDesktopShellFacilities _facilities;

    public DesktopShellSurface(IDesktopShellFacilities facilities)
    {
        _facilities = facilities ?? throw new ArgumentNullException(nameof(facilities));
    }

    public Task<CapabilityResult<DesktopDialogResult>> RequestDialogAsync(
        DesktopCallContext context, DesktopDialogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        return ForwardAsync(
            () => _facilities.RequestDialogAsync(context, request, cancellationToken),
            "dialog");
    }

    public Task<CapabilityResult<DesktopFilePickerResult>> RequestFilePickerAsync(
        DesktopCallContext context, DesktopFilePickerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        return ForwardAsync(
            () => _facilities.RequestFilePickerAsync(context, request, cancellationToken),
            "file_picker");
    }

    public async Task<CapabilityResult<DesktopClipboardContent>> ReadClipboardAsync(
        DesktopCallContext context, ClipboardReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var result = await ForwardAsync(
            () => _facilities.ReadClipboardAsync(context, request, cancellationToken),
            "clipboard").ConfigureAwait(false);

        if (result.IsFailure)
        {
            return result;
        }

        // 纵深防御：设施漏了预算，这里也要收敛（含硬上限）。
        return CapabilityResult<DesktopClipboardContent>.Success(
            DesktopCapabilityBudgets.Apply(result.Value, request.MaxCharacters));
    }

    private static async Task<CapabilityResult<T>> ForwardAsync<T>(
        Func<Task<CapabilityResult<T>>> call, string capability)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消是调用方的意图，必须原样传播（折叠成错误会让取消看起来像失败）。
            throw;
        }
        catch (Exception ex)
        {
            // 只带异常类型名：异常消息可能包含用户内容（路径、剪贴板片段），不得外传。
            return CapabilityResult<T>.Failure(DesktopCapabilityError.Internal(
                $"{capability} facility failed ({ex.GetType().Name})"));
        }
    }
}