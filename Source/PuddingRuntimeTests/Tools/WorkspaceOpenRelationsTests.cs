using PuddingCodeIndex.Contracts;
using PuddingRetrieval.Contracts;
using PuddingRetrieval;
using PuddingCodeIntelligence.Contracts;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <c>workspace_open</c> 的 <c>relations</c> 视图（规格 §8.3）。
/// <para>
/// 重点守护三条「不许猜」的规则：
/// ①入边/出边的文件行号归属不同 —— 出边不得拿当前符号的行号冒充下一跳位置；
/// ②0 条关系必须带说明（ADR-089 §8.3 hollow negation）；
/// ③未知 relation 显式报错，不猜默认种类。
/// </para>
/// </summary>
[TestClass]
public sealed class WorkspaceOpenRelationsTests
{
    private const string Root = @"C:\repo";
    private const string ProjectId = "proj-1";
    private const string SymbolId = "M:Demo.Run";
    private const string FilePath = "Demo.cs";
    private const string ContentHash = "H-FRESH";

    [TestMethod]
    public void Relations_WithoutRef_IsRejectedWithReason()
    {
        // 关系是按符号身份查的：只给 path 给不出「哪个符号」。
        var request = new WorkspaceOpenRequest(
            "ws-1",
            ProjectId: null,
            Path: FilePath,
            View: WorkspaceOpenView.Relations,
            Relation: "callers");

        var error = WorkspaceOpenRequestRules.Validate(request);

        Assert.IsNotNull(error);
        StringAssert.Contains(error, "必须给 ref");
    }

    [TestMethod]
    public void Relations_WithoutRelation_IsRejectedWithAllowedList()
    {
        var request = new WorkspaceOpenRequest(
            "ws-1",
            ProjectId: null,
            Path: null,
            View: WorkspaceOpenView.Relations,
            Ref: FreshRef());

        var error = WorkspaceOpenRequestRules.Validate(request);

        Assert.IsNotNull(error);
        StringAssert.Contains(error, "必须给 relation");
        StringAssert.Contains(error, "implementation");
    }

    [TestMethod]
    public void Relation_OnNonRelationsView_IsRejectedNotIgnored()
    {
        // 给了 relation 却跑 excerpt ⇒ 参数被当空气，必须显式拒绝。
        var request = new WorkspaceOpenRequest(
            "ws-1",
            ProjectId: null,
            Path: FilePath,
            View: WorkspaceOpenView.Excerpt,
            Relation: "callers");

        var error = WorkspaceOpenRequestRules.Validate(request);

        Assert.IsNotNull(error);
        StringAssert.Contains(error, "只用于 view=relations");
    }

    [TestMethod]
    public async Task Relations_Callers_UsesIncomingSourceSide()
    {
        var store = new StubIndexStore(
            incoming:
            [
                Relation(CodeRelationKind.Calls, "M:CallerA", @"Other\A.cs", 12),
                Relation(CodeRelationKind.Calls, "M:CallerB", @"Other\B.cs", 34),
            ]);

        var result = await Open(store, relation: "callers");

        Assert.IsTrue(result.Success, result.Error);
        var payload = (RelationsPayload)result.Payload!;
        Assert.AreEqual(2, payload.TotalCount);
        Assert.AreEqual("callers", payload.Relation);
        Assert.IsNull(payload.Note, "有结果时不应附带 0 条说明。");

        // 入边：条目是**对方**，文件行号也属于对方。
        Assert.AreEqual("M:CallerA", payload.Entries[0].SymbolId);
        Assert.AreEqual(@"Other\A.cs", payload.Entries[0].FilePath);
        Assert.AreEqual(12, payload.Entries[0].Line);
    }

    [TestMethod]
    public async Task Relations_Callees_OmitsFileAndLine()
    {
        // 出边记录里的 SourceLine 属于**当前符号**。若原样带出去，
        // 调用方会以为下游符号就长在当前这一行 —— 那是有害的假信息。
        var store = new StubIndexStore(
            outgoing:
            [
                Relation(CodeRelationKind.Calls, "M:Demo.Run", FilePath, 7, targetSymbolId: "M:Callee"),
            ]);

        var result = await Open(store, relation: "callees");

        Assert.IsTrue(result.Success, result.Error);
        var payload = (RelationsPayload)result.Payload!;
        Assert.AreEqual(1, payload.TotalCount);
        Assert.AreEqual("M:Callee", payload.Entries[0].SymbolId);
        Assert.IsNull(payload.Entries[0].FilePath, "出边不得给出当前符号的文件路径当下一跳位置。");
        Assert.IsNull(payload.Entries[0].Line, "出边不得给出当前符号的行号当下一跳位置。");
    }

    [TestMethod]
    public async Task Relations_Empty_CarriesHonestNote()
    {
        var result = await Open(new StubIndexStore(), relation: "references");

        Assert.IsTrue(result.Success, result.Error);
        var payload = (RelationsPayload)result.Payload!;
        Assert.AreEqual(0, payload.TotalCount);
        Assert.IsNotNull(payload.Note);
        StringAssert.Contains(payload.Note, "索引");
        StringAssert.Contains(payload.Note, "确实没有");
    }

    [TestMethod]
    public async Task Relations_UnknownRelation_IsRejected()
    {
        var result = await Open(new StubIndexStore(), relation: "callerss");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "不支持的 relation");
        StringAssert.Contains(result.Error, "impact");
    }

    [TestMethod]
    public void RelationsPayload_ZeroEntriesWithoutNote_Throws()
    {
        // 契约级守卫：即使服务写错，也造不出「0 条且无说明」的负载。
        var payload = new RelationsPayload([], 0, "callers");

        Assert.Throws<ArgumentException>(payload.Validate);
    }

    [TestMethod]
    public async Task Relations_WithoutIndexStore_IsRejectedExplicitly()
    {
        var service = new WorkspaceOpenService(
            new StubOutlineService(),
            new FixedScopeResolver(Root),
            mapService: null,
            refSource: new StubRefSource(ContentHash),
            indexStore: null,
            codeQueryService: null);

        var result = await service.OpenAsync(Request(relation: "callers"), CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "ICodeIndexStore is not registered");
    }

    [TestMethod]
    public async Task Relations_Impact_WithoutQueryService_IsRejectedExplicitly()
    {
        var service = new WorkspaceOpenService(
            new StubOutlineService(),
            new FixedScopeResolver(Root),
            mapService: null,
            refSource: new StubRefSource(ContentHash),
            indexStore: new StubIndexStore(),
            codeQueryService: null);

        var result = await service.OpenAsync(Request(relation: "impact"), CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "ICodeQueryService is not registered");
    }

    [TestMethod]
    public async Task Relations_StaleRef_IsRejectedBeforeQuerying()
    {
        // 陈旧 ref 连「这个符号还在不在」都无法证明 ⇒ 不得拿 symbol_id 去查关系。
        var service = new WorkspaceOpenService(
            new StubOutlineService(),
            new FixedScopeResolver(Root),
            mapService: null,
            refSource: new StubRefSource(currentHash: null),
            indexStore: new StubIndexStore(),
            codeQueryService: null);

        var result = await service.OpenAsync(Request(relation: "callers"), CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "stale_ref");
    }

    private static WorkspaceOpenRequest Request(string relation) => new(
        "ws-1",
        ProjectId: null,
        Path: null,
        View: WorkspaceOpenView.Relations,
        Ref: FreshRef(),
        Relation: relation);

    [TestMethod]
    public async Task Relations_EmptyOnContainer_ExplainsMembersNotIndex()
    {
        // 容器自身可以一条边都没有：边挂在成员上。若只说「也可能索引没建成」，
        // 调用方会去怀疑索引，而不是去看成员 —— 这正是同一问题在检索面与 open 面答案不一致的地方。
        var store = new StubIndexStore(
            anchor: new CodeSymbolRecord(
                "ws-1", ProjectId, FilePath, SymbolId, "Demo", CodeSymbolKind.Class, 7, 9));

        var result = await Open(store, relation: "callers");

        Assert.IsTrue(result.Success, result.Error);
        var payload = (RelationsPayload)result.Payload!;
        Assert.AreEqual(0, payload.TotalCount);
        Assert.IsNotNull(payload.Note);
        StringAssert.Contains(payload.Note, "容器符号");
        StringAssert.Contains(payload.Note, "成员");
        StringAssert.Contains(payload.Note, "不代表没人用它");
    }

    [TestMethod]
    public async Task Relations_EmptyOnPlainMember_KeepsIndexCaveat()
    {
        // 非容器（方法）0 条：既没有可展开的成员，就不能拿「去看成员」当建议。
        var store = new StubIndexStore(
            anchor: new CodeSymbolRecord(
                "ws-1", ProjectId, FilePath, SymbolId, "Run", CodeSymbolKind.Method, 7, 9));

        var result = await Open(store, relation: "callers");

        Assert.IsTrue(result.Success, result.Error);
        var payload = (RelationsPayload)result.Payload!;
        Assert.IsNotNull(payload.Note);
        StringAssert.Contains(payload.Note, "索引");
        Assert.IsFalse(payload.Note.Contains("容器符号"), "方法没有成员可展开，不得回容器文案。");
    }

    private static async Task<WorkspaceOpenResult> Open(StubIndexStore store, string relation)
    {
        var service = new WorkspaceOpenService(
            new StubOutlineService(),
            new FixedScopeResolver(Root),
            mapService: null,
            refSource: new StubRefSource(ContentHash),
            indexStore: store,
            codeQueryService: null);

        return await service.OpenAsync(Request(relation), CancellationToken.None);
    }

    private static string FreshRef() => SymbolRefV1.Create(
        ProjectId, FilePath, SymbolId, 7, 9, ContentHash, "gen-1").ToToken();

    private static CodeRelationRecord Relation(
        CodeRelationKind kind,
        string sourceSymbolId,
        string sourceFile,
        int sourceLine,
        string targetSymbolId = SymbolId) => new(
            "ws-1",
            ProjectId,
            sourceSymbolId,
            targetSymbolId,
            kind,
            sourceLine,
            sourceFile);

    private sealed class StubOutlineService : ICodeOutlineService
    {
        public Task<CodeOutlineOutcome> OutlineAsync(
            string fullPath, string displayPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException("relations 视图不该调用 outline 服务。");
    }

    private sealed class FixedScopeResolver(string root) : ICodeIndexScopeResolver
    {
        public Task<ScopeResolution?> ResolveAsync(
            string workspaceId,
            string? filePath = null,
            string? scopePath = null,
            string? scopeId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ScopeResolution?>(Build(workspaceId));

        public Task<ScopeResolution> ResolveAndEnsureAsync(
            string workspaceId,
            string? filePath = null,
            string? scopePath = null,
            string? scopeId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Build(workspaceId));

        private ScopeResolution Build(string workspaceId) => new(
            new CodeIndexScope(workspaceId, ProjectId, root, ScopeState.Active, ScopeSource.Auto),
            IsAutoDetected: true,
            IsNewlyCreated: false);
    }

    /// <summary>只回答那两个事实；给 null 表示「索引里没有」，即 Stale。</summary>
    private sealed class StubRefSource(string? currentHash) : ISymbolRefSource
    {
        public Task<string?> GetFileContentHashAsync(
            string workspaceId, string projectId, string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(currentHash);

        public Task<IReadOnlyList<SymbolRefRelocation>> FindSymbolCandidatesAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SymbolRefRelocation>>([]);

        /// <summary>relations 视图不读文件，所以项目根无关；给 null 正好能暴露“多调了一步”。</summary>
        public Task<string?> GetProjectRootAsync(
            string workspaceId, string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }

    /// <summary>只实现关系查询与锚点查表；其余成员一律抛 —— 服务偷偷多调一个方法，测试立刻红。</summary>
    private sealed class StubIndexStore(
        IReadOnlyList<CodeRelationRecord>? incoming = null,
        IReadOnlyList<CodeRelationRecord>? outgoing = null,
        CodeSymbolRecord? anchor = null) : ICodeIndexStore
    {
        public Task<IReadOnlyList<CodeRelationRecord>> ListIncomingRelationsAsync(
            string workspaceId,
            string projectId,
            string symbolId,
            CodeRelationKind? relationKind = null,
            IReadOnlyList<CodeRelationKind>? relationKinds = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(incoming ?? (IReadOnlyList<CodeRelationRecord>)[]);

        public Task<IReadOnlyList<CodeRelationRecord>> ListRelationsAsync(
            string workspaceId,
            string projectId,
            string symbolId,
            CodeRelationKind? relationKind = null,
            IReadOnlyList<CodeRelationKind>? relationKinds = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(outgoing ?? (IReadOnlyList<CodeRelationRecord>)[]);

        public Task UpsertProjectAsync(CodeProjectRecord project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveProjectAsync(string workspaceId, string projectId, bool removeIndexedArtifacts = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(string workspaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CodeProjectRecord?> GetProjectAsync(string workspaceId, string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateProjectStatusAsync(string workspaceId, string projectId, CodeProjectStatus status, string? statusMessage = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertFilesAsync(string workspaceId, string projectId, IReadOnlyCollection<CodeFileRecord> files, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CodeFileRecord>> ListFilesAsync(string workspaceId, string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RemoveFilesAsync(string workspaceId, string projectId, IReadOnlyCollection<string> filePaths, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertSymbolsAsync(string workspaceId, string projectId, IReadOnlyCollection<CodeSymbolRecord> symbols, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CodeSymbolRecord>> SearchSymbolsAsync(CodeSymbolSearchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CodeSymbolRecord?> GetSymbolAsync(string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) =>
            Task.FromResult(anchor);
        public Task<IReadOnlyList<CodeSymbolRecord>> GetSymbolsByFileAsync(string workspaceId, string projectId, string filePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearSymbolsForFileAsync(string workspaceId, string projectId, string filePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertRelationsAsync(string workspaceId, string projectId, IReadOnlyCollection<CodeRelationRecord> relations, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearRelationsAsync(string workspaceId, string projectId, string sourceSymbolId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertReferencesAsync(string workspaceId, string projectId, IReadOnlyCollection<CodeReferenceRecord> references, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CodeReferenceRecord>> ListReferencesAsync(string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearReferencesAsync(string workspaceId, string projectId, string sourceSymbolId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
