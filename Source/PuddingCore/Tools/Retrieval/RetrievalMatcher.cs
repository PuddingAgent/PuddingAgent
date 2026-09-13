// ADR-089 U0-S1：统一检索匹配器（纯新增，未接入任何既有工具）。
// 目标：为 Lucene 命中复核与托管扫描提供同一套 literal/regex + case 匹配合同，
// 消除"分词检索假阳性 / 非法正则静默降级"两类缺陷在后续切片（U0-S2/S3）中的复制。
using System.Text.RegularExpressions;
using System.Threading;

namespace PuddingCode.Tools.Retrieval;

/// <summary>
/// 统一匹配器：按同一合同对单行文本做 literal/regex 匹配复核。
/// <list type="bullet">
/// <item>literal 使用 <see cref="StringComparison.Ordinal"/> / <see cref="StringComparison.OrdinalIgnoreCase"/>，
///       大小写折叠不依赖当前 culture（禁止 culture 敏感比较）。</item>
/// <item>regex 使用 <see cref="Regex"/>（caseMode == Insensitive 时附加 IgnoreCase），
///       超时值必须由调用方经 <paramref name="regexTimeout"/> 传入，本类型不内置常量。</item>
/// <item>非法正则在 <see cref="TryCreate"/> 即失败并返回含原始 pattern 的可读 contractError，
///       绝不静默降级为 literal。</item>
/// <item>调用方必须使用 <see cref="TryMatch"/> 区分「不匹配」与「正则求值超时（未能判定）」：
///       <see cref="TryMatch"/> 在 RegexMatchTimeoutException 时返回 <see cref="RetrievalMatchOutcome.Timeout"/>，
///       在取消令牌已触发时抛出 OperationCanceledException，且不吞掉其它异常。</item>
/// <item><see cref="IsMatch"/> 仅为兼容层保留（等价于 TryMatch == Match；正则超时返回 false，
///       与「不匹配」不可区分），正确性敏感路径禁止使用。</item>
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
                // CultureInvariant 必加：literal 走 Ordinal/OrdinalIgnoreCase（culture 无关），
                // 正则若不抑制 culture 折叠（如土耳其语 i），两条路径会对同一输入给出不同结论。
                var options = caseMode == RetrievalCaseMode.Insensitive
                    ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                    : RegexOptions.CultureInvariant;
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

    /// <summary>
    /// 类型化单行求值（ADR-089 U0 R2）：区分「不匹配」与「正则求值超时（未能判定）」。
    /// line 为 null → NoMatch；<paramref name="ct"/> 已取消 → 抛 <see cref="OperationCanceledException"/>；
    /// 正则求值超时（RegexMatchTimeoutException）→ <see cref="RetrievalMatchOutcome.Timeout"/>（不吞、不抛）；
    /// 其它异常照常上抛。
    /// </summary>
    public RetrievalMatchOutcome TryMatch(string? line, CancellationToken ct)
    {
        if (line is null)
        {
            return RetrievalMatchOutcome.NoMatch;
        }

        ct.ThrowIfCancellationRequested();

        if (_regex is not null)
        {
            try
            {
                return _regex.IsMatch(line)
                    ? RetrievalMatchOutcome.Match
                    : RetrievalMatchOutcome.NoMatch;
            }
            catch (RegexMatchTimeoutException)
            {
                // 超时是「未能判定」，不是「不匹配」：由调用方表达为覆盖非 Complete（Timeout）。
                return RetrievalMatchOutcome.Timeout;
            }
        }

        return line.Contains(_query, _comparison)
            ? RetrievalMatchOutcome.Match
            : RetrievalMatchOutcome.NoMatch;
    }

    /// <summary>
    /// 兼容层：等价于 <see cref="TryMatch"/>(line, CancellationToken.None) == Match。
    /// 注意：正则求值超时在此返回 false，与「不匹配」不可区分——需要区分时必须改用 <see cref="TryMatch"/>。
    /// </summary>
    public bool IsMatch(string line) =>
        TryMatch(line, CancellationToken.None) == RetrievalMatchOutcome.Match;
}
