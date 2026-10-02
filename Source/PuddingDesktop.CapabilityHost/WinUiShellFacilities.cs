using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PuddingDesktop.CapabilityHost;

/// <summary>
/// <see cref="IDesktopShellFacilities"/> 的 WinUI 实现：对话框 / 文件选择器 / 剪贴板。
///
/// 这是整个能力通道里**唯一必须依赖 Windows App SDK** 的部分；预算收敛、取消语义归一、
/// 异常→错误映射都在平台无关的 `DesktopShellSurface` 完成，因此这里只做三件直接调用。
///
/// 三条已确认的 WinUI 陷阱（规格 §6.2）：
/// ① <see cref="ContentDialog"/> **必须**设置 <see cref="XamlRoot"/>，否则抛异常；
/// ② 文件选择器在非打包（unpackaged）应用里**必须**先 <c>InitializeWithWindow</c> 传窗口句柄，否则 COMException；
/// ③ 剪贴板非文本内容不是错误——返回"无文本"的**成功**结果，且内容绝不写日志。
/// </summary>
public sealed class WinUiShellFacilities : IDesktopShellFacilities
{
    private readonly Func<nint> _windowHandle;
    private readonly Func<XamlRoot?> _xamlRoot;

    /// <param name="windowHandle">主窗口句柄（文件选择器需要）。</param>
    /// <param name="xamlRoot">主窗口内容根（对话框需要）；窗口未就绪时返回 <c>null</c>。</param>
    public WinUiShellFacilities(Func<nint> windowHandle, Func<XamlRoot?> xamlRoot)
    {
        _windowHandle = windowHandle ?? throw new ArgumentNullException(nameof(windowHandle));
        _xamlRoot = xamlRoot ?? throw new ArgumentNullException(nameof(xamlRoot));
    }

    public async Task<CapabilityResult<DesktopDialogResult>> RequestDialogAsync(
        DesktopCallContext context, DesktopDialogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var root = _xamlRoot();
        if (root is null)
        {
            // 窗口还没就绪：如实报告不可用，不阻塞也不假装成功。
            return CapabilityResult<DesktopDialogResult>.Failure(
                DesktopCapabilityError.UiUnavailable("the main window is not ready for dialogs"));
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = request.Title,
            Content = request.Message,
        };

        ApplyButtons(dialog, request.Buttons);

        cancellationToken.ThrowIfCancellationRequested();
        var result = await dialog.ShowAsync();

        var choice = result switch
        {
            ContentDialogResult.Primary => PrimaryChoice(request.Buttons),
            ContentDialogResult.Secondary => SecondaryChoice(request.Buttons),
            // None = 用户按 ESC / 点击遮罩关闭：**取消是结果而不是失败**。
            _ => DesktopDialogChoice.Cancel,
        };

        return CapabilityResult<DesktopDialogResult>.Success(new DesktopDialogResult(choice));
    }

    public async Task<CapabilityResult<DesktopFilePickerResult>> RequestFilePickerAsync(
        DesktopCallContext context, DesktopFilePickerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var picker = new FileOpenPicker();

        // ② 非打包应用必须显式绑定窗口句柄，否则 PickXxxAsync 抛 COMException。
        InitializeWithWindow.Initialize(picker, _windowHandle());

        foreach (var extension in request.Extensions)
        {
            picker.FileTypeFilter.Add("." + extension);
        }

        if (picker.FileTypeFilter.Count == 0)
        {
            picker.FileTypeFilter.Add("*");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (request.AllowMultiple)
        {
            var files = await picker.PickMultipleFilesAsync();
            return files is null || files.Count == 0
                ? CapabilityResult<DesktopFilePickerResult>.Success(new DesktopFilePickerResult(canceled: true))
                : CapabilityResult<DesktopFilePickerResult>.Success(
                    new DesktopFilePickerResult(canceled: false, [.. files.Select(file => file.Path)]));
        }

        var single = await picker.PickSingleFileAsync();
        return single is null
            // 用户取消：结果是 Canceled，而不是失败。
            ? CapabilityResult<DesktopFilePickerResult>.Success(new DesktopFilePickerResult(canceled: true))
            : CapabilityResult<DesktopFilePickerResult>.Success(
                new DesktopFilePickerResult(canceled: false, [single.Path]));
    }

    public async Task<CapabilityResult<DesktopClipboardContent>> ReadClipboardAsync(
        DesktopCallContext context, ClipboardReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text))
        {
            // ③ 非文本内容 = "没有文本"的成功结果，不是错误。
            return CapabilityResult<DesktopClipboardContent>.Success(new DesktopClipboardContent(null, truncated: false));
        }

        var text = await content.GetTextAsync();

        // 预算由 DesktopShellSurface 统一收敛（这里不重复实现，避免两处口径漂移）。
        return CapabilityResult<DesktopClipboardContent>.Success(
            new DesktopClipboardContent(string.IsNullOrEmpty(text) ? null : text, truncated: false));
    }

    private static void ApplyButtons(ContentDialog dialog, DesktopDialogButtons buttons)
    {
        switch (buttons)
        {
            case DesktopDialogButtons.Ok:
                dialog.PrimaryButtonText = "确定";
                dialog.DefaultButton = ContentDialogButton.Primary;
                break;

            case DesktopDialogButtons.OkCancel:
                dialog.PrimaryButtonText = "确定";
                dialog.CloseButtonText = "取消";
                dialog.DefaultButton = ContentDialogButton.Primary;
                break;

            case DesktopDialogButtons.YesNo:
                dialog.PrimaryButtonText = "是";
                dialog.CloseButtonText = "否";
                dialog.DefaultButton = ContentDialogButton.Primary;
                break;

            case DesktopDialogButtons.YesNoCancel:
                dialog.PrimaryButtonText = "是";
                dialog.SecondaryButtonText = "否";
                dialog.CloseButtonText = "取消";
                dialog.DefaultButton = ContentDialogButton.Primary;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(buttons), buttons, "Dialog button set is not registered.");
        }
    }

    private static DesktopDialogChoice PrimaryChoice(DesktopDialogButtons buttons) => buttons switch
    {
        DesktopDialogButtons.Ok or DesktopDialogButtons.OkCancel => DesktopDialogChoice.Ok,
        DesktopDialogButtons.YesNo or DesktopDialogButtons.YesNoCancel => DesktopDialogChoice.Yes,
        _ => DesktopDialogChoice.Cancel,
    };

    private static DesktopDialogChoice SecondaryChoice(DesktopDialogButtons buttons) =>
        // 只有 YesNoCancel 用到 Secondary（"否"）；其余组合没有 Secondary 按钮。
        buttons == DesktopDialogButtons.YesNoCancel ? DesktopDialogChoice.No : DesktopDialogChoice.Cancel;
}
