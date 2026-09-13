// ADR-089 U0-S1：统一检索匹配合同与 RetrievalMatcher / RetrievalCoverage 单测。
// 覆盖：literal 大小写语义（Ordinal/OrdinalIgnoreCase，不依赖 culture）、非法正则明确报错（不静默降级）、
// 正则命中、中文匹配、空 query 拒绝、正则求值超时不抛出，以及 RetrievalCoverage 的 IsComplete 硬性语义。
using PuddingCode.Tools.Retrieval;

namespace PuddingCoreTests.Tools.Retrieval;

/// <summary>RetrievalMatcher 的合同行为单测。</summary>
[TestClass]
public sealed class RetrievalMatcherTests
{
    private static RetrievalMatcher Create(
        RetrievalMatchMode mode,
        RetrievalCaseMode caseMode,
        string query,
        TimeSpan? regexTimeout = null)
    {
        var timeout = regexTimeout ?? TimeSpan.FromSeconds(1);
        var ok = RetrievalMatcher.TryCreate(mode, caseMode, query, timeout, out var matcher, out var error);
        Assert.IsTrue(ok, $"TryCreate should succeed for '{query}', error: {error}");
        Assert.IsNotNull(matcher);
        Assert.IsNull(error);
        return matcher;
    }

    private static bool TryRaw(
        RetrievalMatchMode mode,
        RetrievalCaseMode caseMode,
        string query,
        TimeSpan regexTimeout,
        out string? contractError)
    {
        var ok = RetrievalMatcher.TryCreate(mode, caseMode, query, regexTimeout, out _, out contractError);
        return ok;
    }

    // ── literal：大小写语义 ───────────────────────────────────────────────

    [TestMethod]
    public void TryCreate_Literal_Insensitive_Matches_While_Sensitive_DoesNot()
    {
        const string line = "Unified Retrieval over indexed sources";

        var insensitive = Create(RetrievalMatchMode.Literal, RetrievalCaseMode.Insensitive, "unified retrieval");
        var sensitive = Create(RetrievalMatchMode.Literal, RetrievalCaseMode.Sensitive, "unified retrieval");

        // 同一输入、两种 case 模式，结论必须相反。
        Assert.IsTrue(insensitive.IsMatch(line));
        Assert.IsFalse(sensitive.IsMatch(line));
    }

    /// <summary>
    /// Ordinal 语义说明：literal 路径固定使用 StringComparison.Ordinal（Sensitive）与
    /// StringComparison.OrdinalIgnoreCase（Insensitive）。OrdinalIgnoreCase 的 case 折叠是 ASCII
    /// 内建规则（A-Z ↔ a-z），不查询任何 culture 的 casing 表，因此结果不随 CurrentCulture 变化。
    /// 以土耳其语为例：culture 敏感比较在 tr-TR 下可能把 "i" 折叠到 "İ"（U+0130），而
    /// OrdinalIgnoreCase 不会 —— 下面第二条断言固定了该行为（与宿主当前 culture 无关）。
    /// </summary>
    [TestMethod]
    public void TryCreate_Literal_Ordinal_CaseFolding_Is_Culture_Independent()
    {
        var insensitive = Create(RetrievalMatchMode.Literal, RetrievalCaseMode.Insensitive, "i");

        Assert.IsTrue(insensitive.IsMatch("FILE")); // ASCII 大小写折叠恒成立
        Assert.IsFalse(insensitive.IsMatch("İ"));   // U+0130 不参与 ASCII 折叠（culture 敏感比较才可能命中）
    }

    // ── regex：非法模式与命中 ────────────────────────────────────────────

    [TestMethod]
    public void TryCreate_InvalidRegex_Fails_With_Readable_Error_Containing_Pattern()
    {
        var ok = RetrievalMatcher.TryCreate(
            RetrievalMatchMode.Regex, RetrievalCaseMode.Insensitive, "a(",
            TimeSpan.FromSeconds(1), out var matcher, out var contractError);

        Assert.IsFalse(ok);
        Assert.IsNull(matcher);
        Assert.IsNotNull(contractError);
        StringAssert.Contains(contractError, "a("); // 错误必须携带原始 pattern，绝不静默降级为 literal
    }

    [TestMethod]
    public void TryCreate_ValidRegex_Matches_Respecting_CaseMode()
    {
        var sensitive = Create(RetrievalMatchMode.Regex, RetrievalCaseMode.Sensitive, @"\bretriev\w*");

        Assert.IsTrue(sensitive.IsMatch("unified retrieval pipeline"));
        Assert.IsFalse(sensitive.IsMatch("unified RETRIEVAL pipeline")); // Sensitive 时正则不忽略大小写
    }

    [TestMethod]
    public void TryCreate_ValidRegex_Insensitive_Matches_Uppercase()
    {
        var insensitive = Create(RetrievalMatchMode.Regex, RetrievalCaseMode.Insensitive, @"\bretriev\w*");

        Assert.IsTrue(insensitive.IsMatch("unified RETRIEVAL pipeline"));
    }

    // ── 中文匹配 ─────────────────────────────────────────────────────────

    [TestMethod]
    public void TryCreate_Chinese_Literal_Matches_Both_CaseModes()
    {
        const string line = "第七节：统一检索与渐进展开工具链设计";

        var sensitive = Create(RetrievalMatchMode.Literal, RetrievalCaseMode.Sensitive, "统一检索");
        var insensitive = Create(RetrievalMatchMode.Literal, RetrievalCaseMode.Insensitive, "统一检索");

        Assert.IsTrue(sensitive.IsMatch(line));
        Assert.IsTrue(insensitive.IsMatch(line));
    }

    [TestMethod]
    public void TryCreate_Chinese_Regex_Dot_Matches_Exactly_One_Char()
    {
        var matcher = Create(RetrievalMatchMode.Regex, RetrievalCaseMode.Insensitive, "统.检索");

        Assert.IsTrue(matcher.IsMatch("统一检索"));   // "." 命中一个汉字
        Assert.IsFalse(matcher.IsMatch("统一与检索")); // 两个汉字不匹配单个 "."
    }

    // ── 合同拒绝：空 query / 非法 timeout / 未知枚举 ─────────────────────

    [TestMethod]
    public void TryCreate_EmptyQuery_Fails_With_ContractError()
    {
        Assert.IsFalse(TryRaw(RetrievalMatchMode.Literal, RetrievalCaseMode.Insensitive, "",
            TimeSpan.FromSeconds(1), out var literalError));
        StringAssert.Contains(literalError!, "query");

        Assert.IsFalse(TryRaw(RetrievalMatchMode.Regex, RetrievalCaseMode.Sensitive, "   ",
            TimeSpan.FromSeconds(1), out var regexError));
        StringAssert.Contains(regexError!, "query");
    }

    [TestMethod]
    public void TryCreate_NonPositive_RegexTimeout_Fails()
    {
        // regexTimeout 必须来自参数且为正值；零/负值属于合同错误（不内置默认常量兜底）。
        Assert.IsFalse(TryRaw(RetrievalMatchMode.Regex, RetrievalCaseMode.Sensitive, "a+",
            TimeSpan.Zero, out var error));
        StringAssert.Contains(error!, "regexTimeout");
    }

    [TestMethod]
    public void TryCreate_UnknownEnumValue_Fails_With_ContractError()
    {
        Assert.IsFalse(TryRaw((RetrievalMatchMode)999, RetrievalCaseMode.Sensitive, "a",
            TimeSpan.FromSeconds(1), out var modeError));
        StringAssert.Contains(modeError!, "mode");

        Assert.IsFalse(TryRaw(RetrievalMatchMode.Literal, (RetrievalCaseMode)999, "a",
            TimeSpan.FromSeconds(1), out var caseError));
        StringAssert.Contains(caseError!, "case");
    }

    // ── 超时：返回 false 且不抛出 ────────────────────────────────────────

    [TestMethod]
    public void IsMatch_RegexTimeout_Returns_False_Without_Throwing()
    {
        // 灾难性回溯模式：regexTimeout 由参数显式传入（本类型不内置常量）。
        // 求值超时应表现为"不命中"，而不是把 RegexMatchTimeoutException 抛给调用方。
        var matcher = Create(
            RetrievalMatchMode.Regex, RetrievalCaseMode.Sensitive, "^(a+)+$",
            TimeSpan.FromMilliseconds(50));

        Assert.IsFalse(matcher.IsMatch(new string('a', 32) + "!"));
    }

    [TestMethod]
    public void IsMatch_NullLine_Returns_False()
    {
        var literal = Create(RetrievalMatchMode.Literal, RetrievalCaseMode.Sensitive, "a");
        var regex = Create(RetrievalMatchMode.Regex, RetrievalCaseMode.Sensitive, "a+");

        Assert.IsFalse(literal.IsMatch(null!));
        Assert.IsFalse(regex.IsMatch(null!));
    }
}

/// <summary>RetrievalCoverage 的 IsComplete 硬性语义单测。</summary>
[TestClass]
public sealed class RetrievalCoverageTests
{
    [TestMethod]
    public void Complete_IsComplete_True()
    {
        Assert.IsTrue(RetrievalCoverage.Complete().IsComplete);
        Assert.IsTrue(RetrievalCoverage.Complete(["scanned 12 files"]).IsComplete);
    }

    [TestMethod]
    public void NonComplete_Status_IsComplete_False()
    {
        Assert.IsFalse(RetrievalCoverage.Partial("stopped at directory 3/10").IsComplete);
        Assert.IsFalse(RetrievalCoverage.Of(RetrievalCoverageStatus.Truncated, "hit limit 20").IsComplete);
        Assert.IsFalse(RetrievalCoverage.Of(RetrievalCoverageStatus.Timeout, "regex budget exhausted").IsComplete);
        Assert.IsFalse(RetrievalCoverage.Of(RetrievalCoverageStatus.Unavailable, "index backend offline").IsComplete);
        Assert.IsFalse(RetrievalCoverage.Of(RetrievalCoverageStatus.ContractError, "invalid glob").IsComplete);
    }

    [TestMethod]
    public void IsComplete_Is_Derived_From_Status_Ignoring_Ctor_Arg()
    {
        // 硬性语义：IsComplete 由 Status 唯一推导 —— 调用方误传也不能构造出矛盾状态。
        var forcedFalse = new RetrievalCoverage(RetrievalCoverageStatus.Complete, [], isComplete: false);
        var forcedTrue = new RetrievalCoverage(RetrievalCoverageStatus.Partial, ["x"], isComplete: true);

        Assert.IsTrue(forcedFalse.IsComplete);
        Assert.IsFalse(forcedTrue.IsComplete);
    }
}

/// <summary>RetrievalQuery 默认值合同单测。</summary>
[TestClass]
public sealed class RetrievalQueryContractTests
{
    [TestMethod]
    public void RetrievalQuery_Has_Specified_Defaults()
    {
        var query = new RetrievalQuery("统一检索", @"E:\github\AgentNetworkPlan\PuddingAgent\Source");

        Assert.AreEqual("统一检索", query.Query);
        Assert.AreEqual(@"E:\github\AgentNetworkPlan\PuddingAgent\Source", query.Scope);
        Assert.AreEqual(RetrievalMatchMode.Literal, query.Match);
        Assert.AreEqual(RetrievalCaseMode.Insensitive, query.Case);
        Assert.IsNull(query.Glob);
        Assert.AreEqual(RetrievalFreshness.Indexed, query.Freshness);
        Assert.AreEqual(20, query.Limit);
        Assert.IsNull(query.Cursor);
    }
}
