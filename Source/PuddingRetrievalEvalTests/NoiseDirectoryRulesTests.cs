using PuddingRetrievalEval.Services;

namespace PuddingRetrievalEvalTests;

/// <summary>
/// The noise-directory definition drives the noise-rate metric, so its membership and its matching
/// semantics are pinned here: the instrument must not drift silently.
/// </summary>
[TestClass]
public sealed class NoiseDirectoryRulesTests
{
    [TestMethod]
    public void Segments_IsTheUnionOfTheThreeDocumentedRuleSets()
    {
        // search_grep(12) + IndexExcludePatterns.NoiseDirNames(28) + FullTextIndexOptions(33) -> 46 distinct.
        Assert.AreEqual(46, NoiseDirectoryRules.Segments.Count);

        // one representative member from each source set
        Assert.IsTrue(NoiseDirectoryRules.Segments.Contains("$outputWwwroot"), "SearchGrepTool set missing");
        Assert.IsTrue(NoiseDirectoryRules.Segments.Contains(".pudding-code"), "IndexExcludePatterns set missing");
        Assert.IsTrue(NoiseDirectoryRules.Segments.Contains("bower_components"), "FullTextIndexOptions set missing");
    }

    [TestMethod]
    public void IsNoisePath_FlagsTheDirectoriesTheUserCalledOut()
    {
        foreach (var path in new[]
                 {
                     "repo/node_modules/pkg/index.js",
                     "repo/dist/bundle.js",
                     "repo/bin/Debug/net10.0/app.dll",
                     "repo/obj/project.assets.json",
                     "repo/publish/app.exe",
                     "repo/.git/config",
                     "repo/TestResults/run.trx",
                 })
        {
            Assert.IsTrue(NoiseDirectoryRules.IsNoisePath(path), $"expected noise: {path}");
        }
    }

    [TestMethod]
    public void IsNoisePath_DoesNotFlagOrdinarySourcePaths()
    {
        foreach (var path in new[]
                 {
                     "Source/PuddingCore/Tools/Retrieval/RetrievalMatcher.cs",
                     "Docs/Conventions/组件化交付规程.md",
                     "Source/PuddingPlatformAdmin/src/pages/chat/client/syncEngine.ts",
                 })
        {
            Assert.IsFalse(NoiseDirectoryRules.IsNoisePath(path), $"unexpected noise: {path}");
        }
    }

    [TestMethod]
    public void IsNoisePath_MatchesWholeSegments_NotPrefixes()
    {
        Assert.IsFalse(NoiseDirectoryRules.IsNoisePath("repo/src/bin.cs"));
        Assert.IsFalse(NoiseDirectoryRules.IsNoisePath("repo/src/obj.json"));
        Assert.IsFalse(NoiseDirectoryRules.IsNoisePath("repo/src/binder/Foo.cs"));
        Assert.IsTrue(NoiseDirectoryRules.IsNoisePath("repo/src/bin/Foo.cs"));
    }

    [TestMethod]
    public void IsNoisePath_IsCaseInsensitiveAndSeparatorAgnostic()
    {
        Assert.IsTrue(NoiseDirectoryRules.IsNoisePath(@"Repo\NODE_MODULES\pkg\index.js"));
        Assert.IsTrue(NoiseDirectoryRules.IsNoisePath(@"Repo\Dist\bundle.js"));
        Assert.IsTrue(NoiseDirectoryRules.IsNoisePath("repo/BIN/app.dll"));
    }

    [TestMethod]
    public void IsNoisePath_HandlesRootLevelPathsAndTrailingSeparators()
    {
        Assert.IsTrue(NoiseDirectoryRules.IsNoisePath("node_modules/pkg/index.js"));
        Assert.IsTrue(NoiseDirectoryRules.IsNoisePath("repo/node_modules/"));
        Assert.IsFalse(NoiseDirectoryRules.IsNoisePath(string.Empty));
        Assert.IsFalse(NoiseDirectoryRules.IsNoisePath(null));
    }

    [TestMethod]
    public void CountNoiseHits_CountsOnlyNoisePaths()
    {
        var hits = new[]
        {
            "repo/src/A.cs",
            "repo/node_modules/pkg/index.js",
            "repo/src/B.cs",
            "repo/obj/x.json",
        };

        Assert.AreEqual(2, NoiseDirectoryRules.CountNoiseHits(hits));
    }
}
