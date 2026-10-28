using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCodeIndex.Services.Retrieval;
using PuddingFullTextIndex.Contracts;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services.Retrieval;

/// <summary>
/// <see cref="FullTextRetrievalTextSource"/> 的边界断言。
/// <para>
/// 断言重点不是"能搜到"，而是<b>失败语义</b>：scope 映射不到目录、没有已建索引、
/// 引擎报错，三种情况都必须抛
/// <see cref="RetrievalTextSourceUnavailableException"/>，
/// 绝不允许返回空集合（返回空只表示"查了，没匹配"）。
/// </para>
/// </summary>
[TestClass]
public sealed class FullTextRetrievalTextSourceTests
{
    private sealed class FakeEngine(
        IReadOnlyList<FullTextSearchMatch>? matches = null,
        bool hasIndex = true,
        bool success = true,
        string? error = null) : IFullTextSearchEngine
    {
        public int SearchCalls { get; private set; }

        public int LastMaxResults { get; private set; }

        public bool HasIndex(string directoryPath) => hasIndex;

        public Task<FullTextSearchResult> SearchAsync(
            string query,
            string directoryPath,
            int maxResults = 30,
            string? fileExtensionFilter = null,
            string? subDirectoryFilter = null,
            CancellationToken ct = default,
            FullTextSearchScope? scope = null)
        {
            SearchCalls++;
            LastMaxResults = maxResults;

            var result = new FullTextSearchResult(
                success,
                success ? (matches ?? []).Take(maxResults).ToList() : [],
                error,
                success ? (matches ?? []).Count : 0,
                1);

            return Task.FromResult(result);
        }

        public Task<FullTextIndexResult> BuildIndexAsync(
            string directoryPath,
            string? filePatterns = null,
            CancellationToken ct = default) =>
            Task.FromResult(new FullTextIndexResult(true, 0, 0, 0, null));

        public bool RemoveIndex(string directoryPath) => true;
    }

    private sealed class FakeDirectories(params string[] roots) : IRetrievalScopeDirectories
    {
        public IReadOnlyList<string> Resolve(string scopeId) => roots;
    }

    private static FullTextSearchMatch Match(int line, string file = @"D:\repo\A.cs") =>
        new(file, line, "// OrderService note");

    [TestMethod]
    public async Task ScopeMapsToNoDirectory_ThrowsUnavailable_NotEmpty()
    {
        var source = new FullTextRetrievalTextSource(new FakeEngine(), new FakeDirectories());

        await AssertUnavailableAsync(() => source.SearchAsync("ws-1", "Order", 10, CancellationToken.None));
    }

    [TestMethod]
    public async Task NoIndexedDirectory_ThrowsUnavailable_NotEmpty()
    {
        var engine = new FakeEngine(hasIndex: false);
        var source = new FullTextRetrievalTextSource(engine, new FakeDirectories(@"D:\repo"));

        await AssertUnavailableAsync(() => source.SearchAsync("ws-1", "Order", 10, CancellationToken.None));

        // 没有索引就不该去搜 —— 搜了再报空会把"没索引"读成"没匹配"。
        Assert.AreEqual(0, engine.SearchCalls);
    }

    [TestMethod]
    public async Task EngineFailure_ThrowsUnavailable_NotEmpty()
    {
        var engine = new FakeEngine(success: false, error: "Directory 'D:\\repo' is not indexed.");
        var source = new FullTextRetrievalTextSource(engine, new FakeDirectories(@"D:\repo"));

        await AssertUnavailableAsync(() => source.SearchAsync("ws-1", "Order", 10, CancellationToken.None));
    }

    [TestMethod]
    public async Task MatchesAreMappedToRetrievalTextMatch()
    {
        var engine = new FakeEngine([Match(12), Match(30, @"D:\repo\B.cs")]);
        var source = new FullTextRetrievalTextSource(engine, new FakeDirectories(@"D:\repo"));

        var matches = await source.SearchAsync("ws-1", "Order", 10, CancellationToken.None);

        Assert.AreEqual(2, matches.Count);
        Assert.AreEqual(@"D:\repo\A.cs", matches[0].FilePath);
        Assert.AreEqual(12, matches[0].Line);
        Assert.AreEqual("// OrderService note", matches[0].LineText);
        Assert.AreEqual(30, matches[1].Line);
    }

    [TestMethod]
    public async Task MaxMatchesIsPassedDownAndEnforced()
    {
        var engine = new FakeEngine(Enumerable.Range(1, 5).Select(i => Match(i)).ToList());
        var source = new FullTextRetrievalTextSource(engine, new FakeDirectories(@"D:\repo"));

        var matches = await source.SearchAsync("ws-1", "Order", 2, CancellationToken.None);

        Assert.AreEqual(2, matches.Count);
        Assert.AreEqual(2, engine.LastMaxResults);
    }

    [TestMethod]
    public async Task ZeroBudget_ReturnsEmptyWithoutTouchingEngine()
    {
        var engine = new FakeEngine([Match(1)]);
        var source = new FullTextRetrievalTextSource(engine, new FakeDirectories(@"D:\repo"));

        var matches = await source.SearchAsync("ws-1", "Order", 0, CancellationToken.None);

        Assert.AreEqual(0, matches.Count);
        Assert.AreEqual(0, engine.SearchCalls);
    }

    private static async Task AssertUnavailableAsync(Func<Task> action)
    {
        var threw = false;
        try
        {
            await action();
        }
        catch (RetrievalTextSourceUnavailableException)
        {
            threw = true;
        }

        Assert.IsTrue(
            threw,
            "数据面不可用时必须抛 RetrievalTextSourceUnavailableException；返回空集合会把'没法查'读成'没有'。");
    }
}
