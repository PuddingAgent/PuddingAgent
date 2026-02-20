namespace PuddingCode.Abstractions;

/// <summary>工具输出蒸馏器接口。</summary>
public interface IOutputDistiller
{
    /// <summary>蒸馏原始输出，返回适合传递给 LLM 的精简文本。</summary>
    DistillResult Distill(string rawOutput, DistillContext context);
}

/// <summary>蒸馏的输入上下文。</summary>
public sealed record DistillContext(
    string CommandName,
    int ExitCode,
    string? WorkingDirectory = null);

/// <summary>蒸馏结果。</summary>
public sealed record DistillResult(
    string Summary,
    int OriginalLines,
    int RetainedLines,
    bool IsTruncated,
    string? FullLogPath = null);
