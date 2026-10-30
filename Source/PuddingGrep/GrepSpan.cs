namespace PuddingGrep;

/// <summary>
/// 行内匹配结果的描述。只描述「命中了什么位置」，不含文件与行号 —— 那些属于扫描层。
/// </summary>
/// <param name="Index">命中起点（0 基，按 UTF-16 代码单元计）。</param>
/// <param name="Length">命中长度。</param>
public readonly record struct GrepSpan(int Index, int Length)
{
    /// <summary>命中结束位置（不含）。</summary>
    public int End => Index + Length;
}
