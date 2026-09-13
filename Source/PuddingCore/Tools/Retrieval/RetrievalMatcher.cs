// ADR-089 U0-S1：统一检索匹配器（纯新增，未接入任何既有工具）。
// 目标：为 Lucene 命中复核与托管扫描提供同一套 literal/regex + case 匹配合同，
// 消除"分词检索假阳性 / 非法正则静默降级"两类缺陷在后续切片（U0-S2/S3）中的复制。
using System.Text.RegularExpressions;
using System.Threading;

namespace PuddingCore.Tools.Retrieval;

/// <summary>
/// 统一匹配器：按同一合同对单行文本做 literal/regex 匹配复核。
/// <list type="bullet">
/// <item>literal 使用 <see cref="StringComparison.Ordinal"/> / <see cref="StringComparison.OrdinalIgnoreCase"/>，
///       大小写折叠不依赖当前 culture（禁止 culture 敏感比较）。</item>
/// <item>regex 使用 <see cref="Regex"/>（caseMode == Insensitive 时附加 IgnoreCase），
///       超时值必须由调用方经 <paramref name="regexTimeout"/> 传入，本类型不内置常量。</item>
/// <item>非法正则在 <see cref="TryCreate"/> 即失败并返回含原始 pattern 的可读 contractError，
///       绝不静默降级为 literal。</item>
/// <item><see cref="IsMatch"/> 在正则求值超时（<see cref="RegexMatchTimeoutException"/>）时返回 false 且不抛出；
///       超时与否由调用方通过 <see cref="RetrievalCoverageStatus.Timeout"/> 表达，本类型不吞掉其它异常。</item>
/// </list>
/// </summary>
public sealed class RetrievalMatcher
{
    private readonly string _query;
    private readonly StringComparison _comparison;
    private readonly Regex? _regex;

    private RetrievalMatcher(
        string query,
        StringComparison comparison,
        RetrievalMatchMode mode,
        RetrievalCaseMode caseMode,
        Regex? regex)
    {
        _query = query;
        _comparison = comparison;
        _regex = regex;
        Mode = mode;
        CaseMode = caseMode;
    }

    /// <summary>匹配模式。</summary>
    public RetrievalMatchMode Mode { get; }

    /// <summary>大小写模式。</summary>
    public RetrievalCaseMode CaseMode { get; }

    /// <summary>
    /// 创建匹配器。失败时 <paramref name="matcher"/> 为 null，且 <paramref name="contractError"/>
    /// 给出可读原因（非法正则时包含原始 pattern）。
    /// </summary>
    public static bool TryCreate(
        RetrievalMatchMode mode,
        RetrievalCaseMode caseMode,
        string query,
        TimeSpan regexTimeout,
        out RetrievalMatcher? matcher,
        out string? contractError)
    {
        matcher = null;
        contractError = null;

        if (mode is not (RetrievalMatchMode.Literal or RetrievalMatchMode.Regex))
        {
            contractError = $"unsupported match mode: {mode}";
            return false;
        }

        if (caseMode is not (RetrievalCaseMode.Sensitive or RetrievalCaseMode.Insensitive))
        {
            contractError = $"unsupported case mode: {caseMode}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            contractError = "query is required";
            return false;
        }

        if (mode == RetrievalMatchMode.Regex)
        {
            if (regexTimeout <= TimeSpan.Zero && regexTimeout != Timeout.InfiniteTimeSpan)
            {
                contractError = $"regexTimeout must be positive: {regexTimeout}";
                return false;
            }

            try
            {
                var options = caseMode == RetrievalCaseMode.Insensitive
                    ? RegexOptions.IgnoreCase
                    : RegexOptions.None;
                var regex = new Regex(query, options, regexTimeout);
                matcher = new RetrievalMatcher(query, StringComparison.Ordinal, mode, caseMode, regex);
            }
            catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
            {
                contractError = $"invalid regex pattern '{query}': {ex.Message}";
                return false;
            }
        }
        else
        {
            var comparison = caseMode == RetrievalCaseMode.Insensitive
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            matcher = new RetrievalMatcher(query, comparison, mode, caseMode, regex: null);
        }

        return true;
    }

    /// <summary>对单行文本求值。line 为 null 时返回 false；正则求值超时返回 false 且不抛出。</summary>
    public bool IsMatch(string line)
    {
        if (line is null)
        {
            return false;
        }

        if (_regex is not null)
        {
            try
            {
                return _regex.IsMatch(line);
            }
            catch (RegexMatchTimeoutException)
            {
                // 超时属于覆盖状态（Timeout）的表达范畴：本层返回 false 且不抛出；其它异常照常上抛。
                return false;
            }
        }

        return line.Contains(_query, _comparison);
    }
}
