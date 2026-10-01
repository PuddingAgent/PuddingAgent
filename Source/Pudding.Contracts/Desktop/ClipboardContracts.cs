namespace Pudding.Contracts.Desktop;

/// <summary>
/// 剪贴板**读取**请求（v1 只有读取；写入属后续切片，届时需要新的能力名或版本升级）。
/// 预算由调用方给出：剪贴板可能包含极长文本甚至凭据，绝不能无界回传。
/// </summary>
public sealed record ClipboardReadRequest
{
    public const int DefaultMaxCharacters = 64_000;

    public const int MaxMaxCharacters = 1_000_000;

    public ClipboardReadRequest(int maxCharacters = DefaultMaxCharacters)
    {
        if (maxCharacters is < 1 or > MaxMaxCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCharacters), maxCharacters, $"Clipboard budget must be in [1, {MaxMaxCharacters}].");
        }

        MaxCharacters = maxCharacters;
    }

    public int MaxCharacters { get; }
}

/// <summary>
/// 剪贴板内容（只读结果）。
/// <b>不变量</b>：截断必须如实标注；<see cref="Text"/> 绝不进入日志或审计（调用方自己负责不外传）。
/// </summary>
public sealed record DesktopClipboardContent
{
    public DesktopClipboardContent(string? text, bool truncated)
    {
        if (truncated && string.IsNullOrEmpty(text))
        {
            // 「截断了但没有内容」是自相矛盾的结果：宁可判为无内容，也不返回含糊状态。
            text = null;
            truncated = false;
        }

        Text = text;
        Truncated = truncated;
    }

    public string? Text { get; }

    /// <summary>是否因预算被截断（如实标注，不假装完整）。</summary>
    public bool Truncated { get; }

    public bool HasText => !string.IsNullOrEmpty(Text);

    public int Length => Text?.Length ?? 0;

    /// <summary>刻意不暴露内容：ToString 只给形状，避免被顺手写进日志。</summary>
    public override string ToString() => $"clipboard(chars={Length}, truncated={Truncated})";
}
