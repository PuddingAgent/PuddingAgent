using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingRetrieval;
using PuddingCodeIndex.Contracts;
using PuddingRetrieval.Contracts;
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

    private sealed class FakeTextSource(params RetrievalTextMatch[] matches) : IRetrievalTextSource
    {
        public int Calls { get; private set; }

        public int LastMaxMatches { get; private set; }

        public Task<IReadOnlyList<RetrievalTextMatch>> SearchAsync(
            string scopeId,
            string query,
            int maxMatches,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastMaxMatches = maxMatches;

            return Task.FromResult<IReadOnlyList<RetrievalTextMatch>>(matches.Take(maxMatches).ToList());
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
    public async Task Impact_Depth1_ReturnsDirectUpstreamOnly()
    {
        var source = new FakeSource(
            [ActiveProject()],
            [
                Symbol("OrderService", CodeSymbolKind.Class, "sym-1"),
                Symbol("CheckoutFlow", CodeSymbolKind.Class, "sym-a", line: 3),
            ],
            [
                new CodeRelationRecord(
                    "ws-1", "proj-1", "sym-a", "sym-1", CodeRelationKind.Calls,
                    SourceLine: 42, SourceFilePath: @"D:\repo\CheckoutFlow.cs"),
            ]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("OrderService", "ws-1", RetrievalIntent.Impact),
            CancellationToken.None);

        Assert.IsNull(result.EmptyReason);
        Assert.AreEqual(1, result.Hits.Count);
        Assert.AreEqual("CheckoutFlow", result.Hits[0].SymbolName);
        Assert.AreEqual(RetrievalHitKind.CallSite, result.Hits[0].HitKind);

        // 深度必须写进 why —— 命中结构没有 depth 字段，不写就被当成直接依赖。
        StringAssert.Contains(result.Hits[0].Why, "深度 1");
        Assert.IsFalse(result.Degraded);
    }

    [TestMethod]
    public async Task Impact_Depth2_CrossesIntermediateSymbol()
    {
        var source = new FakeSource(
            [ActiveProject()],
            [
                Symbol("OrderService", CodeSymbolKind.Class, "sym-1"),
                Symbol("OrderRepository", CodeSymbolKind.Class, "sym-b", line: 5),
                Symbol("CheckoutFlow", CodeSymbolKind.Class, "sym-a", line: 3),
            ],
            [
                new CodeRelationRecord("ws-1", "proj-1", "sym-b", "sym-1", CodeRelationKind.Calls, SourceLine: 12),
                new CodeRelationRecord("ws-1", "proj-1", "sym-a", "sym-b", CodeRelationKind.Calls, SourceLine: 42),
            ]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("OrderService", "ws-1", RetrievalIntent.Impact),
            CancellationToken.None);

        Assert.AreEqual(2, result.Hits.Count, "只展开一层的话，CheckoutFlow 会被漏掉。");
        Assert.IsTrue(result.Hits.Any(h => h.SymbolName == "OrderRepository" && h.Why.Contains("深度 1")));
        Assert.IsTrue(result.Hits.Any(h => h.SymbolName == "CheckoutFlow" && h.Why.Contains("深度 2")));
    }

    [TestMethod]
    public async Task Impact_CycleBackToRoot_DoesNotReEmitRoot()
    {
        var source = new FakeSource(
            [ActiveProject()],
            [
                Symbol("OrderService", CodeSymbolKind.Class, "sym-1"),
                Symbol("CheckoutFlow", CodeSymbolKind.Class, "sym-a", line: 3),
            ],
            [
                new CodeRelationRecord("ws-1", "proj-1", "sym-a", "sym-1", CodeRelationKind.Calls, SourceLine: 42),
                // 反向边：BFS 如果不记 visited，会从 a 再走回 1，无限循环。
                new CodeRelationRecord("ws-1", "proj-1", "sym-1", "sym-a", CodeRelationKind.Calls, SourceLine: 7),
            ]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("OrderService", "ws-1", RetrievalIntent.Impact),
            CancellationToken.None);

        Assert.AreEqual(1, result.Hits.Count, "起点自己不得作为它的影响者被返回。");
        Assert.AreEqual("CheckoutFlow", result.Hits[0].SymbolName);
    }

    [TestMethod]
    public async Task Impact_DepthLimitReached_MarksDegraded()
    {
        // 链式三层：OrderService <- Repository <- CheckoutFlow <- Controller
        var source = new FakeSource(
            [ActiveProject()],
            [
                Symbol("OrderService", CodeSymbolKind.Class, "sym-1"),
                Symbol("OrderRepository", CodeSymbolKind.Class, "sym-b"),
                Symbol("CheckoutFlow", CodeSymbolKind.Class, "sym-a"),
                Symbol("OrderController", CodeSymbolKind.Class, "sym-c"),
            ],
            [
                new CodeRelationRecord("ws-1", "proj-1", "sym-b", "sym-1", CodeRelationKind.Calls),
                new CodeRelationRecord("ws-1", "proj-1", "sym-a", "sym-b", CodeRelationKind.Calls),
                new CodeRelationRecord("ws-1", "proj-1", "sym-c", "sym-a", CodeRelationKind.Calls),
            ]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("OrderService", "ws-1", RetrievalIntent.Impact),
            CancellationToken.None);

        Assert.AreEqual(2, result.Hits.Count);
        Assert.IsTrue(
            result.Degraded,
            "深度封顶且仍有未展开节点时必须标 degraded —— 否则调用方会以为影响就这些。");
        StringAssert.Contains(result.DegradedReason, "深度 2");
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
        // R0-01（ADR-089 §8.x）：按名字查关系面时，目标身份并未经 ref 确认 ⇒ 只能算「未找到证据」，
        // 不能下「没人调用它」的结论；下一步必须是换面，而不是接受缺失。
        Assert.AreEqual(RetrievalNextStepKind.UseAlternativeFace, result.NextSteps[0].Kind);
        Assert.AreEqual(RetrievalClaimKind.NoCandidates, result.Claim!.Kind);
    }

    [TestMethod]
    public async Task Empty_NaturalLanguageQuery_DeclaresLexicalFace_AndDoesNotClaimNonexistence()
    {
        // 实测缺陷：中文概念查询「签发符号引用的令牌」被判成 GenuinelyAbsent 且只回
        // 「确实没有匹配项」——Agent 会读成"这个概念不存在"，而它就在 SymbolRefIssuer 里。
        // 结论真实（词面确实没命中），但覆盖面没说清，等价于误导。
        var source = new FakeSource([ActiveProject()], Array.Empty<CodeSymbolRecord>());
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("签发符号引用的令牌", "ws-1", RetrievalIntent.Auto),
            CancellationToken.None);

        // R0-01：auto 零结果一律禁止 AcceptAbsence；且否决面必须可机器判定，不能只靠文案。
        Assert.AreEqual(RetrievalNextStepKind.UseAlternativeFace, result.NextSteps[0].Kind);
        Assert.AreEqual(RetrievalClaimKind.None, result.Claim!.Kind);
        CollectionAssert.Contains(
            result.Claim.UnsearchedSurfaces.ToArray(),
            RetrievalClaim.SurfaceVector);
        var text = result.NextSteps[0].Text;
        StringAssert.Contains(text, "语义召回");
        StringAssert.Contains(text, "不代表该概念不存在");
        StringAssert.Contains(text, "英文标识符");
    }

    [TestMethod]
    public async Task Empty_IdentifierQuery_DeclaresLexicalFace_WithoutTellingAgentToRename()
    {
        // 标识符形态的查询：词面没命中就是真没有，不必劝 Agent 换名字。
        var source = new FakeSource([ActiveProject()], Array.Empty<CodeSymbolRecord>());
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("OrderService", "ws-1", RetrievalIntent.Auto),
            CancellationToken.None);

        var text = result.NextSteps[0].Text;
        StringAssert.Contains(text, "词面确实无匹配");
        StringAssert.Contains(text, "语义召回");
        Assert.IsFalse(text.Contains("不像代码标识符"), "标识符查询不该被劝改名字。");
    }
    [TestMethod]
    public async Task Callers_Anchored_ResolvesBySymbolId_WithoutAnyNameSearch()
    {
        // 两个同名符号（两个项目各一个）—— 名字检索无法区分，锚点必须直接取对的那个。
        var source = new FakeSource(
            [ActiveProject()],
            [
                Symbol("OrderService", CodeSymbolKind.Class, "sym-wanted", line: 10, file: @"D:\repo\A.cs"),
                Symbol("OrderService", CodeSymbolKind.Class, "sym-other", line: 90, file: @"D:\repo\B.cs"),
                Symbol("CheckoutFlow", CodeSymbolKind.Class, "sym-caller", line: 3, file: @"D:\repo\CheckoutFlow.cs"),
            ],
            [Rel("sym-caller", "sym-wanted", CodeRelationKind.Calls, line: 42, file: @"D:\repo\CheckoutFlow.cs")]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest(
                query: string.Empty,
                scopeId: "ws-1",
                intent: RetrievalIntent.Callers,
                anchor: new SymbolAnchor("proj-1", "sym-wanted")),
            CancellationToken.None);

        Assert.IsNull(result.EmptyReason);
        Assert.AreEqual(1, result.Hits.Count);
        Assert.AreEqual("CheckoutFlow", result.Hits[0].SymbolName);
        Assert.AreEqual(42, result.Hits[0].Evidence.Line);

        // 关键断言：有锚点就**不**得再走名字检索（否则同名符号又会把目标换成别人）。
        Assert.AreEqual(0, source.Requests.Count);
    }

    [TestMethod]
    public async Task Callers_AnchoredSymbolMissingInIndex_ReportsNotIndexed_NotGenuinelyAbsent()
    {
        var source = new FakeSource(
            [ActiveProject()],
            [Symbol("OrderService", CodeSymbolKind.Class, "sym-present")]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest(
                query: string.Empty,
                scopeId: "ws-1",
                intent: RetrievalIntent.Callers,
                anchor: new SymbolAnchor("proj-1", "sym-missing")),
            CancellationToken.None);

        Assert.AreEqual(0, result.Hits.Count);

        // “索引里找不到” ≠ “确实没人调用它”：前者是缺口，后者是否定代码存在。
        Assert.AreEqual(RetrievalEmptyReason.NotIndexed, result.EmptyReason);
        Assert.IsTrue(result.NextSteps.Count >= 1);
        Assert.AreEqual(0, source.Requests.Count);
    }

    [TestMethod]
    public async Task Callers_AnchoredOnContainerWithNoEdges_SaysMembersNotExpanded_NotSilentAbsence()
    {
        // 实测动机（2026-10-30 线上验收）：对类 T:…SymbolRefIssuer 签发的 ref 查 references，
        // 返回 GenuinelyAbsent /「确实没有匹配项」，而同一个名字的名字检索有 11 条命中 ——
        // 因为关系边挂在成员（.ctor / FromManifest / Issue…）上，容器自身零边。
        // 这种“空洞否定”会让 Agent 判定“没人用它”并打转，必须把边界说出来。
        var source = new FakeSource(
            [ActiveProject()],
            [Symbol("OrderService", CodeSymbolKind.Class, "sym-container")]);
        var port = new RetrievalPortV1(source);

        var result = await port.RetrieveAsync(
            new RetrievalRequest(
                query: string.Empty,
                scopeId: "ws-1",
                intent: RetrievalIntent.References,
                anchor: new SymbolAnchor("proj-1", "sym-container")),
            CancellationToken.None);

        Assert.AreEqual(0, result.Hits.Count);
        Assert.AreEqual(RetrievalEmptyReason.GenuinelyAbsent, result.EmptyReason);

        // 不得只回“接受缺失”：必须换面，并把“成员未展开”说出来。
        Assert.AreEqual(RetrievalNextStepKind.UseAlternativeFace, result.NextSteps[0].Kind);
        StringAssert.Contains(result.NextSteps[0].Text, "成员");
        StringAssert.Contains(result.NextSteps[0].Text, "不代表没人用它");
        Assert.AreEqual(0, source.Requests.Count);
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

        // 注释面没有注入文本数据面；不得先枚举文件表再报“没有”。
        Assert.AreEqual(0, source.ListFilesCalls);
        Assert.AreEqual(0, source.Requests.Count);
    }

    [TestMethod]
    public async Task CommentIntent_WithTextSource_SplitsCommentAndBodyHits()
    {
        var source = new FakeSource([ActiveProject()], []);
        var text = new FakeTextSource(
            new RetrievalTextMatch(@"D:\repo\B.cs", 30, "var orderService = new OrderService();"),
            new RetrievalTextMatch(@"D:\repo\A.cs", 12, "// OrderService 负责下单"));
        var port = new RetrievalPortV1(source, text);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("Order", "ws-1", RetrievalIntent.Comment),
            CancellationToken.None);

        Assert.IsNull(result.EmptyReason);
        Assert.AreEqual(1, text.Calls);
        Assert.AreEqual(2, result.Hits.Count);

        // CommentLayers 把 DocMention 排在 TextHit 前面 —— 真正的“提到过”优先于
        // “只是同名代码”。
        Assert.AreEqual(RetrievalHitKind.DocMention, result.Hits[0].HitKind);
        Assert.AreEqual(RetrievalMatchTarget.DocComment, result.Hits[0].MatchTargets);
        Assert.AreEqual(12, result.Hits[0].Evidence.Line);

        Assert.AreEqual(RetrievalHitKind.TextHit, result.Hits[1].HitKind);
        Assert.AreEqual(RetrievalMatchTarget.Body, result.Hits[1].MatchTargets);

        // 文本命中不归属符号。
        Assert.IsFalse(result.Hits[0].Identity.HasSymbol);
    }

    [TestMethod]
    public async Task CommentIntent_TextSourceReachesCap_MarksDegraded()
    {
        var many = Enumerable.Range(1, 200)
            .Select(i => new RetrievalTextMatch($@"D:\repo\F{i}.cs", i, "// OrderService note"))
            .ToArray();
        var source = new FakeSource([ActiveProject()], []);
        var text = new FakeTextSource(many);
        var port = new RetrievalPortV1(source, text);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("Order", "ws-1", RetrievalIntent.Comment),
            CancellationToken.None);

        // 到达上限时必须标 degraded：totalCount 只能当下限用，不能声称“就这么多”。
        Assert.IsTrue(result.Degraded);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.DegradedReason));
        Assert.AreEqual(200, text.LastMaxMatches);
    }

    private sealed class ThrowingTextSource(Exception error) : IRetrievalTextSource
    {
        public Task<IReadOnlyList<RetrievalTextMatch>> SearchAsync(
            string scopeId,
            string query,
            int maxMatches,
            CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<RetrievalTextMatch>>(error);
    }

    [TestMethod]
    public async Task CommentIntent_TextSourceUnavailable_ReportsNotIndexed_NotAbsent()
    {
        var source = new FakeSource([ActiveProject()], []);
        var text = new ThrowingTextSource(
            new RetrievalTextSourceUnavailableException("全文索引未建立"));
        var port = new RetrievalPortV1(source, text);

        var result = await port.RetrieveAsync(
            new RetrievalRequest("Order", "ws-1", RetrievalIntent.Comment),
            CancellationToken.None);

        // “没法查” 不得被读成 “确实没有”。
        Assert.AreEqual(RetrievalEmptyReason.NotIndexed, result.EmptyReason);
        Assert.AreNotEqual(RetrievalEmptyReason.GenuinelyAbsent, result.EmptyReason);
        Assert.AreEqual(RetrievalNextStepKind.RebuildIndex, result.NextSteps[0].Kind);
    }

    [TestMethod]
    public async Task CommentIntent_TextSourceThrowsOtherException_Propagates()
    {
        var source = new FakeSource([ActiveProject()], []);
        var text = new ThrowingTextSource(new InvalidOperationException("引擎炸了"));
        var port = new RetrievalPortV1(source, text);

        // 只捕“不可用”一种；其余异常必须上抛 —— 吞错同样会造出空洞否定。
        var threw = false;
        try
        {
            await port.RetrieveAsync(
                new RetrievalRequest("Order", "ws-1", RetrievalIntent.Comment),
                CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "非“不可用”异常必须上抛，不得被吞成空结果");
    }
}
