using System.Text;
using PuddingRetrievalEval.Contracts;
using PuddingRetrievalEval.Services;

namespace PuddingRetrievalEvalTests;

/// <summary>
/// The report is the deliverable a human reads and the artifact a regression compares, so its contract
/// is pinned: raw numbers, explicit "no threshold", cold and warm buckets kept apart, UTF-8 without BOM.
/// </summary>
[TestClass]
public sealed class EvalReportWriterTests
{
    private static async Task<EvalRun> BuildRunAsync()
    {
        var probe = new StubSearchProbe("unit-probe")
            .ForQuery("Alpha", "repo/src/Alpha.cs")
            .ForQuery("Beta", "repo/node_modules/pkg/index.js");
        probe.ElapsedMs = 5;

        var set = new EvalSet("unit-set", 1,
        [
            new EvalCase("Alpha", ["repo/src/Alpha.cs"], EvalKind.Symbol, EvalLanguage.CSharp),
            new EvalCase("Beta", ["repo/src/Beta.cs"], EvalKind.Intent, EvalLanguage.Markdown),
        ]);

        return await new EvalRunner(probe)
            .RunAsync(set, new EvalRunOptions(new SearchScope("E:/repo"), "unit-scope", 10, 1, 2));
    }

    [TestMethod]
    public async Task ToMarkdown_DeclaresThatNoThresholdIsApplied()
    {
        var markdown = EvalReportWriter.ToMarkdown(await BuildRunAsync(), "dotnet test --filter unit");

        StringAssert.Contains(markdown, "No达标阈值");
        StringAssert.Contains(markdown, "## Reproduce");
        StringAssert.Contains(markdown, "dotnet test --filter unit");
    }

    [TestMethod]
    public async Task ToMarkdown_ContainsHeadlineMetricsStrataAndBothLatencyBuckets()
    {
        var markdown = EvalReportWriter.ToMarkdown(await BuildRunAsync());

        foreach (var expected in new[]
                 {
                     "| recall@1 |", "| recall@5 |", "| recall@10 |", "| MRR |",
                     "| precision@5 |", "| precision@10 |", "| noiseRate@10 |",
                     "## By language stratum",
                     "| CSharp |", "| Markdown |",
                     "## Latency (raw ms)", "cold (1st call per query)", "warm (all later calls)",
                     "## Cold-call raw samples", "## Per-case results", "### Expected hits per case",
                 })
        {
            StringAssert.Contains(markdown, expected);
        }

        // The probe reports 5 ms for every call; the report must show that raw number.
        StringAssert.Contains(markdown, "| 5 |");
    }

    [TestMethod]
    public async Task ToMarkdown_ListsEveryCaseWithItsExpectationAndWhatCameBack()
    {
        var markdown = EvalReportWriter.ToMarkdown(await BuildRunAsync());

        StringAssert.Contains(markdown, "repo/src/Alpha.cs");
        StringAssert.Contains(markdown, "repo/node_modules/pkg/index.js");
        StringAssert.Contains(markdown, "unit-probe");
        StringAssert.Contains(markdown, "| `repo/src/Alpha.cs` |");
    }

    [TestMethod]
    public async Task ToJson_SerialisesEnumNamesAndKeepsRawCaseNumbers()
    {
        var run = await BuildRunAsync();

        var json = EvalReportWriter.ToJson(run);

        StringAssert.Contains(json, "\"ProbeName\": \"unit-probe\"");
        StringAssert.Contains(json, "\"CSharp\"");
        StringAssert.Contains(json, "\"Markdown\"");
        StringAssert.Contains(json, "\"NoiseHitsAt10\"");
        StringAssert.Contains(json, "\"ColdLatency\"");
        StringAssert.Contains(json, "\"WarmLatency\"");
    }

    [TestMethod]
    public async Task ToJson_IsStableForTheSameRun_ExceptForTimestampsAndLatency()
    {
        var run = await BuildRunAsync();

        var first = EvalReportWriter.ToJson(run);
        var second = EvalReportWriter.ToJson(run);

        Assert.AreEqual(first, second, "serialising the same run twice must be byte-identical");
    }

    [TestMethod]
    public async Task Write_EmitsMarkdownAndJsonAsUtf8WithoutBom()
    {
        var run = await BuildRunAsync();
        var directory = Path.Combine(Path.GetTempPath(), "pudding-retrieval-eval-tests", Guid.NewGuid().ToString("N"));
        var basePath = Path.Combine(directory, "baseline");

        try
        {
            EvalReportWriter.Write(basePath, run, "reproduce-me");

            Assert.IsTrue(File.Exists(basePath + ".md"), "markdown report missing");
            Assert.IsTrue(File.Exists(basePath + ".json"), "json report missing");

            foreach (var path in new[] { basePath + ".md", basePath + ".json" })
            {
                var bytes = File.ReadAllBytes(path);
                Assert.IsTrue(bytes.Length > 0, $"{path} is empty");
                Assert.IsFalse(
                    bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                    $"{path} must be UTF-8 without BOM");
            }

            var roundTripped = File.ReadAllText(basePath + ".md", Encoding.UTF8);
            StringAssert.Contains(roundTripped, "reproduce-me");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
