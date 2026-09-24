using System.Text;

namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 跨 scope 去重键（ADR-089 §2.4）：<b>规范化 symbol_id + 文件 + 行</b>。
/// <para>
/// 实测动机（§2.4 证据）：仓库根与三个子目录被各自注册为项目
/// （<c>b375fee0…</c> / <c>scope-0ca100c528ef</c> / <c>scope-ee887ff5297f</c> / <c>scope-6526fb344e33</c>），
/// 同一符号在 3 个 project_id 下各出现一次 —— 40 条命中里真正不同的符号远少于此。
/// 这类噪音<b>加任何过滤条件都消不掉</b>，只能按符号身份去重；且必须先于过载判定，
/// 否则会把"索引结构性膨胀"误判成"查询过泛"。
/// </para>
/// <para>
/// 归一化在**构造时**完成（<see cref="Create"/> / <see cref="ForTextAt"/>），
/// 因此 record 的取值相等就是身份相等，可以直接当字典键用 —— 没有"忘了归一化"的中间态。
/// </para>
/// </summary>
public sealed record SymbolIdentity
{
    private SymbolIdentity(string? symbolId, string normalizedFilePath, int line)
    {
        SymbolId = symbolId;
        NormalizedFilePath = normalizedFilePath;
        Line = line;
    }

    /// <summary>规范化后的 symbol_id；纯文本命中（不归属符号）为 <c>null</c>。</summary>
    public string? SymbolId { get; }

    /// <summary>规范化文件路径（<c>\</c> → <c>/</c>、折叠重复分隔符、小写）。</summary>
    public string NormalizedFilePath { get; }

    /// <summary>1 基行号。</summary>
    public int Line { get; }

    /// <summary>是否归属某个符号（文本命中为 false）。</summary>
    public bool HasSymbol => SymbolId is not null;

    /// <summary>稳定的去重/排序键（确定性排序的末级 tie-break 用它）。</summary>
    public string Key => $"{SymbolId ?? string.Empty}|{NormalizedFilePath}|{Line}";

    /// <summary>符号身份：symbol_id 非空、文件路径非空、行号 ≥ 1。</summary>
    public static SymbolIdentity Create(string symbolId, string filePath, int line) =>
        new(Normalize(symbolId), NormalizePath(filePath), RequireLine(line));

    /// <summary>纯文本命中的身份（没有归属符号，但有证据位置）。</summary>
    public static SymbolIdentity ForTextAt(string filePath, int line) =>
        new(null, NormalizePath(filePath), RequireLine(line));

    /// <summary>规范化 symbol_id：去首尾空白 + 内部连续空白折叠为单空格。</summary>
    public static string Normalize(string symbolId)
    {
        if (string.IsNullOrWhiteSpace(symbolId))
            throw new ArgumentException("符号身份必须有非空 symbol_id（缺失请改用 ForTextAt）。", nameof(symbolId));

        var builder = new StringBuilder(symbolId.Length);
        var pendingSpace = false;
        foreach (var ch in symbolId.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 规范化文件路径：<c>\</c> → <c>/</c>、折叠重复分隔符、去首尾空白、小写化。
    /// 小写化与既有 <c>CodePathIdentity</c> 的"Windows 路径大小写不敏感"取舍一致。
    /// </summary>
    public static string NormalizePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("符号身份必须有非空文件路径。", nameof(filePath));

        var builder = new StringBuilder(filePath.Length);
        var previousWasSeparator = false;
        foreach (var ch in filePath.Trim())
        {
            var c = ch == '\\' ? '/' : ch;
            if (c == '/')
            {
                if (previousWasSeparator)
                    continue;

                previousWasSeparator = true;
            }
            else
            {
                previousWasSeparator = false;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private static int RequireLine(int line) =>
        line < 1
            ? throw new ArgumentOutOfRangeException(nameof(line), line, "身份行号必须 ≥ 1（1 基）。")
            : line;
}
