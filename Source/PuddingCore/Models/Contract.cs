namespace PuddingCode.Models;

/// <summary>Leader 生成的契约定义。</summary>
public sealed record Contract
{
    /// <summary>契约唯一标识符。</summary>
    public required string Id { get; init; }

    /// <summary>契约涉及的文件路径列表。</summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>契约涉及的符号（类名。方法名）列表。</summary>
    public required IReadOnlyList<string> Symbols { get; init; }

    /// <summary>契约描述（自然语言 + 约束条件）。</summary>
    public required string Specification { get; init; }
}
