using PuddingCode.Tools;
using PuddingRuntime.Services.Search;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class SearchAttemptLedgerTests
{
    [TestMethod]
    public void Record_Then_TryGetSuppression_Only_For_Deterministic_Outcomes()
    {
        var ledger = new SearchAttemptLedger();
        var key = new SearchAttemptKey("search_grep", "Needle", "c:\\ws", "*.cs", false, "abc123");

        Assert.IsFalse(ledger.TryGetSuppression(key, out _));

        ledger.Record(key, new SearchAttemptRecord(SearchAttemptOutcome.NoMatch, "(no matches)", 0, DateTimeOffset.UtcNow));
        Assert.IsTrue(ledger.TryGetSuppression(key, out var prior));
        Assert.AreEqual(SearchAttemptOutcome.NoMatch, prior.Outcome);

        ledger.Record(key, new SearchAttemptRecord(SearchAttemptOutcome.Timeout, "(search timed out)", 0, DateTimeOffset.UtcNow));
        Assert.IsTrue(ledger.TryGetSuppression(key, out prior));
        Assert.AreEqual(SearchAttemptOutcome.Timeout, prior.Outcome);

        // 命中/截断/错误不可被短路
        ledger.Record(key, new SearchAttemptRecord(SearchAttemptOutcome.Hit, "a.cs:1", 1, DateTimeOffset.UtcNow));
        Assert.IsFalse(ledger.TryGetSuppression(key, out _));

        ledger.Record(key, new SearchAttemptRecord(SearchAttemptOutcome.Truncated, "truncated", 10, DateTimeOffset.UtcNow));
        Assert.IsFalse(ledger.TryGetSuppression(key, out _));

        ledger.Record(key, new SearchAttemptRecord(SearchAttemptOutcome.Error, "boom", 0, DateTimeOffset.UtcNow));
        Assert.IsFalse(ledger.TryGetSuppression(key, out _));
    }

    [TestMethod]
    public void Key_Is_Sensitive_To_All_Components()
    {
        var ledger = new SearchAttemptLedger();
        var baseKey = new SearchAttemptKey("search_grep", "Needle", "c:\\ws", "*.cs", false, "v1");

        ledger.Record(baseKey, new SearchAttemptRecord(SearchAttemptOutcome.NoMatch, "(no matches)", 0, DateTimeOffset.UtcNow));

        Assert.IsFalse(ledger.TryGetSuppression(baseKey with { Query = "Other" }, out _));
        Assert.IsFalse(ledger.TryGetSuppression(baseKey with { Scope = "c:\\other" }, out _));
        Assert.IsFalse(ledger.TryGetSuppression(baseKey with { Glob = "*.txt" }, out _));
        Assert.IsFalse(ledger.TryGetSuppression(baseKey with { CaseSensitive = true }, out _));
        Assert.IsFalse(ledger.TryGetSuppression(baseKey with { WorkspaceVersion = "v2" }, out _));
        Assert.IsFalse(ledger.TryGetSuppression(baseKey with { Tool = "file_search" }, out _));
        Assert.IsTrue(ledger.TryGetSuppression(baseKey, out _));
    }

    [TestMethod]
    public void NormalizeScope_Produces_Canonical_Lowercased_Absolute_Path()
    {
        var raw = Path.Combine(Path.GetTempPath(), "PuddingWs", "Sub");
        var normalized = SearchAttemptKeyNormalizer.NormalizeScope(raw);
        Assert.AreEqual(raw.ToLowerInvariant(), normalized);

        Assert.AreEqual(string.Empty, SearchAttemptKeyNormalizer.NormalizeScope(null));
        Assert.AreEqual(string.Empty, SearchAttemptKeyNormalizer.NormalizeScope("   "));
    }

    [TestMethod]
    public void WorkspaceVersion_Is_Deterministic_And_Never_Throws()
    {
        // 无 .git 的临时目录应退化为空串，且缓存后重复调用一致。
        var temp = Path.Combine(Path.GetTempPath(), "pudding-wsv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var v1 = SearchWorkspaceVersion.Resolve(temp);
            var v2 = SearchWorkspaceVersion.Resolve(temp);
            Assert.AreEqual(v1, v2);
            // 空串或 40 位 hex 都是合法确定性结果；此处无 git 仓库，期望空串。
            Assert.IsTrue(v1.Length == 0 || (v1.Length == 40 && v1.All(Uri.IsHexDigit)));
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }
}
