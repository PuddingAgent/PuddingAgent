using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Contracts.Retrieval;
using PuddingCodeIndex.Services.Retrieval;

namespace PuddingCodeIndexTests.Services.Retrieval;

/// <summary>
/// <see cref="RetrievalPortV1"/> 的边界断言（R6 B1 切片一）。
/// <para>
/// 断言重点不是"能返回命中"，而是三条**失败即可见**的硬约束：
/// ①不支持的意图必须报 <see cref="RetrievalEmptyReason.IntentUnsupported"/> 并给替代方案，
/// 不得伪装成空命中；②跨 scope 重复符号必须去重；③超出页大小必须落盘 + 给游标。
/// </para>
/// </summary>
[TestClass]
public sealed class RetrievalPortV1Tests
{
    private sealed class FakeSource(
        IReadOnlyList<CodeProjectRecord> projects,
        IReadOnlyList<CodeSymbolRecord> symbols) : IRetrievalSymbolSource
    {
        public List<CodeSymbolSearchRequest> Requests { get; } = [];

        public Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(
            string scopeId,
            CancellationToken cancellationToken) =>
            Task.FromResult(projects);

        public Task<IReadOnlyList<CodeSymbolRecord>> SearchSymbolsAsync(
            CodeSymbolSearchRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);

            var query = request.Query;
            var filtered = symbols
                .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Where(s => request.Kinds is null || request.Kinds.Count == 0 || request.Kinds.Contains(s.Kind))
                .Skip(request.Skip)
                .Take(request.Limit)
                .ToList();

            return Task.FromResult<IReadOnlyList<CodeSymbolRecord>>(filtered);
        }
    }

    private static CodeProjectRecord ActiveProject(string id = "proj-1", string path = @"D:\repo") =>
        new("ws-1", id, path, CodeProjectStatus.Active);

    private static CodeSymbolRecord Symbol(
        string name,
        CodeSymbolKind kind,
        string symbolId,
        int line = 10,
        string file = @"D:\repo\Sample.cs") =>
        new("ws-1", "proj-1", file, symbolId, name, kind, line, line + 5);

    [TestMethod]
    public async Task TypeIntent_RequestsTypeKindsAndReturnsDeclarations()
    {
        var source = new FakeSource([ActiveProject()], [Symbol("OrderService", CodeSymbolKind.Class, "sym-1")]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("Order", "ws-1", RetrievalIntent.Type),
            CancellationToken.None);

        Assert.AreEqual(1, source.Requests.Count);
        var request = source.Requests[0];
        Assert.IsNotNull(request.Kinds);
        Assert.AreEqual(5, request.Kinds!.Count);
        CollectionAssert.Contains(request.Kinds.ToList(), CodeSymbolKind.Class);
        CollectionAssert.DoesNotContain(request.Kinds.ToList(), CodeSymbolKind.Method);

        Assert.AreEqual(1, result.Hits.Count);
        var hit = result.Hits[0];
        Assert.AreEqual(RetrievalHitKind.Declaration, hit.HitKind);
        Assert.AreEqual(RetrievalMatchTarget.SymbolName, hit.MatchTargets);
        Assert.IsFalse(string.IsNullOrWhiteSpace(hit.Why));
        Assert.IsNull(result.EmptyReason);
    }

    [TestMethod]
    public async Task UnsupportedIntent_ReportsIntentUnsupported_NotFakeEmptyHit()
    {
        var source = new FakeSource([ActiveProject()], [Symbol("OrderService", CodeSymbolKind.Class, "sym-1")]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("Order", "ws-1", RetrievalIntent.Callers),
            CancellationToken.None);

        Assert.AreEqual(0, result.Hits.Count);
        Assert.AreEqual(RetrievalEmptyReason.IntentUnsupported, result.EmptyReason);
        Assert.AreEqual(1, result.NextSteps.Count);
        Assert.AreEqual(RetrievalNextStepKind.UseAlternativeFace, result.NextSteps[0].Kind);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.NextSteps[0].Alternative));

        // 不支持的能力不应先打数据面 —— 否则"没实现"会被读成"查过但没有"。
        Assert.AreEqual(0, source.Requests.Count);
    }

    [TestMethod]
    public async Task NoProjects_ReportsNotIndexed_AndSuggestsRebuild()
    {
        var source = new FakeSource([], []);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("Order", "ws-1", RetrievalIntent.Type),
            CancellationToken.None);

        Assert.AreEqual(RetrievalEmptyReason.NotIndexed, result.EmptyReason);
        Assert.AreEqual(1, result.NextSteps.Count);
        Assert.AreEqual(RetrievalNextStepKind.RebuildIndex, result.NextSteps[0].Kind);
    }

    [TestMethod]
    public async Task DuplicateSymbolIdentity_IsDeduplicatedToOneHit()
    {
        // 同一 symbol_id + 文件 + 行在两个 project 下各出现一次（真实的多 scope 重复）。
        var source = new FakeSource(
            [ActiveProject("proj-1"), ActiveProject("proj-2", @"D:\repo\sub")],
            [
                Symbol("OrderService", CodeSymbolKind.Class, "sym-dup"),
                Symbol("OrderService", CodeSymbolKind.Class, "sym-dup"),
            ]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("Order", "ws-1", RetrievalIntent.Type),
            CancellationToken.None);

        Assert.AreEqual(1, result.Hits.Count);
    }

    [TestMethod]
    public async Task PartialPage_SpillsAndProvidesCursor_InsteadOfSilentTruncation()
    {
        var symbols = Enumerable.Range(1, 5)
            .Select(i => Symbol($"OrderService{i}", CodeSymbolKind.Class, $"sym-{i}", line: i))
            .ToList();
        var source = new FakeSource([ActiveProject()], symbols);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("Order", "ws-1", RetrievalIntent.Type, pageSize: 2),
            CancellationToken.None);

        Assert.AreEqual(2, result.Hits.Count);
        Assert.IsTrue(result.TotalCount > result.Hits.Count);
        Assert.IsNotNull(result.Overflow);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.NextCursor));
        Assert.IsFalse(result.Distribution.IsEmpty);
        Assert.AreEqual(1, result.NextSteps.Count);
        Assert.AreEqual(RetrievalNextStepKind.NarrowQuery, result.NextSteps[0].Kind);
    }
}
