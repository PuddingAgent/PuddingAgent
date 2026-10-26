namespace PuddingBrowser.WebView2;

/// <summary>
/// 系统剪贴板写入（**必须在 UI 线程调用**，由 <see cref="IWebView2UiDispatcher"/> 保证）。
///
/// 为什么驱动层要碰 OS 剪贴板：没有物理键位的文本（中文/emoji）要进入受控编辑器，
/// 唯一可靠的真实输入路径是「粘贴」。而剪贴板是 OS 资源，只能由宿主面提供 ——
/// WinUI 走 <c>Windows.ApplicationModel.DataTransfer</c>，WPF 走 <c>System.Windows.Clipboard</c>。
/// 两者都在本文件里按编译面分派，页面逻辑保持单一实现（不再各写一套）。
/// </summary>
internal static class BrowserClipboard
{
    public static void SetText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

#if WINUI_BROWSER
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
#else
        System.Windows.Clipboard.SetText(text);
#endif
    }
}
