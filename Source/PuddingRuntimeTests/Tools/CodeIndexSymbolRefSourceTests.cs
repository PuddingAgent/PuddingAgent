using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services.Retrieval;
using PuddingRetrieval;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <see cref="CodeIndexSymbolRefSource"/>：把 manifest 与符号行翻译成 ref 判定要的两个事实。
/// <para>
/// 守的重点是**"不知道就说不知道"**：文件不在 manifest 里必须返回 <c>null</c>
/// （由调用方判 Stale），不得用空串或任何默认值顶替 —— 否则陈旧 ref 会被误判成 Fresh。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeIndexSymbolRefSourceTests
{
    private static SourceFingerprint Fingerprint(string hash) =>
        new(DateTimeOffset.UnixEpoch, 1, hash);

    /// <summary>路径归一化后比较：manifest 里的 `\` 与大小写不影响命中。</summary>
    [TestMethod]
    public async Task Hash_LookupIsNormalized()
    {
        var source = new CodeIndexSymbolRefSource(
            new StubIndexStore(null),
            new StubMaintenanceStore(new Dictionary<string, CodeSourceEntry>
            {
                [@"Source\\A\\B.cs"] = new(@"Source\A\B.cs", Fingerprint("hash-42"), Array.Empty<AppliedFileVersion>()),
            }));

        var hash = await source.GetFileContentHashAsync("ws-1", "proj-1", "source/a/b.cs", CancellationToken.None);

        Assert.AreEqual("hash-42", hash);
    }

    /// <summary>
    /// manifest 落库是**绝对路径**（实测 CodeSourceManifest.FilePath），ref 存的是项目内相对路径。
    /// 只比相对形态会让每一个 ref 都判 Stale —— 不报错、只返回 null，看起来完全正常。
    /// </summary>
    [TestMethod]
    public async Task Hash_AbsoluteManifestKey_MatchesRelativeRefPath()
    {
        var project = new CodeProjectRecord("ws-1", "proj-1", @"D:\Repo\Proj", CodeProjectStatus.Active);
        var source = new CodeIndexSymbolRefSource(
            new StubIndexStore(null, project),
            new StubMaintenanceStore(new Dictionary<string, CodeSourceEntry>
            {
                [@"D:\Repo\Proj\Services\Tools\Tool.cs"] =
                    new(@"D:\Repo\Proj\Services\Tools\Tool.cs", Fingerprint("hash-abs"), Array.Empty<AppliedFileVersion>()),
            }));

        var hash = await source.GetFileContentHashAsync("ws-1", "proj-1", "services/tools/tool.cs", CancellationToken.None);

        Assert.AreEqual("hash-abs", hash);
    }

    /// <summary>项目记录缺失 ⇒ 不猜绝对路径，只按原样比对；不命中就 null（判 Stale）。</summary>
    [TestMethod]
    public async Task Hash_ProjectUnknown_DoesNotGuessAbsolutePath()
    {
        var source = new CodeIndexSymbolRefSource(
            new StubIndexStore(null),
            new StubMaintenanceStore(new Dictionary<string, CodeSourceEntry>
            {
                [@"D:\Repo\Proj\services\tools\tool.cs"] =
                    new(@"D:\Repo\Proj\services\tools\tool.cs", Fingerprint("hash-abs"), Array.Empty<AppliedFileVersion>()),
            }));

        var hash = await source.GetFileContentHashAsync("ws-1", "proj-1", "services/tools/tool.cs", CancellationToken.None);

        Assert.IsNull(hash);
    }

    /// <summary>不在 manifest 中 ⇒ null（不得编造版本）。</summary>
    [TestMethod]
    public async Task Hash_UnknownFile_ReturnsNull()
    {
        var source = new CodeIndexSymbolRefSource(
            new StubIndexStore(null),
            new StubMaintenanceStore(new Dictionary<string, CodeSourceEntry>
            {
                ["source/other.cs"] = new("source/other.cs", Fingerprint("hash-x"), Array.Empty<AppliedFileVersion>()),
            }));

        var hash = await source.GetFileContentHashAsync("ws-1", "proj-1", "source/a.cs", CancellationToken.None);

        Assert.IsNull(hash);
    }

    /// <summary>没有 manifest 能力时同样返回 null —— 无法证明一致就不算一致。</summary>
    [TestMethod]
    public async Task Hash_NoMaintenanceStore_ReturnsNull()
    {
        var source = new CodeIndexSymbolRefSource(new StubIndexStore(null), null);

        var hash = await source.GetFileContentHashAsync("ws-1", "proj-1", "source/a.cs", CancellationToken.None);

        Assert.IsNull(hash);
    }

    /// <summary>符号命中 ⇒ 给出其当前行区间，供调用方消歧后重新请求。</summary>
    [TestMethod]
    public async Task Candidates_FoundSymbol_ReturnsSingleRelocation()
    {
        var symbol = new CodeSymbolRecord(
            "ws-1", "proj-1", "source/a.cs", "M:Ns.Type.Run", "Run", CodeSymbolKind.Method, 40, 42);
        var source = new CodeIndexSymbolRefSource(new StubIndexStore(symbol), null);

        var candidates = await source.FindSymbolCandidatesAsync("ws-1", "proj-1", "M:Ns.Type.Run", CancellationToken.None);

        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual("source/a.cs", candidates[0].FilePath);
        Assert.AreEqual(40, candidates[0].StartLine);
        Assert.AreEqual(42, candidates[0].EndLine);
    }

    /// <summary>符号不存在 ⇒ 空列表（不抛异常）；调用方据此报"无法定位"而不是"不存在"。</summary>
    [TestMethod]
    public async Task Candidates_MissingSymbol_ReturnsEmpty()
    {
        var source = new CodeIndexSymbolRefSource(new StubIndexStore(null), null);

        var candidates = await source.FindSymbolCandidatesAsync("ws-1", "proj-1", "M:Ns.Type.Gone", CancellationToken.None);

        Assert.IsEmpty(candidates);
    }

    /// <summary>只实现被用到的那一个成员，其余一律抛 —— 一旦适配器偷偷多调一个方法，测试立刻红。</summary>
    private sealed class StubIndexStore(CodeSymbolRecord? symbol, CodeProjectRecord? project = null) : ICodeIndexStore
    {
        public Task<CodeSymbolRecord?> GetSymbolAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) =>
            Task.FromResult(symbol);

        public Task UpsertProjectAsync(CodeProjectRecord project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveProjectAsync(string workspaceId, string projectId, bool removeIndexedArtifacts = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(string workspaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CodeProjectRecord?> GetProjectAsync(string workspaceId, string projectId, CancellationToken cancellationToken = default) => Task.FromResult(project);
        public Task UpdateProjectStatusAsync(string workspaceId, string projectId, CodeProjectStatus status, string? statusMessage = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertFilesAsync(string workspaceId, string projectId, IReadOnlyCollection<CodeFileRecord> files, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CodeFileRecord>> ListFilesAsync(string workspaceId, string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RemoveFilesAsync(string workspaceId, string projectId, IReadOnlyCollection<string> filePaths, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertSymbolsAsync(string workspaceId, string projectId, IReadOnlyCollection<CodeSymbolRecord> symbols, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CodeSymbolRecord>> SearchSymbolsAsync(CodeSymbolSearchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CodeSymbolRecord>> GetSymbolsByFileAsync(string workspaceId, string projectId, string filePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearSymbolsForFileAsync(string workspaceId, string projectId, string filePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertRelationsAsync(string workspaceId, string projectId, IReadOnlyCollection<CodeRelationRecord> relations, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CodeRelationRecord>> ListRelationsAsync(string workspaceId, string projectId, string symbolId, CodeRelationKind? relationKind = null, IReadOnlyList<CodeRelationKind>? relationKinds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CodeRelationRecord>> ListIncomingRelationsAsync(string workspaceId, string projectId, string symbolId, CodeRelationKind? relationKind = null, IReadOnlyList<CodeRelationKind>? relationKinds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearRelationsAsync(string workspaceId, string projectId, string sourceSymbolId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertReferencesAsync(string workspaceId, string projectId, IReadOnlyCollection<CodeReferenceRecord> references, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CodeReferenceRecord>> ListReferencesAsync(string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearReferencesAsync(string workspaceId, string projectId, string sourceSymbolId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubMaintenanceStore(IReadOnlyDictionary<string, CodeSourceEntry> manifest)
        : ICodeSourceMaintenanceStore
    {
        public Task<CodeSourceMaintenanceSnapshot> LoadSourceMaintenanceAsync(
            string workspaceId, string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodeSourceMaintenanceSnapshot(
                manifest,
                new CodeSourceMaintenanceLedgerState(
                    WorkspaceId: "ws-1",
                    ScopeId: "proj-1",
                    Epoch: 0,
                    DesiredVersion: 0,
                    CommittedVersion: 0,
                    ConsumerAppliedVersions: new Dictionary<string, long>(),
                    PendingRetries: new Dictionary<string, CodeSourceRetry>(),
                    ScanWatermarkUtc: null,
                    DirtyAgain: false)));

        public Task<int> SaveSourceManifestAsync(string workspaceId, string projectId, IReadOnlyCollection<CodeSourceEntry> entries, IReadOnlyCollection<string> removedFilePaths, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> SaveMaintenanceLedgerAsync(string workspaceId, string projectId, CodeSourceMaintenanceLedgerState state, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CodeSourceFileReplacementResult> ReplaceFilesAsync(string workspaceId, string projectId, IReadOnlyCollection<CodeSourceFileReplacement> replacements, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
