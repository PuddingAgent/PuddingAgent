using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services.CodeIndex;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// D3 更新计划门禁：把真实变更集规划成「提取 / 只重绑 / 删除 / 本轮不动」，并用**已知的符号变化**
/// 沿反向依赖扩展受影响文件。
/// <para>
/// 关键不变量：没有提取依据就不猜影响面；退避中的路径不参与扩展；同一条路径只出现一次且保留更重的动作；
/// 扩展有界并显式报告 <c>Truncated</c>（不得静默丢掉依赖方）。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeSourceUpdatePlannerTests
{
    private const string WorkspaceId = "ws-plan";
    private const string ProjectId = "scope-plan";

    private const string Owner = @"C:\repo\src\Owner.cs";
    private const string DependentA = @"C:\repo\src\DependentA.cs";
    private const string DependentB = @"C:\repo\src\DependentB.cs";

    private static CodeSourceUpdatePlanner Planner(FakeGraph graph, int maxExpansions = 512) =>
        new(graph, maxExpansions);

    private static CodeSourceChangeSet ChangeSet(params CodeSourceChange[] changes) =>
        new(changes, true, null, 0, 0, 0, 0, 0, 0, 0);

    private static CodeSourceChange Change(
        string path,
        CodeSourceAction action,
        params string[] reasons) =>
        new(path, action, CodeSourceChangeSource.MTimeScan, reasons, ["csharp"], RequiresContentHash: false);

    private static CodeFileSemanticChange Semantic(string path, params string[] symbolIds) =>
        new(path, symbolIds);

    [TestMethod]
    public async Task MapsChangeActionsToExecutionActionsInDependencyOrder()
    {
        var graph = new FakeGraph();
        var plan = await Planner(graph).PlanAsync(
            WorkspaceId,
            ProjectId,
            ChangeSet(
                Change(@"C:\repo\src\Rebind.cs", CodeSourceAction.RebindConsumers),
                Change(@"C:\repo\src\Gone.cs", CodeSourceAction.Delete),
                Change(@"C:\repo\src\Extract.cs", CodeSourceAction.ReindexContent),
                Change(@"C:\repo\src\Later.cs", CodeSourceAction.Deferred)));

        CollectionAssert.AreEqual(
            new[]
            {
                @"C:\repo\src\Extract.cs",
                @"C:\repo\src\Rebind.cs",
                @"C:\repo\src\Gone.cs",
                @"C:\repo\src\Later.cs",
            },
            plan.Items.Select(item => item.FilePath).ToArray(),
            "顺序必须是 提取 → 只重绑 → 删除 → 本轮不动");

        Assert.AreEqual(1, plan.ExtractCount);
        Assert.AreEqual(1, plan.RebindCount);
        Assert.AreEqual(1, plan.DeleteCount);
        Assert.AreEqual(1, plan.RetryCount);
        Assert.IsFalse(plan.Truncated);
    }

    [TestMethod]
    public async Task FingerprintOnlyChangesDoNotEnterThePlan()
    {
        var graph = new FakeGraph();
        var plan = await Planner(graph).PlanAsync(
            WorkspaceId,
            ProjectId,
            ChangeSet(Change(@"C:\repo\src\Meta.cs", CodeSourceAction.RefreshFingerprintOnly)));

        Assert.IsEmpty(plan.Items, "只刷新 manifest 的路径没有索引要改");
        Assert.AreEqual(0, plan.ExtractCount + plan.RebindCount + plan.DeleteCount + plan.RetryCount);
    }

    [TestMethod]
    public async Task KnownSymbolChangesExpandToDependentFilesAsRebindOnly()
    {
        var graph = new FakeGraph();
        graph.Add("sym-owner", DependentA, DependentB);

        var plan = await Planner(graph).PlanAsync(
            WorkspaceId,
            ProjectId,
            ChangeSet(Change(Owner, CodeSourceAction.ReindexContent)),
            semanticChanges: [Semantic(Owner, "sym-owner")]);

        Assert.AreEqual(1, plan.ExtractCount);
        Assert.AreEqual(2, plan.RebindCount);
        Assert.AreEqual(2, plan.DependencyExpansionCount);

        var rebinds = plan.Items.Where(item => item.Action == CodeSourceUpdateAction.RebindOnly).ToArray();
        Assert.IsTrue(rebinds.All(item => item.DependencyDepth == 1));
        Assert.IsTrue(rebinds.All(item =>
            item.Reasons.Contains(CodeSourceUpdateReasons.DependentOfChangedSymbols)));
        Assert.IsTrue(rebinds.All(item => item.Providers.Contains("csharp")), "依赖方沿用触发它的消费者");
    }

    [TestMethod]
    public async Task WithoutKnownSymbolChangesNothingIsExpanded()
    {
        var graph = new FakeGraph();
        graph.Add("sym-owner", DependentA);

        // 正文变了但调用方还没提取（没有语义变化依据）：不得猜影响面。
        var plan = await Planner(graph).PlanAsync(
            WorkspaceId,
            ProjectId,
            ChangeSet(Change(Owner, CodeSourceAction.ReindexContent)));

        Assert.AreEqual(1, plan.Items.Count);
        Assert.AreEqual(0, plan.DependencyExpansionCount);
        Assert.IsFalse(graph.WasQueried, "没有符号变化就不该查图");
    }

    [TestMethod]
    public async Task EmptyChangedSymbolListIsNotTreatedAsASemanticChange()
    {
        var graph = new FakeGraph();
        graph.Add("sym-owner", DependentA);

        var plan = await Planner(graph).PlanAsync(
            WorkspaceId,
            ProjectId,
            ChangeSet(Change(Owner, CodeSourceAction.ReindexContent)),
            semanticChanges: [Semantic(Owner)]);

        Assert.AreEqual(1, plan.Items.Count);
        Assert.IsFalse(graph.WasQueried);
    }

    [TestMethod]
    public async Task DependentsThatWillBeExtractedAnywayAreNotPlannedAsRebindOnly()
    {
        var graph = new FakeGraph();
        graph.Add("sym-owner", DependentA);

        var plan = await Planner(graph).PlanAsync(
            WorkspaceId,
            ProjectId,
            ChangeSet(
                Change(Owner, CodeSourceAction.ReindexContent),
                Change(DependentA, CodeSourceAction.ReindexContent)),
            semanticChanges: [Semantic(Owner, "sym-owner")]);

        Assert.AreEqual(2, plan.ExtractCount, "它本来就要提取：提取自带重新绑定");
        Assert.AreEqual(0, plan.RebindCount);
        Assert.IsTrue(
            plan.Items.Single(item => item.FilePath == DependentA).Action == CodeSourceUpdateAction.Extract);
    }

    [TestMethod]
    public async Task DependentsBeingDeletedAreNotPlannedAsRebindOnly()
    {
        var graph = new FakeGraph();
        graph.Add("sym-owner", DependentA);

        var plan = await Planner(graph).PlanAsync(
            WorkspaceId,
            ProjectId,
            ChangeSet(
                Change(Owner, CodeSourceAction.ReindexContent),
                Change(DependentA, CodeSourceAction.Delete)),
            semanticChanges: [Semantic(Owner, "sym-owner")]);

        Assert.AreEqual(1, plan.DeleteCount);
        Assert.AreEqual(0, plan.RebindCount);
    }

    [TestMethod]
    public async Task PathsInsideTheRetryBackoffAreNotTouchedOrExpanded()
    {
        var graph = new FakeGraph();
        graph.Add("sym-owner", DependentA);

        var plan = await Planner(graph).PlanAsync(
            WorkspaceId,
            ProjectId,
            ChangeSet(
                Owner == Owner ? Change(Owner, CodeSourceAction.ReindexContent) : throw new InvalidOperationException(),
                Change(DependentA, CodeSourceAction.RebindConsumers)),
            semanticChanges: [Semantic(Owner, "sym-owner")],
            retryPaths: [DependentA]);

        var retryItem = plan.Items.Single(item => item.FilePath == DependentA);
        Assert.AreEqual(CodeSourceUpdateAction.RetryLater, retryItem.Action);
        Assert.IsTrue(retryItem.Reasons.Contains(CodeSourceUpdateReasons.RetryBackoffActive));
        Assert.AreEqual(0, plan.RebindCount, "退避中的依赖方不重复入队");
    }

    [TestMethod]
    public async Task AFileUnderBackoffDoesNotDriveItsDependentsEither()
    {
        var graph = new FakeGraph();
        graph.Add("sym-owner", DependentA);

        var plan = await Planner(graph).PlanAsync(
            WorkspaceId,
            ProjectId,
            ChangeSet(Change(Owner, CodeSourceAction.ReindexContent)),
            semanticChanges: [Semantic(Owner, "sym-owner")],
            retryPaths: [Owner]);

        Assert.AreEqual(1, plan.Items.Count, "自己还在退避：依赖方也先不动，等它落定");
        Assert.AreEqual(0, plan.DependencyExpansionCount);
        Assert.IsFalse(graph.WasQueried);
    }

    [TestMethod]
    public async Task DependencyExpansionIsBoundedAndReportsTruncation()
    {
        var graph = new FakeGraph();
        var dependents = Enumerable.Range(0, 10).Select(index => $@"C:\repo\src\Dep{index}.cs").ToArray();
        graph.Add("sym-owner", dependents);

        var plan = await Planner(graph, maxExpansions: 3).PlanAsync(
            WorkspaceId,
            ProjectId,
            ChangeSet(Change(Owner, CodeSourceAction.ReindexContent)),
            semanticChanges: [Semantic(Owner, "sym-owner")]);

        Assert.AreEqual(3, plan.DependencyExpansionCount);
        Assert.IsTrue(plan.Truncated, "触顶必须显式报告：否则依赖方会被静默丢掉");
        Assert.AreEqual(1 + 3, plan.Items.Count);
    }

    [TestMethod]
    public async Task ExpansionUsesTheOwnersChangedSymbolsOnly()
    {
        var graph = new FakeGraph();
        graph.Add("sym-a", DependentA);
        graph.Add("sym-b", DependentB);

        var plan = await Planner(graph).PlanAsync(
            WorkspaceId,
            ProjectId,
            ChangeSet(Change(Owner, CodeSourceAction.ReindexContent)),
            semanticChanges: [Semantic(Owner, "sym-a")]);

        Assert.AreEqual(DependentA, plan.Items.Single(item => item.Action == CodeSourceUpdateAction.RebindOnly).FilePath);
        Assert.IsFalse(graph.QueriedSymbols.Contains("sym-b"), "只查真正变化的符号");
    }

    [TestMethod]
    public async Task PlannerRejectsAnInvalidExpansionLimit()
    {
        var graph = new FakeGraph();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CodeSourceUpdatePlanner(graph, maxDependencyExpansions: 0));
        Assert.Throws<ArgumentNullException>(() => new CodeSourceUpdatePlanner(null!));
    }

    /// <summary>可编程的反向依赖图替身。</summary>
    private sealed class FakeGraph : ICodeGraphDependencyQuery
    {
        private readonly Dictionary<string, List<string>> _dependents = new(StringComparer.Ordinal);

        public bool WasQueried { get; private set; }

        public List<string> QueriedSymbols { get; } = [];

        public void Add(string symbolId, params string[] dependentFilePaths)
        {
            if (!_dependents.TryGetValue(symbolId, out var paths))
            {
                paths = [];
                _dependents[symbolId] = paths;
            }

            paths.AddRange(dependentFilePaths);
        }

        public Task<IReadOnlyList<string>> ListDependentFilePathsAsync(
            string workspaceId,
            string projectId,
            IReadOnlyCollection<string> symbolIds,
            CancellationToken cancellationToken = default)
        {
            WasQueried = true;
            QueriedSymbols.AddRange(symbolIds);

            var result = symbolIds
                .Where(_dependents.ContainsKey)
                .SelectMany(symbolId => _dependents[symbolId])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Task.FromResult<IReadOnlyList<string>>(result);
        }
    }
}
