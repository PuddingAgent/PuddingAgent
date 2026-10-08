namespace PuddingBrowser.Automation;

/// <summary>
/// **页面脚本对话框**（<c>alert</c>/<c>confirm</c>/<c>prompt</c>）的处理策略
/// （设计方案 §8 的可靠性要求：未处理的模态框会把页面**卡住**，后续动作全部莫名失败）。
///
/// 策略原则（"不阻塞"优先，且**绝不编造用户输入**）：
/// <list type="bullet">
///   <item><c>alert</c>：只有一个 OK ⇒ 接受（没有别的选择，拒绝也会被当成 OK）；</item>
///   <item><c>confirm</c>：**取消**（否定是安全侧：肯定可能触发不可逆操作）；</item>
///   <item><c>prompt</c>：**取消且不填文本**（编一个文本替用户提交是伪造输入）；</item>
///   <item><c>beforeunload</c>：**取消**（不替用户放弃未保存内容）。</item>
/// </list>
///
/// 同时提供**有界且不含敏感内容**的事实摘要：只保留种类、消息长度与截断后的前若干字符，
/// 供回执/诊断说明"刚才有个弹窗被自动处理了"，而不是把整页文本搬进日志。
/// </summary>
public static class ScriptDialogPolicy
{
    /// <summary>摘要里保留的消息字符数上限（够定位问题，又不至于把整页内容搬进日志）。</summary>
    public const int SummaryMessageChars = 120;

    public enum Kind
    {
        Alert,
        Confirm,
        Prompt,
        BeforeUnload,
    }

    /// <summary>对该对话框的处置。</summary>
    public enum Answer
    {
        /// <summary>接受（等于点击 OK/是）。</summary>
        Accept,

        /// <summary>取消（等于点击 取消/否）。</summary>
        Dismiss,
    }

    /// <summary>决定怎么答（见类型注释里的四条原则）。</summary>
    public static Answer Decide(Kind kind) => kind switch
    {
        Kind.Alert => Answer.Accept,
        _ => Answer.Dismiss,
    };

    /// <summary>是否需要把文本回填给页面：**永远不需要**（prompt 也不填）。</summary>
    public static bool ShouldProvideText(Kind kind) => false;

    /// <summary>有界、无控制字符的事实摘要（用于回执/诊断，不用于继续交互）。</summary>
    public static string Describe(Kind kind, string? message, Answer answer)
    {
        var normalized = Normalize(message);
        var truncated = normalized.Length > SummaryMessageChars
            ? normalized[..SummaryMessageChars] + "…"
            : normalized;

        return $"scriptDialog kind={kind.ToString().ToLowerInvariant()} answered={answer.ToString().ToLowerInvariant()}"
            + $" messageChars={normalized.Length}"
            + (truncated.Length == 0 ? string.Empty : $" message={truncated}");
    }

    /// <summary>把消息压成单行、去掉控制字符并裁剪（日志/回执里不该出现换行与控制序列）。</summary>
    public static string Normalize(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(message.Length);
        var lastWasSpace = false;
        foreach (var character in message)
        {
            var isSpace = char.IsWhiteSpace(character) || char.IsControl(character);
            if (isSpace)
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }
}
