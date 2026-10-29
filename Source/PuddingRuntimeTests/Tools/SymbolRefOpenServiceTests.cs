using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Contracts.Retrieval;
using PuddingCodeIndex.Services.Retrieval;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <c>workspace_open</c> 的 ref 分支（symbol_ref 提案 §5）：**只有 Fresh 才按 ref 行号打开**。
/// <para>
/// 守的是三条：①Fresh 真的打开 ref 记录的那几行；②Stale 一律 <c>stale_ref</c>，
/// 即使重定位成功也**不代替打开**；③不可重定位（位置相关 id）与未接线都要显式说清楚。
/// </para>
/// </summary>
[TestClass]
public sealed class SymbolRefOpenServiceTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "symref-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllLines(Path.Combine(_root, "a.cs"), new[] { "line1", "line2", "line3", "line4", "line5" });
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>hash 一致 ⇒ 按 ref 的 StartLine..EndLine 取出那段，行号权威。</summary>
    [TestMethod]
    public async Task Fresh_OpensRefRecordedRange()
    {
        var reference = SymbolRefV1.Create("proj-1", "a.cs", "M:Ns.Type.Run", 2, 3, "hash-1", "roslyn-v1");
        var service = NewService(new FakeSymbolRefSource("hash-1", Array.Empty<SymbolRefRelocation>()));

        var result = await service.OpenAsync(Request(reference.ToToken(), WorkspaceOpenView.Excerpt), CancellationToken.None);

        Assert.IsTrue(result.Success, result.Error);
        var payload = (ExcerptPayload)result.Payload!;
        CollectionAssert.AreEqual(new[] { "line2", "line3" }, payload.Lines.ToArray());
        Assert.AreEqual(2, payload.StartLine);
        Assert.AreEqual(5, payload.TotalLineCount);
    }

    /// <summary>hash 不同 ⇒ stale_ref，且零候选时措辞是「无法定位」而不是「符号不存在」。</summary>
    [TestMethod]
    public async Task Stale_NoCandidate_ReportsStaleRef_NotOpening()
    {
        var reference = SymbolRefV1.Create("proj-1", "a.cs", "M:Ns.Type.Run", 2, 3, "hash-old", "roslyn-v1");
        var service = NewService(new FakeSymbolRefSource("hash-new", Array.Empty<SymbolRefRelocation>()));

        var result = await service.OpenAsync(Request(reference.ToToken(), WorkspaceOpenView.Excerpt), CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "stale_ref");
        StringAssert.Contains(result.Error, "无法定位");
        Assert.IsNull(result.Payload, "陈旧 ref 绝不能返回内容。");
    }

    /// <summary>唯一候选 ⇒ 仍报 stale_ref，只把候选当提示，**不代替打开**。</summary>
    [TestMethod]
    public async Task Stale_UniqueCandidate_ReportsHintButDoesNotOpen()
    {
        var reference = SymbolRefV1.Create("proj-1", "a.cs", "M:Ns.Type.Run", 2, 3, "hash-old", "roslyn-v1");
        var service = NewService(new FakeSymbolRefSource(
            "hash-new",
            new[] { new SymbolRefRelocation("a.cs", 40, 42) }));

        var result = await service.OpenAsync(Request(reference.ToToken(), WorkspaceOpenView.Excerpt), CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "stale_ref");
        StringAssert.Contains(result.Error, "候选: a.cs:40-42");
        Assert.IsNull(result.Payload);
    }

    /// <summary>位置相关 id 即使有候选也不可重定位，且不得给出「候选:」提示。</summary>
    [TestMethod]
    public async Task PositionDependentId_DoesNotOfferRelocation()
    {
        var reference = SymbolRefV1.Create(
            "proj-1", "a.cs", "Method:(T:Ns.Type)Run@1234", 2, 3, "hash-old", "roslyn-v1");
        var service = NewService(new FakeSymbolRefSource(
            "hash-new",
            new[] { new SymbolRefRelocation("a.cs", 40, 42) }));

        var result = await service.OpenAsync(Request(reference.ToToken(), WorkspaceOpenView.Excerpt), CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "@span");
        Assert.DoesNotContain("候选:", result.Error);
    }

    /// <summary>没有索引来源时显式报未接线，不静默回落到 path。</summary>
    [TestMethod]
    public async Task NoRefSource_ReportsNotWired()
    {
        var reference = SymbolRefV1.Create("proj-1", "a.cs", "M:Ns.Type.Run", 2, 3, "hash-1", "roslyn-v1");
        var service = new WorkspaceOpenService(null, new FixedScopeResolver(_root));

        var result = await service.OpenAsync(Request(reference.ToToken(), WorkspaceOpenView.Excerpt), CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "尚未接线");
    }

    /// <summary>ref v1 只支持 excerpt：其他视图必须说「只支持」，而不是拿 ref 的行号去做 outline。</summary>
    [TestMethod]
    public async Task NonExcerptView_OnRef_IsRejected()
    {
        var reference = SymbolRefV1.Create("proj-1", "a.cs", "M:Ns.Type.Run", 2, 3, "hash-1", "roslyn-v1");
        var service = NewService(new FakeSymbolRefSource("hash-1", Array.Empty<SymbolRefRelocation>()));

        var result = await service.OpenAsync(Request(reference.ToToken(), WorkspaceOpenView.Outline), CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "只支持 view=excerpt");
    }

    private WorkspaceOpenService NewService(ISymbolRefSource source) =>
        new(null, new FixedScopeResolver(_root), null, source);

    private static WorkspaceOpenRequest Request(string token, WorkspaceOpenView view) =>
        new("ws-1", ProjectId: null, Path: null, view, Ref: token);

    private sealed class FakeSymbolRefSource(string? hash, IReadOnlyList<SymbolRefRelocation> candidates)
        : ISymbolRefSource
    {
        public Task<string?> GetFileContentHashAsync(
            string workspaceId, string projectId, string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(hash);

        public Task<IReadOnlyList<SymbolRefRelocation>> FindSymbolCandidatesAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken) =>
            Task.FromResult(candidates);
    }

    private sealed class FixedScopeResolver(string root) : ICodeIndexScopeResolver
    {
        public Task<ScopeResolution?> ResolveAsync(
            string workspaceId, string? filePath = null, string? scopePath = null,
            string? scopeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<ScopeResolution?>(Build(workspaceId));

        public Task<ScopeResolution> ResolveAndEnsureAsync(
            string workspaceId, string? filePath = null, string? scopePath = null,
            string? scopeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Build(workspaceId));

        private ScopeResolution Build(string workspaceId) => new(
            new CodeIndexScope(workspaceId, "proj-1", root, ScopeState.Active, ScopeSource.Auto),
            IsAutoDetected: true,
            IsNewlyCreated: false);
    }
}
