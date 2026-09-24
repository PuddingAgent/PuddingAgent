using PuddingRetrievalEval.Contracts;
using PuddingRetrievalEval.Services;

namespace PuddingRetrievalEvalTests;

/// <summary>Path matching rules: case, separator, relative-vs-absolute, symbol suffix, de-duplication.</summary>
[TestClass]
public sealed class PathIdentityTests
{
    [TestMethod]
    public void Normalize_UnifiesCaseAndSeparators()
    {
        Assert.AreEqual("source/puddingcore/foo.cs", PathIdentity.Normalize(@"SOURCE\PuddingCore\.\Foo.cs"));
        Assert.AreEqual("source/foo.cs", PathIdentity.Normalize("./source//foo.cs"));
        Assert.AreEqual("source/foo.cs", PathIdentity.Normalize("Source/Foo.cs/"));
        Assert.AreEqual(string.Empty, PathIdentity.Normalize(null));
        Assert.AreEqual(string.Empty, PathIdentity.Normalize("   "));
    }

    [TestMethod]
    public void MatchesHit_RelativeExpectationMatchesAbsoluteHitPath()
    {
        var hit = new SearchProbeHit(@"E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Tools\Retrieval\RetrievalMatcher.cs");

        Assert.IsTrue(PathIdentity.MatchesHit(hit, "Source/PuddingCore/Tools/Retrieval/RetrievalMatcher.cs"));
    }

    [TestMethod]
    public void MatchesHit_DoesNotMatchADifferentFileOrAMereSuffixOfTheName()
    {
        var hit = new SearchProbeHit("repo/src/sub/Alpha.cs");

        Assert.IsFalse(PathIdentity.MatchesHit(hit, "repo/src/Beta.cs"));
        // "Alpha.cs" must not match on a bare name suffix without a separator boundary.
        Assert.IsFalse(PathIdentity.MatchesHit(hit, "lpha.cs"));
    }

    [TestMethod]
    public void MatchesHit_EnforcesTheSymbolConstraintWhenTheProbeReportsASymbol()
    {
        var hit = new SearchProbeHit("repo/src/Alpha.cs", "Alpha");

        Assert.IsTrue(PathIdentity.MatchesHit(hit, "repo/src/Alpha.cs#Alpha"));
        Assert.IsTrue(PathIdentity.MatchesHit(hit, "repo/src/Alpha.cs#alpha"));
        Assert.IsFalse(PathIdentity.MatchesHit(hit, "repo/src/Alpha.cs#Beta"));
        Assert.IsTrue(PathIdentity.MatchesHit(hit, "repo/src/Alpha.cs"));
    }

    [TestMethod]
    public void MatchesHit_IgnoresTheSymbolConstraintWhenTheProbeReportsNoSymbol()
    {
        // An engine that cannot report symbols must not be penalised for it.
        var hit = new SearchProbeHit("repo/src/Alpha.cs");

        Assert.IsTrue(PathIdentity.MatchesHit(hit, "repo/src/Alpha.cs#Alpha"));
    }

    [TestMethod]
    public void SplitExpected_SeparatesTheSymbolAtTheLastHash()
    {
        Assert.AreEqual(("repo/src/Alpha.cs", "Alpha"), PathIdentity.SplitExpected("repo/src/Alpha.cs#Alpha"));
        Assert.AreEqual(("repo/src/Alpha.cs", (string?)null), PathIdentity.SplitExpected("repo/src/Alpha.cs"));
        // A trailing '#' carries no symbol.
        Assert.AreEqual(("repo/src/Alpha.cs#", (string?)null), PathIdentity.SplitExpected("repo/src/Alpha.cs#"));
    }

    [TestMethod]
    public void DistinctExpected_DeduplicatesCaseSeparatorAndSymbolVariants()
    {
        var distinct = PathIdentity.DistinctExpected(
        [
            "repo/src/Alpha.cs",
            @"REPO\SRC\ALPHA.CS",
            "repo/src/Alpha.cs#Alpha",
            "repo/src/Beta.cs",
        ]);

        Assert.AreEqual(3, distinct.Count);
    }

    [TestMethod]
    public void DistinctExpected_DropsBlankEntries()
    {
        Assert.AreEqual(1, PathIdentity.DistinctExpected(["", "   ", "repo/src/Alpha.cs"]).Count);
    }

    [TestMethod]
    public void DistinctByFile_KeepsTheFirstOccurrenceOfEachFile()
    {
        var hits = TestHits.Of("repo/src/Alpha.cs", @"repo\SRC\alpha.cs", "repo/src/Beta.cs");

        var distinct = PathIdentity.DistinctByFile(hits);

        Assert.AreEqual(2, distinct.Count);
        Assert.AreEqual("repo/src/Alpha.cs", distinct[0].Path);
        Assert.AreEqual("repo/src/Beta.cs", distinct[1].Path);
    }

    [TestMethod]
    public void FirstRankOf_IsZeroBasedAndNegativeWhenAbsent()
    {
        var hits = TestHits.Of("repo/src/Alpha.cs", "repo/src/Beta.cs");

        Assert.AreEqual(1, PathIdentity.FirstRankOf(hits, "repo/src/Beta.cs"));
        Assert.AreEqual(-1, PathIdentity.FirstRankOf(hits, "repo/src/Missing.cs"));
    }
}
