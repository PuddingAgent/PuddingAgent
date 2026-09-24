using PuddingIndexChunking;

namespace PuddingIndexChunkingTests;

/// <summary>
/// C2 filter rules (ADR-089): minimum token length, per-language keyword stop words, case handling and
/// the bookkeeping that lets a report attribute removals to a rule.
/// <para>
/// <c>Before_And_After_Token_Sets_Differ_Exactly_By_Removed_Tokens</c> is the component-level form of
/// the acceptance evidence "过滤真的生效": it asserts the exact before-set, the exact after-set and the
/// exact removed-set, so a rule that silently stopped working cannot hide behind a smaller index.
/// </para>
/// <para>
/// The fixture tokenises to <b>12</b> tokens — <c>public static void Configure string path int i if i 0
/// else</c>. Note that <c>0</c> is a token and that <c>if</c> is only two characters long, so with the
/// C# minimum length of 3 it is the <b>length</b> rule (not the keyword rule) that removes it; the
/// per-rule attribution below depends on that order and would change if it flipped.
/// </para>
/// </summary>
[TestClass]
public sealed class ChunkFilterTests
{
    /// <summary>A snippet with both short tokens (<c>i</c>, <c>0</c>) and keywords (<c>public</c>, <c>if</c>, …).</summary>
    private const string MixedSnippet =
        "public static void Configure(string path, int i) { if (i > 0) { else(); } }";

    private const int TokensIn = 12;

    [TestMethod]
    public void Rules_Are_Language_Specific()
    {
        Assert.AreEqual(3, ChunkFilterRules.DefaultMinimumTokenLengthFor(SourceLanguage.CSharp));
        Assert.AreEqual(3, ChunkFilterRules.DefaultMinimumTokenLengthFor(SourceLanguage.TypeScript));
        Assert.AreEqual(3, ChunkFilterRules.DefaultMinimumTokenLengthFor(SourceLanguage.Python));
        Assert.AreEqual(2, ChunkFilterRules.DefaultMinimumTokenLengthFor(SourceLanguage.Markdown));
        Assert.AreEqual(2, ChunkFilterRules.DefaultMinimumTokenLengthFor(SourceLanguage.Unknown));

        Assert.Contains("if", ChunkFilterRules.DefaultStopWords(SourceLanguage.CSharp));
        Assert.Contains("function", ChunkFilterRules.DefaultStopWords(SourceLanguage.TypeScript));
        Assert.Contains("def", ChunkFilterRules.DefaultStopWords(SourceLanguage.Python));

        // Language difference, in both directions: prose has no keyword list, and C# does not know Python.
        Assert.IsEmpty(ChunkFilterRules.DefaultStopWords(SourceLanguage.Markdown));
        Assert.DoesNotContain("def", ChunkFilterRules.DefaultStopWords(SourceLanguage.CSharp));
        Assert.DoesNotContain("public", ChunkFilterRules.DefaultStopWords(SourceLanguage.Python));

        // The stored lists are lower-case data; case folding is the filter's job, not the data's.
        Assert.Contains("public", ChunkFilterRules.CSharpKeywords);
        Assert.DoesNotContain("Public", ChunkFilterRules.CSharpKeywords);
    }

    [TestMethod]
    public void Stop_Word_Matching_Is_Case_Insensitive_By_Default_And_Literal_When_Disabled()
    {
        var insensitive = new ChunkFilter();
        Assert.IsTrue(insensitive.IsStopWord("if", SourceLanguage.CSharp));
        Assert.IsTrue(insensitive.IsStopWord("IF", SourceLanguage.CSharp));
        Assert.IsTrue(insensitive.IsStopWord("If", SourceLanguage.CSharp));

        var sensitive = new ChunkFilter(new ChunkFilterOptions { IgnoreCase = false });
        Assert.IsTrue(sensitive.IsStopWord("if", SourceLanguage.CSharp));
        Assert.IsFalse(sensitive.IsStopWord("IF", SourceLanguage.CSharp));
        Assert.IsFalse(sensitive.IsStopWord("If", SourceLanguage.CSharp));

        // …and the difference survives the whole pipeline, not just the predicate: with case folding
        // every upper-case spelling disappears, without it only the literally lower-case one does.
        Assert.AreEqual(string.Empty, insensitive.Apply("ELSE IF ELSE", SourceLanguage.CSharp));
        Assert.AreEqual("ELSE ELSE", sensitive.Apply("ELSE IF ELSE", SourceLanguage.CSharp));
    }

    [TestMethod]
    public void Minimum_Token_Length_Uses_Language_Default_And_Honours_Override()
    {
        var defaults = new ChunkFilter();
        Assert.AreEqual(3, defaults.MinimumTokenLengthFor(SourceLanguage.CSharp));
        Assert.IsTrue(defaults.IsShortToken("ab", SourceLanguage.CSharp));
        Assert.IsFalse(defaults.IsShortToken("abc", SourceLanguage.CSharp));

        var overridden = new ChunkFilter(new ChunkFilterOptions { MinimumTokenLength = 5 });
        Assert.AreEqual(5, overridden.MinimumTokenLengthFor(SourceLanguage.CSharp));
        Assert.AreEqual(5, overridden.MinimumTokenLengthFor(SourceLanguage.Markdown));
        Assert.IsTrue(overridden.IsShortToken("path", SourceLanguage.CSharp));
        Assert.IsFalse(overridden.IsShortToken("paths", SourceLanguage.CSharp));
    }

    [TestMethod]
    public void Before_And_After_Token_Sets_Differ_Exactly_By_Removed_Tokens()
    {
        var filter = new ChunkFilter();
        var before = ChunkTokenizer.Tokenize(MixedSnippet);
        var detailed = filter.ApplyDetailed(MixedSnippet, SourceLanguage.CSharp);
        var after = ChunkTokenizer.Tokenize(detailed.Text);

        // Raw side, so the expectation below is not self-fulfilling.
        Assert.AreEqual(
            "public static void Configure string path int i if i 0 else",
            string.Join(' ', before),
            "tokenisation of the fixture must be exactly this, otherwise the diff below means nothing");
        Assert.HasCount(TokensIn, before);

        Assert.AreEqual(
            "Configure path",
            detailed.Text,
            "the filtered text is the surviving tokens joined by spaces");
        Assert.AreEqual(
            "Configure path",
            string.Join(' ', after),
            "re-tokenising the filtered text reproduces the kept tokens — filtering invents nothing");

        var removed = before.ToHashSet(StringComparer.Ordinal).Except(after.ToHashSet(StringComparer.Ordinal));
        CollectionAssert.AreEquivalent(
            new[] { "0", "else", "i", "if", "int", "public", "static", "string", "void" },
            removed.OrderBy(t => t, StringComparer.Ordinal).ToArray(),
            "the removed token set is exactly the keywords plus the short tokens");

        // Per-rule attribution: the length rule runs first, so `i`, `i`, `0` and the two-character `if`
        // are attributed to it; only the remaining keywords are attributed to the keyword rule.
        Assert.AreEqual(new FilterReport(TokensIn, 2, 4, 6), detailed.Report);
    }

    [TestMethod]
    public void Rule_Attribution_Moves_When_A_Rule_Is_Switched_Off()
    {
        // Only the length rule: keywords survive.
        var lengthOnly = new ChunkFilter(new ChunkFilterOptions { RemoveStopWords = false });
        var lengthOnlyResult = lengthOnly.ApplyDetailed(MixedSnippet, SourceLanguage.CSharp);
        Assert.AreEqual(new FilterReport(TokensIn, 8, 4, 0), lengthOnlyResult.Report);
        Assert.AreEqual("public static void Configure string path int else", lengthOnlyResult.Text);

        // Only the keyword rule: the short tokens survive, which is that rule's measured contribution.
        var stopWordsOnly = new ChunkFilter(new ChunkFilterOptions { RemoveShortTokens = false });
        var stopWordsOnlyResult = stopWordsOnly.ApplyDetailed(MixedSnippet, SourceLanguage.CSharp);
        Assert.AreEqual(new FilterReport(TokensIn, 5, 0, 7), stopWordsOnlyResult.Report);
        Assert.AreEqual("Configure path i i 0", stopWordsOnlyResult.Text);
    }

    [TestMethod]
    public void Prose_Language_Keeps_Keywords_And_Only_Drops_Short_Tokens()
    {
        var filter = new ChunkFilter();
        var detailed = filter.ApplyDetailed(MixedSnippet, SourceLanguage.Markdown);

        // Same text, different language profile: min length 2 and no keyword list, so `if` survives.
        Assert.AreEqual(new FilterReport(TokensIn, 9, 3, 0), detailed.Report);
        Assert.Contains("if", detailed.Text.Split(' '));
        Assert.Contains("else", detailed.Text.Split(' '));
        Assert.DoesNotContain("i", detailed.Text.Split(' '));
    }

    [TestMethod]
    public void No_Op_Filter_Returns_The_Tokenised_Text_Unchanged()
    {
        var filter = new ChunkFilter(new ChunkFilterOptions { RemoveShortTokens = false, RemoveStopWords = false });
        Assert.IsTrue(filter.Options.IsNoOp);

        var expected = string.Join(' ', ChunkTokenizer.Tokenize(MixedSnippet));
        Assert.AreEqual(expected, filter.Apply(MixedSnippet, SourceLanguage.CSharp));
        Assert.AreEqual(expected, filter.Apply(MixedSnippet, SourceLanguage.Markdown));
    }

    [TestMethod]
    public void Filter_Tokens_Preserves_Order_And_Duplicates()
    {
        var filter = new ChunkFilter();
        var kept = filter.FilterTokens(["Alpha", "beta", "if", "alpha", "x", "Alpha"], SourceLanguage.CSharp);

        CollectionAssert.AreEqual(new[] { "Alpha", "beta", "alpha", "Alpha" }, kept.ToArray());
    }

    [TestMethod]
    public void Empty_And_Null_Text_Produce_Empty_Output()
    {
        var filter = new ChunkFilter();
        Assert.AreEqual(string.Empty, filter.Apply(null, SourceLanguage.CSharp));
        Assert.AreEqual(string.Empty, filter.Apply(string.Empty, SourceLanguage.CSharp));
        Assert.AreEqual(string.Empty, filter.Apply("   \t  ", SourceLanguage.CSharp));
        Assert.AreEqual(FilterReport.Zero, filter.ApplyDetailed(null, SourceLanguage.CSharp).Report);
    }

    [TestMethod]
    public void Filter_Reports_Aggregate_For_Corpus_Scale_Accounting()
    {
        var total = new FilterReport(1, 2, 3, 4) + new FilterReport(10, 20, 30, 40);
        Assert.AreEqual(new FilterReport(11, 22, 33, 44), total);
        Assert.AreEqual(FilterReport.Zero, FilterReport.Zero + FilterReport.Zero);
    }
}
