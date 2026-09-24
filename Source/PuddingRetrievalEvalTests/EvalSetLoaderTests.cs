using PuddingRetrievalEval.Contracts;
using PuddingRetrievalEval.Services;

namespace PuddingRetrievalEvalTests;

/// <summary>
/// The annotated query set is version-controlled data, so these tests check both halves:
/// (a) the shipped seed set loads and satisfies the annotation contract (task acceptance A1),
/// (b) malformed sets are rejected fail-closed instead of silently weakening the measurement.
/// </summary>
[TestClass]
public sealed class EvalSetLoaderTests
{
    private static string SeedSetPath =>
        Path.Combine(AppContext.BaseDirectory, "eval", "sets", "seed-v1.json");

    [TestMethod]
    public void LoadFromFile_LoadsTheVersionControlledSeedSet()
    {
        Assert.IsTrue(File.Exists(SeedSetPath),
            $"the annotated set must be copied from Source/PuddingRetrievalEval/eval/sets/ to the test output; expected {SeedSetPath}");

        var set = EvalSetLoader.LoadFromFile(SeedSetPath);

        Assert.AreEqual("seed-v1", set.Name);
        Assert.AreEqual(1, set.Version);
        Assert.AreEqual(80, set.Cases.Count,
            "U4-0 ships exactly 80 annotated cases; changing the set requires bumping 'version' and this number");
    }

    [TestMethod]
    public void LoadFromFile_CoversEveryLanguageStratumAndEveryKind()
    {
        var set = EvalSetLoader.LoadFromFile(SeedSetPath);

        var byLanguage = set.Cases.GroupBy(c => c.Language).ToDictionary(g => g.Key, g => g.Count());
        Assert.IsTrue(byLanguage.TryGetValue(EvalLanguage.CSharp, out var csharp) && csharp >= 15,
            $"C# stratum must have >= 15 cases, found {(byLanguage.TryGetValue(EvalLanguage.CSharp, out var c) ? c : 0)}");
        Assert.IsTrue(byLanguage.TryGetValue(EvalLanguage.TypeScript, out var ts) && ts >= 15,
            $"TS/TSX stratum must have >= 15 cases, found {(byLanguage.TryGetValue(EvalLanguage.TypeScript, out var t) ? t : 0)}");
        Assert.IsTrue(byLanguage.TryGetValue(EvalLanguage.Markdown, out var md) && md >= 15,
            $"markdown stratum must have >= 15 cases, found {(byLanguage.TryGetValue(EvalLanguage.Markdown, out var m) ? m : 0)}");

        var kinds = set.Cases.Select(c => c.Kind).Distinct().ToArray();
        CollectionAssert.AreEquivalent(
            new[] { EvalKind.Symbol, EvalKind.Intent, EvalKind.Crossref },
            kinds,
            "all three annotation kinds must be present");

        Assert.IsFalse(set.Cases.Any(c => c.Kind == EvalKind.Unknown), "no case may remain Unknown");
        Assert.IsFalse(set.Cases.Any(c => c.Language == EvalLanguage.Unknown), "no case may remain Unknown");
    }

    [TestMethod]
    public void LoadFromFile_EveryCaseCarriesAtLeastOneExpectedHit()
    {
        var set = EvalSetLoader.LoadFromFile(SeedSetPath);

        foreach (var evalCase in set.Cases)
        {
            Assert.IsTrue(evalCase.ExpectedHits.Count >= 1, $"no expectation: {evalCase}");
            Assert.IsFalse(evalCase.ExpectedHits.Any(string.IsNullOrWhiteSpace), $"blank expectation: {evalCase}");
        }
    }

    [TestMethod]
    public void LoadFromFile_HasNoDuplicateAnnotationForTheSameKind()
    {
        var set = EvalSetLoader.LoadFromFile(SeedSetPath);

        var duplicates = set.Cases
            .GroupBy(c => $"{c.Kind}|{c.Language}|{c.Query}", StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.AreEqual(0, duplicates.Length, "duplicate annotations: " + string.Join(", ", duplicates));
    }

    [TestMethod]
    public void Parse_AcceptsLanguageAliases()
    {
        var set = EvalSetLoader.Parse(
            """
            { "name": "aliases", "version": 1, "cases": [
              { "query": "a", "expectedHits": ["x.cs"], "kind": "symbol", "language": "cs" },
              { "query": "b", "expectedHits": ["x.ts"], "kind": "intent", "language": "ts" },
              { "query": "c", "expectedHits": ["x.tsx"], "kind": "crossref", "language": "tsx" },
              { "query": "d", "expectedHits": ["x.md"], "kind": "intent", "language": "md" }
            ] }
            """,
            "aliases.json");

        CollectionAssert.AreEqual(
            new[] { EvalLanguage.CSharp, EvalLanguage.TypeScript, EvalLanguage.TypeScript, EvalLanguage.Markdown },
            set.Cases.Select(c => c.Language).ToArray());
    }

    [TestMethod]
    public void Parse_IsCaseInsensitiveForPropertyNamesAndLiterals()
    {
        var set = EvalSetLoader.Parse(
            """
            { "Name": "casing", "Version": 2, "Cases": [
              { "Query": "q", "ExpectedHits": ["x.cs"], "Kind": "SYMBOL", "Language": "CSharp" }
            ] }
            """,
            "casing.json");

        Assert.AreEqual("casing", set.Name);
        Assert.AreEqual(2, set.Version);
        Assert.AreEqual(1, set.Cases.Count);
        Assert.AreEqual(EvalKind.Symbol, set.Cases[0].Kind);
        Assert.AreEqual(EvalLanguage.CSharp, set.Cases[0].Language);
    }

    [TestMethod]
    public void Parse_RejectsUnknownKind()
    {
        var error = ExpectFormatError(
            """{ "name": "x", "version": 1, "cases": [ { "query": "q", "expectedHits": ["a"], "kind": "guess", "language": "cs" } ] }""");

        StringAssert.Contains(error.Message, "unknown kind");
    }

    [TestMethod]
    public void Parse_RejectsUnknownLanguage()
    {
        var error = ExpectFormatError(
            """{ "name": "x", "version": 1, "cases": [ { "query": "q", "expectedHits": ["a"], "kind": "symbol", "language": "klingon" } ] }""");

        StringAssert.Contains(error.Message, "unknown language");
    }

    [TestMethod]
    public void Parse_RejectsEmptyCaseArray()
    {
        var error = ExpectFormatError("""{ "name": "x", "version": 1, "cases": [] }""");

        StringAssert.Contains(error.Message, "must not be empty");
    }

    [TestMethod]
    public void Parse_RejectsMissingCasesProperty()
    {
        ExpectFormatError("""{ "name": "x", "version": 1 }""");
    }

    [TestMethod]
    public void Parse_RejectsBlankQuery()
    {
        var error = ExpectFormatError(
            """{ "name": "x", "version": 1, "cases": [ { "query": "  ", "expectedHits": ["a"], "kind": "symbol", "language": "cs" } ] }""");

        StringAssert.Contains(error.Message, "'query'");
    }

    [TestMethod]
    public void Parse_RejectsEmptyExpectedHits()
    {
        var error = ExpectFormatError(
            """{ "name": "x", "version": 1, "cases": [ { "query": "q", "expectedHits": [], "kind": "symbol", "language": "cs" } ] }""");

        StringAssert.Contains(error.Message, "expectedHits");
    }

    [TestMethod]
    public void Parse_RejectsDuplicateExpectedHits()
    {
        var error = ExpectFormatError(
            """{ "name": "x", "version": 1, "cases": [ { "query": "q", "expectedHits": ["a.cs", "A.CS"], "kind": "symbol", "language": "cs" } ] }""");

        StringAssert.Contains(error.Message, "duplicate expected hit");
    }

    [TestMethod]
    public void Parse_RejectsInvalidJsonAndBadVersion()
    {
        ExpectFormatError("{ \"name\": \"x\", ");
        ExpectFormatError("""{ "name": "x", "version": 0, "cases": [ { "query": "q", "expectedHits": ["a"], "kind": "symbol", "language": "cs" } ] }""");
        ExpectFormatError("[]");
    }

    [TestMethod]
    public void LoadFromFile_RejectsMissingFile()
    {
        RetrievalMetricsTests.ExpectThrows<FileNotFoundException>(() =>
            EvalSetLoader.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "eval", "sets", "does-not-exist.json")));
    }

    private static EvalSetFormatException ExpectFormatError(string json)
    {
        try
        {
            EvalSetLoader.Parse(json, "test.json");
        }
        catch (EvalSetFormatException ex)
        {
            Assert.IsTrue(ex.Message.StartsWith("test.json:", StringComparison.Ordinal),
                "the error must name the offending source: " + ex.Message);
            return ex;
        }

        Assert.Fail("expected EvalSetFormatException, none was thrown");
        throw new InvalidOperationException("unreachable");
    }
}
