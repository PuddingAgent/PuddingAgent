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
        IReadOnlyList<CodeSymbolRecord> symbols,
        IReadOnlyList<CodeRelationRecord>? relations = null,
        IReadOnlyList<CodeFileRecord>? files = null) : IRetrievalSymbolSource
    {
        public List<CodeSymbolSearchRequest> Requests { get; } = [];

        public List<IReadOnlyList<CodeRelationKind>> RelationKindRequests { get; } = [];

        public int ListIncomingCalls { get; private set; }

        public int ListOutgoingCalls { get; private set; }

        public int ListFilesCalls { get; private set; }

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

        public Task<CodeSymbolRecord?> GetSymbolAsync(
            string workspaceId,
            string projectId,
            string symbolId,
            CancellationToken cancellationToken) =>
            Task.FromResult(symbols.FirstOrDefault(s => s.SymbolId == symbolId));

        public Task<IReadOnlyList<CodeFileRecord>> ListFilesAsync(
            string workspaceId,
            string projectId,
            CancellationToken cancellationToken)
        {
            ListFilesCalls++;

            var filtered = (files ?? [])
                .Where(f => f.ProjectId == projectId)
                .ToList();

            return Task.FromResult<IReadOnlyList<CodeFileRecord>>(filtered);
        }

        public Task<IReadOnlyList<CodeRelationRecord>> ListIncomingRelationsAsync(
            string workspaceId,
            string projectId,
            string symbolId,
            IReadOnlyList<CodeRelationKind> relationKinds,
            CancellationToken cancellationToken)
        {
            ListIncomingCalls++;
            RelationKindRequests.Add(relationKinds);

            var filtered = (relations ?? [])
                .Where(r => r.TargetSymbolId == symbolId && relationKinds.Contains(r.Kind))
                .ToList();

            return Task.FromResult<IReadOnlyList<CodeRelationRecord>>(filtered);
        }

        public Task<IReadOnlyList<CodeRelationRecord>> ListOutgoingRelationsAsync(
            string workspaceId,
            string projectId,
            string symbolId,
            IReadOnlyList<CodeRelationKind> relationKinds,
            CancellationToken cancellationToken)
        {
            ListOutgoingCalls++;
            RelationKindRequests.Add(relationKinds);

            var filtered = (relations ?? [])
                .Where(r => r.SourceSymbolId == symbolId && relationKinds.Contains(r.Kind))
                .ToList();

            return Task.FromResult<IReadOnlyList<CodeRelationRecord>>(filtered);
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

        // Impact 需要多跳遍历，本刀（切片二）仍是显式不支持项；文件面板走文件面。
        var result = await port.RetrieveAsync(
            new RetrievalRequest("Order", "ws-1", RetrievalIntent.Impact),
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

    private static CodeRelationRecord Rel(
        string sourceId,
        string targetId,
        CodeRelationKind kind,
        int line = 20,
        string file = @"D:\repo\Caller.cs") =>
        new("ws-1", "proj-1", sourceId, targetId, kind, line, file);

    [TestMethod]
    public async Task Callers_ReturnsIncomingCallers_WithCallSiteEvidence()
    {
        var source = new FakeSource(
            [ActiveProject()],
            [
                Symbol("OrderService", CodeSymbolKind.Class, "sym-target", line: 10, file: @"D:\repo\OrderService.cs"),
                Symbol("CheckoutFlow", CodeSymbolKind.Class, "sym-caller", line: 3, file: @"D:\repo\CheckoutFlow.cs"),
            ],
            [Rel("sym-caller", "sym-target", CodeRelationKind.Calls, line: 42, file: @"D:\repo\CheckoutFlow.cs")]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("OrderService", "ws-1", RetrievalIntent.Callers),
            CancellationToken.None);

        Assert.IsNull(result.EmptyReason);
        Assert.AreEqual(1, result.Hits.Count);

        var hit = result.Hits[0];
        Assert.AreEqual("CheckoutFlow", hit.SymbolName);
        Assert.AreEqual(RetrievalHitKind.CallSite, hit.HitKind);
        Assert.AreEqual(RetrievalRelationKind.Calls, hit.Relation);

        // 证据在调用点，身份在声明处 —— 两者分开是“能解释为什么”的前提。
        Assert.AreEqual(42, hit.Evidence.Line);
        Assert.AreEqual(@"D:\repo\CheckoutFlow.cs", hit.Evidence.FilePath);
        Assert.AreEqual(3, hit.Identity.Line);

        // 关系种类来自冻结契约（CallRelations = Calls），不是本类自己拍脑袋。
        Assert.AreEqual(1, source.RelationKindRequests.Count);
        CollectionAssert.AreEqual(
            new List<CodeRelationKind> { CodeRelationKind.Calls },
            source.RelationKindRequests[0].ToList());
        Assert.AreEqual(1, source.ListIncomingCalls);
        Assert.AreEqual(0, source.ListOutgoingCalls);
    }

    [TestMethod]
    public async Task Callees_UsesOutgoingEdges()
    {
        var source = new FakeSource(
            [ActiveProject()],
            [
                Symbol("CheckoutFlow", CodeSymbolKind.Class, "sym-source", line: 3, file: @"D:\repo\CheckoutFlow.cs"),
                Symbol("OrderService", CodeSymbolKind.Class, "sym-target", line: 10, file: @"D:\repo\OrderService.cs"),
            ],
            [Rel("sym-source", "sym-target", CodeRelationKind.Calls, line: 44, file: @"D:\repo\CheckoutFlow.cs")]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("CheckoutFlow", "ws-1", RetrievalIntent.Callees),
            CancellationToken.None);

        Assert.AreEqual(1, result.Hits.Count);
        Assert.AreEqual("OrderService", result.Hits[0].SymbolName);
        Assert.AreEqual(RetrievalHitKind.CallSite, result.Hits[0].HitKind);
        Assert.AreEqual(0, source.ListIncomingCalls);
        Assert.AreEqual(1, source.ListOutgoingCalls);
    }

    [TestMethod]
    public async Task Implementation_RequestsExactlyThePolicyRelationKinds()
    {
        var source = new FakeSource(
            [ActiveProject()],
            [
                Symbol("IRepository", CodeSymbolKind.Interface, "sym-iface"),
                Symbol("SqlRepository", CodeSymbolKind.Class, "sym-impl", file: @"D:\repo\SqlRepository.cs"),
            ],
            [Rel("sym-impl", "sym-iface", CodeRelationKind.Implements, line: 7, file: @"D:\repo\SqlRepository.cs")]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("IRepository", "ws-1", RetrievalIntent.Implementation),
            CancellationToken.None);

        Assert.AreEqual(1, result.Hits.Count);
        Assert.AreEqual(RetrievalHitKind.Implementation, result.Hits[0].HitKind);
        Assert.AreEqual(RetrievalRelationKind.Implements, result.Hits[0].Relation);

        // 冻结契约 ImplementationRelations = [Implements, Overrides]；多写一条 Inherits 就算漂移。
        var requested = source.RelationKindRequests[0].ToList();
        CollectionAssert.AreEquivalent(
            new List<CodeRelationKind> { CodeRelationKind.Implements, CodeRelationKind.Overrides },
            requested);
    }

    [TestMethod]
    public async Task Callers_MultipleCallSitesOfSameCaller_CollapseToOneHit()
    {
        var source = new FakeSource(
            [ActiveProject()],
            [
                Symbol("OrderService", CodeSymbolKind.Class, "sym-target", file: @"D:\repo\OrderService.cs"),
                Symbol("CheckoutFlow", CodeSymbolKind.Class, "sym-caller", line: 3, file: @"D:\repo\CheckoutFlow.cs"),
            ],
            [
                Rel("sym-caller", "sym-target", CodeRelationKind.Calls, line: 42, file: @"D:\repo\CheckoutFlow.cs"),
                Rel("sym-caller", "sym-target", CodeRelationKind.Calls, line: 77, file: @"D:\repo\CheckoutFlow.cs"),
            ]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("OrderService", "ws-1", RetrievalIntent.Callers),
            CancellationToken.None);

        // 两个调用点 = 一个调用者；不得把 1 个调用者顶成 2 条命中。
        Assert.AreEqual(1, result.Hits.Count);
        Assert.AreEqual(1, result.TotalCount);
    }

    [TestMethod]
    public async Task Callers_TargetExistsButNoEdges_ReportsGenuinelyAbsent()
    {
        var source = new FakeSource(
            [ActiveProject()],
            [Symbol("OrderService", CodeSymbolKind.Class, "sym-target")]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("OrderService", "ws-1", RetrievalIntent.Callers),
            CancellationToken.None);

        Assert.AreEqual(0, result.Hits.Count);
        Assert.AreEqual(RetrievalEmptyReason.GenuinelyAbsent, result.EmptyReason);
        Assert.AreEqual(RetrievalNextStepKind.AcceptAbsence, result.NextSteps[0].Kind);
    }

    private static CodeFileRecord File(string path, string projectId = "proj-1") =>
        new("ws-1", projectId, path);

    [TestMethod]
    public async Task FileIntent_MatchesPath_ReturnsTextHitWithPathTarget()
    {
        var source = new FakeSource(
            [ActiveProject()],
            [],
            files:
            [
                File(@"D:\repo\src\OrderService.cs"),
                File(@"D:\repo\src\Cart.cs"),
            ]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("Order", "ws-1", RetrievalIntent.File),
            CancellationToken.None);

        Assert.IsNull(result.EmptyReason);
        Assert.AreEqual(1, result.Hits.Count);

        var hit = result.Hits[0];
        Assert.AreEqual("OrderService.cs", hit.SymbolName);
        Assert.AreEqual(RetrievalHitKind.TextHit, hit.HitKind);
        Assert.AreEqual(RetrievalMatchTarget.Path, hit.MatchTargets);
        Assert.AreEqual(RetrievalConfidence.Lexical, hit.Confidence);
        Assert.AreEqual(@"D:\repo\src\OrderService.cs", hit.Evidence.FilePath);

        // 文件命中不归属任何符号 —— 身份必须走 ForTextAt（HasSymbol=false）。
        Assert.IsFalse(hit.Identity.HasSymbol);

        // 文件表是逐个项目全量枚举的，不是“已经只来了一个文件”。
        Assert.AreEqual(1, source.ListFilesCalls);
        Assert.AreEqual(0, source.Requests.Count);
    }

    [TestMethod]
    public async Task FileIntent_UnmatchedFilesAreNotHits()
    {
        var source = new FakeSource(
            [ActiveProject()],
            [],
            files:
            [
                File(@"D:\repo\src\OrderService.cs"),
                File(@"D:\repo\src\Cart.cs"),
                File(@"D:\repo\src\Payment.cs"),
            ]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("Cart", "ws-1", RetrievalIntent.File),
            CancellationToken.None);

        // 枚举了三个文件，但只能返回匹配的那一个 —— 否则“全量”会被伪装成“搜到很多”。
        Assert.AreEqual(1, result.Hits.Count);
        Assert.AreEqual("Cart.cs", result.Hits[0].SymbolName);
        Assert.AreEqual(1, source.ListFilesCalls);
    }

    [TestMethod]
    public async Task FileIntent_PartialPage_SpillsAndProvidesCursor()
    {
        var source = new FakeSource(
            [ActiveProject()],
            [],
            files:
            [
                File(@"D:\repo\Order1.cs"),
                File(@"D:\repo\Order2.cs"),
                File(@"D:\repo\Order3.cs"),
            ]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("Order", "ws-1", RetrievalIntent.File, pageSize: 2),
            CancellationToken.None);

        Assert.AreEqual(2, result.Hits.Count);
        Assert.AreEqual(3, result.TotalCount);
        Assert.IsNotNull(result.Overflow);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.NextCursor));
        Assert.AreEqual(RetrievalNextStepKind.NarrowQuery, result.NextSteps[0].Kind);
    }

    [TestMethod]
    public async Task CommentIntent_RemainsUnsupported_WithoutTouchingData()
    {
        var source = new FakeSource([ActiveProject()], [], files: [File(@"D:\repo\Order.cs")]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("Order", "ws-1", RetrievalIntent.Comment),
            CancellationToken.None);

        Assert.AreEqual(0, result.Hits.Count);
        Assert.AreEqual(RetrievalEmptyReason.IntentUnsupported, result.EmptyReason);

        // 注释面背后没有文本/全文数据面；不得先枚举文件表再报“没有”。
        Assert.AreEqual(0, source.ListFilesCalls);
        Assert.AreEqual(0, source.Requests.Count);
    }
}
