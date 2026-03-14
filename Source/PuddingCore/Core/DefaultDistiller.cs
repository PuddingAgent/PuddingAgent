using PuddingCode.Abstractions;

namespace PuddingCode.Core;

/// <summary>
/// 默认输出蒸馏器：物理截断 + 错误增强模式。
/// 短输出直接透传；长输出保留首尾截断中间；
/// 失败时提取错误行及其上下文。
/// </summary>
public sealed class DefaultDistiller : IOutputDistiller
{
    private readonly DistillerConfig _config;

    public DefaultDistiller(DistillerConfig? config = null)
        => _config = config ?? DistillerConfig.Default;

    public DistillResult Distill(string rawOutput, DistillContext context)
    {
        if (string.IsNullOrEmpty(rawOutput))
            return new("(no output)", 0, 0, false);

        var lines = rawOutput.Split('\n');
        var totalLines = lines.Length;

        // 直接透传
        if (totalLines <= _config.PassthroughLimit)
            return new(rawOutput, totalLines, totalLines, false);

        // 失败时：增强模式
        if (context.ExitCode != 0)
            return DistillWithErrorEnhancement(lines, context);

        // 正常截断
        return TruncateHeadTail(lines, totalLines);
    }

    private DistillResult TruncateHeadTail(string[] lines, int total)
    {
        var head = lines.Take(_config.HeaderSize);
        var tail = lines.TakeLast(_config.FooterSize);
        var omitted = total - _config.HeaderSize - _config.FooterSize;

        var summary =
            $"{string.Join('\n', head)}\n" +
            $"\n... [{omitted} lines truncated] ...\n\n" +
            string.Join('\n', tail);

        return new(summary, total, _config.HeaderSize + _config.FooterSize, true);
    }

    private DistillResult DistillWithErrorEnhancement(string[] lines, DistillContext context)
    {
        var errors = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (!ContainsErrorKeyword(lines[i])) continue;

            // 提取错误行及上下文（前后各 3 行）
            var start = Math.Max(0, i - 3);
            var end = Math.Min(lines.Length - 1, i + 3);
            var snippet = string.Join('\n',
                lines[start..(end + 1)]
                    .Select((l, idx) => idx + start == i ? $"> {l}" : $"  {l}"));
            errors.Add(snippet);

            if (errors.Count >= _config.MaxErrorSnippets) break;
        }

        string summary;

        if (errors.Count > 0)
        {
            summary =
                $"[FAILED] Exit code: {context.ExitCode}. " +
                $"Found {errors.Count} error(s) in {lines.Length} lines.\n\n" +
                string.Join("\n---\n", errors);
        }
        else
        {
            // Failed but no error keywords found — use head+tail
            var fallback = TruncateHeadTail(lines, lines.Length);
            summary = $"[FAILED] Exit code: {context.ExitCode}. {lines.Length} lines of output.\n\n" +
                       fallback.Summary;
            return new(TruncateToLimit(summary), lines.Length, fallback.RetainedLines, true);
        }

        return new(TruncateToLimit(summary), lines.Length, errors.Count * 7, true);
    }

    private string TruncateToLimit(string text) =>
        text.Length > _config.MaxLlmChars
            ? text[.._config.MaxLlmChars] + "\n[...truncated]"
            : text;

    private static bool ContainsErrorKeyword(string line) =>
        line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("FATAL", StringComparison.Ordinal);
}

/// <summary>蒸馏器配置。</summary>
public sealed record DistillerConfig
{
    /// <summary>行数 ≤ 此值时直接透传。</summary>
    public int PassthroughLimit { get; init; } = 20;

    /// <summary>截断时保留的头部行数。</summary>
    public int HeaderSize { get; init; } = 5;

    /// <summary>截断时保留的尾部行数。</summary>
    public int FooterSize { get; init; } = 10;

    /// <summary>返回给 LLM 的最大字符数。</summary>
    public int MaxLlmChars { get; init; } = 4000;

    /// <summary>错误增强模式最多提取的错误片段数。</summary>
    public int MaxErrorSnippets { get; init; } = 5;

    public static DistillerConfig Default => new();
}
