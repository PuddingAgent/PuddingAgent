namespace Pudding.Contracts.Desktop;

/// <summary>对话框按钮组合（与用户可见语义一一对应，不提供自定义按钮避免语义漂移）。</summary>
public enum DesktopDialogButtons
{
    Ok,
    OkCancel,
    YesNo,
    YesNoCancel,
}

/// <summary>用户的选择结果。</summary>
public enum DesktopDialogChoice
{
    Ok,
    Cancel,
    Yes,
    No,
}

/// <summary>
/// 对话框请求：标题/正文/按钮组合。
/// 正文可能含由 Core 生成的提示，因此长度受上限约束（避免把大段内容塞进 UI 弹窗）。
/// </summary>
public sealed record DesktopDialogRequest
{
    public const int MaxTitleLength = 128;

    public const int MaxMessageLength = 4096;

    public DesktopDialogRequest(
        string title,
        string message,
        DesktopDialogButtons buttons = DesktopDialogButtons.Ok)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > MaxTitleLength)
        {
            throw new ArgumentException($"Title must be 1..{MaxTitleLength} characters.", nameof(title));
        }

        if (string.IsNullOrWhiteSpace(message) || message.Length > MaxMessageLength)
        {
            throw new ArgumentException($"Message must be 1..{MaxMessageLength} characters.", nameof(message));
        }

        if (!Enum.IsDefined(buttons))
        {
            throw new ArgumentOutOfRangeException(nameof(buttons), buttons, "Button set is not registered.");
        }

        Title = title;
        Message = message;
        Buttons = buttons;
    }

    public string Title { get; }

    public string Message { get; }

    public DesktopDialogButtons Buttons { get; }

    /// <summary>该按钮组合是否允许取消（只有 Ok 是"必须选一个"，其余都能取消）。</summary>
    public bool AllowsCancel => Buttons != DesktopDialogButtons.Ok;

    public override string ToString() => $"dialog({Buttons}, titleChars={Title.Length}, messageChars={Message.Length})";
}

/// <summary>
/// 对话框结果。
///
/// <b>语义要点</b>：<b>用户取消不是失败</b>——它用 <see cref="Canceled"/> 如实表达，
/// 而不是伪装成 <c>CapabilityResult.Failure</c>。把它当错误会让上层做出"重试弹窗"这种骚扰用户的行为。
/// </summary>
public sealed record DesktopDialogResult
{
    public DesktopDialogResult(DesktopDialogChoice choice)
    {
        if (!Enum.IsDefined(choice))
        {
            throw new ArgumentOutOfRangeException(nameof(choice), choice, "Dialog choice is not registered.");
        }

        Choice = choice;
    }

    public DesktopDialogChoice Choice { get; }

    /// <summary>用户是否取消了本次交互。</summary>
    public bool Canceled => Choice == DesktopDialogChoice.Cancel;

    /// <summary>是否需要执行后续动作（只有肯定性选择才为真）。</summary>
    public bool IsAffirmative => Choice is DesktopDialogChoice.Ok or DesktopDialogChoice.Yes;

    public override string ToString() => $"dialog({Choice}{(Canceled ? ", canceled" : string.Empty)})";
}
