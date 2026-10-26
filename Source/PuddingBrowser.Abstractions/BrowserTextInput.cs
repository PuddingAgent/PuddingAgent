namespace PuddingBrowser.Abstractions;

/// <summary>
/// 文本输入的**形态判定**：哪些文本没有物理键位、只能靠「剪贴板 + Ctrl+V」进入页面。
///
/// 为什么需要这个判定：CDP 的 <c>Input.dispatchKeyEvent</c> 只能表达**物理键**。
/// 中文/emoji/全角标点没有键位，以前我们用「只带 text 的 keyDown」凑，
/// 结果 DOM 里字进去了、Draft.js/React 这类受控编辑器的 EditorState 仍是空的
/// （2026-10-26 抖音评论框实测：发送按钮不出现、回车不提交）。
/// 真正的键盘路径只有两条：IME 组合事件，或剪贴板粘贴 —— 后者对任意文本都成立。
///
/// 放在 Abstractions 里，是因为驱动面（只允许引用 Abstractions）和测试面都要用同一份判据。
/// </summary>
public static class BrowserTextInput
{
    /// <summary>
    /// 可打印 ASCII（0x20–0x7E）之外都算「没有物理键位」。
    /// 换行/制表符同样没有对应可打印键，走粘贴更安全。
    /// </summary>
    public static bool RequiresClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var character in text)
        {
            if (character is < (char)0x20 or > (char)0x7E)
            {
                return true;
            }
        }

        return false;
    }
}
