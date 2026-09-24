namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 证据位置（ADR-089 §3 / §8.2）：<b>每条命中都必须能回答"证据在哪"</b>（文件 + 行号），
/// 否则"地图"会变成猜谜。
/// <para>路径与行号是必填的 —— 没有证据的命中在结构上不可表示。</para>
/// </summary>
public sealed record RetrievalEvidence
{
    /// <summary>构造证据位置。文件路径非空、行号 ≥ 1 为硬要求。</summary>
    /// <param name="filePath">文件路径（相对或绝对；内部会归一化为 <c>/</c> 分隔）。</param>
    /// <param name="line">1 基行号。</param>
    /// <param name="endLine">可选的结束行号（≥ <paramref name="line"/>）。</param>
    /// <param name="snippet">可选片段（命中处的原文，供人读；不参与去重键）。</param>
    public RetrievalEvidence(string filePath, int line, int? endLine = null, string? snippet = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("证据必须给出文件路径（不得为空）。", nameof(filePath));

        if (line < 1)
            throw new ArgumentOutOfRangeException(nameof(line), line, "证据行号必须 ≥ 1（1 基）。");

        if (endLine is { } end && end < line)
            throw new ArgumentOutOfRangeException(nameof(endLine), end, "结束行号不得小于起始行号。");

        FilePath = filePath.Trim();
        Line = line;
        EndLine = endLine;
        Snippet = string.IsNullOrWhiteSpace(snippet) ? null : snippet;
    }

    /// <summary>文件路径（原样保留，另见 <see cref="NormalizedFilePath"/>）。</summary>
    public string FilePath { get; }

    /// <summary>1 基行号。</summary>
    public int Line { get; }

    /// <summary>可选结束行号。</summary>
    public int? EndLine { get; }

    /// <summary>可选片段。</summary>
    public string? Snippet { get; }

    /// <summary>归一化路径（<c>\</c> → <c>/</c>、去首尾空白）——用于确定性排序 Key。</summary>
    public string NormalizedFilePath => SymbolIdentity.NormalizePath(FilePath);

    /// <summary>人类可读的"文件:行"。</summary>
    public string Display =>
        EndLine is { } end && end > Line
            ? $"{FilePath}:{Line}-{end}"
            : $"{FilePath}:{Line}";
}
