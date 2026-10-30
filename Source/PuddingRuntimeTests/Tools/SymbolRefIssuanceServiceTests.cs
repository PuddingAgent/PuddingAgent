using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Contracts.Retrieval;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 宿主侧桥接的契约测试：证明「存储出问题时降级而不是打断搜索」与「不签发时说明原因」。
/// <para>
/// 签发判据本身由 <c>SymbolRefIssuerTests</c> 守护，这里只守胶水层的行为。
/// </para>
/// </summary>
[TestClass]
public sealed class SymbolRefIssuanceServiceTests
{
    private const string WorkspaceId = "ws-1";
    private const string ProjectId = "proj-1";
    private const string ProjectRoot = @"D:\proj\alpha";
    private const string SourcePath = @"D:\proj\alpha\src\a.cs";

    private static CodeSymbolDetail Hit(string filePath = SourcePath, string symbolId = "M:Ns.Type.Run") =>
        new(new CodeSymbolRecord(
            WorkspaceId,
            ProjectId,
            filePath,
            symbolId,
            "Run",
            CodeSymbolKind.Method,
            2,
            3));

    private static CodeProjectRecord Project() =>
        new(WorkspaceId, ProjectId, ProjectRoot, CodeProjectStatus.Active);

    private static CodeSourceEntry Entry(string path, string contentHash) =>
        new(
            path,
            new SourceFingerprint(DateTimeOffset.UnixEpoch, 12, contentHash),
            Array.Empty<AppliedFileVersion>());

    [TestMethod]
    public async Task IssueAsync_AuthoritativeHit_ProducesToken()
    {
        var store = new StubStore(new Dictionary<string, CodeSourceEntry>(StringComparer.Ordinal)
        {
            [SourcePath] = Entry(SourcePath, "hash-1"),
        });

        var service = new SymbolRefIssuanceService(store);
        var result = await service.IssueAsync(
            WorkspaceId,
            new[] { Hit() },
            new[] { Project() },
            CancellationToken.None);

        Assert.IsNull(result.Note);
        Assert.AreEqual(1, result.IssuedCount);

        var token = result.Tokens[(ProjectId, "M:Ns.Type.Run")];
        var parsed = SymbolRefV1.Parse(token);
        Assert.AreEqual("hash-1", parsed.ContentHash);
        Assert.AreEqual(SymbolRefIssuanceService.ProviderGeneration, parsed.ProviderGeneration);
    }

    [TestMethod]
    public async Task IssueAsync_MissingManifestEntry_IssuesNothingWithoutNote()
    {
        var service = new SymbolRefIssuanceService(new StubStore(
            new Dictionary<string, CodeSourceEntry>(StringComparer.Ordinal)));
        var result = await service.IssueAsync(
            WorkspaceId,
            new[] { Hit() },
            new[] { Project() },
            CancellationToken.None);

        Assert.AreEqual(0, result.IssuedCount);
        // 没有全局故障，只是这条命中没有可证明的源版本 ⇒ 顶层不需要说明，逐项是 null。
        Assert.IsNull(result.Note);
    }

    /// <summary>
    /// 存储炸掉时搜索必须照常返回：ref 拿不到是可接受的降级，把搜索打挂不是。
    /// 同时必须留下说明，否则调用方会以为「这些符号本来就没有 ref」。
    /// </summary>
    [TestMethod]
    public async Task IssueAsync_StoreThrows_DegradesWithNote()
    {
        var service = new SymbolRefIssuanceService(new ThrowingStore());

        var result = await service.IssueAsync(
            WorkspaceId,
            new[] { Hit() },
            new[] { Project() },
            CancellationToken.None);

        Assert.AreEqual(0, result.IssuedCount);
        Assert.IsNotNull(result.Note);
        StringAssert.Contains(result.Note, nameof(InvalidOperationException));
    }

    [TestMethod]
    public async Task IssueAsync_NoStore_ReturnsEmpty()
    {
        var service = new SymbolRefIssuanceService(maintenanceStore: null);

        var result = await service.IssueAsync(
            WorkspaceId,
            new[] { Hit() },
            new[] { Project() },
            CancellationToken.None);

        Assert.AreEqual(0, result.IssuedCount);
        Assert.IsNull(result.Note);
    }

    /// <summary>
    /// token 超 <see cref="SymbolRefV1.MaxTokenLength"/> 上限时**必须留下逐项原因**。
    /// <para>
    /// 实测动机（2026-10-30）：<c>SymbolRefIssuer.FromManifest</c> 这类泛型方法把 symbol_id 撑到
    /// 186 字符，签出的 token 达 558 字节 ⇒ <c>ToToken</c> 抛 ArgumentException ⇒ 签发器 Skip。
    /// 在此修复之前工具层把原因丢掉，调用方只看到 <c>symbol_ref=null</c>，无法区分
    /// 「本来就没有 ref」与「这个符号签不出来」。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task IssueAsync_TokenTooLong_RecordsPerHitSkipReason()
    {
        var longSymbolId = "M:Ns.Type.Run(" + new string('X', 400) + ")";
        var store = new StubStore(new Dictionary<string, CodeSourceEntry>(StringComparer.Ordinal)
        {
            [SourcePath] = Entry(SourcePath, "hash-1"),
        });

        var service = new SymbolRefIssuanceService(store);
        var result = await service.IssueAsync(
            WorkspaceId,
            new[] { Hit(symbolId: longSymbolId) },
            new[] { Project() },
            CancellationToken.None);

        Assert.AreEqual(0, result.IssuedCount);
        Assert.IsNull(result.Note, "这是逐项失败，不是全局故障");
        Assert.IsTrue(
            result.Skips.ContainsKey((ProjectId, longSymbolId)),
            "签不出来必须给逐项原因，不能只给 null");
        StringAssert.Contains(result.Skips[(ProjectId, longSymbolId)], "512");
    }

    private sealed class StubStore(IReadOnlyDictionary<string, CodeSourceEntry> manifest)
        : ICodeSourceMaintenanceStore
    {
        public Task<CodeSourceMaintenanceSnapshot> LoadSourceMaintenanceAsync(
            string workspaceId,
            string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodeSourceMaintenanceSnapshot(
                manifest,
                new CodeSourceMaintenanceLedgerState(
                    WorkspaceId: workspaceId,
                    ScopeId: projectId,
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

    private sealed class ThrowingStore : ICodeSourceMaintenanceStore
    {
        public Task<CodeSourceMaintenanceSnapshot> LoadSourceMaintenanceAsync(
            string workspaceId,
            string projectId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("stub: manifest unavailable");

        public Task<int> SaveSourceManifestAsync(string workspaceId, string projectId, IReadOnlyCollection<CodeSourceEntry> entries, IReadOnlyCollection<string> removedFilePaths, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> SaveMaintenanceLedgerAsync(string workspaceId, string projectId, CodeSourceMaintenanceLedgerState state, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CodeSourceFileReplacementResult> ReplaceFilesAsync(string workspaceId, string projectId, IReadOnlyCollection<CodeSourceFileReplacement> replacements, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
