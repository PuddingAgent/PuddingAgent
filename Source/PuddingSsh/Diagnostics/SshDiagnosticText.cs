using System.Text;

namespace PuddingSsh.Diagnostics;

/// <summary>
/// 公开诊断文本的有界化（ADR-096 D5）：**不得**把第三方异常 Message、known_hosts 整行或私钥路径
/// 直接放进公开 <c>Diagnostic</c>；所有诊断字段都有长度上限。
/// </summary>
public static class SshDiagnosticText
{
    /// <summary>公开诊断字段的长度上限。</summary>
    public const int MaxLength = 512;

    private const string TruncationMarker = "…(truncated)";

    /// <summary>截断到 <see cref="MaxLength"/>，并显式标记截断。</summary>
    public static string? Bound(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var trimmed = text.Trim();
        if (trimmed.Length <= MaxLength)
        {
            return trimmed;
        }

        var keep = MaxLength - TruncationMarker.Length;
        return string.Concat(trimmed.AsSpan(0, keep), TruncationMarker);
    }

    /// <summary>
    /// 由异常得到**有界且不含原始载荷**的诊断：只保留异常类型名与稳定的因果类别，
    /// 不携带 <see cref="Exception.Message"/>（可能含第三方内部细节、路径、命令文本）。
    /// </summary>
    public static string FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Bound("exception=" + exception.GetType().Name) ?? "exception";
    }

    /// <summary>把若干诊断片段拼成一条有界文本。</summary>
    public static string Join(params string?[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('｜');
            }

            builder.Append(part.Trim());
        }

        return Bound(builder.ToString()) ?? string.Empty;
    }
}
