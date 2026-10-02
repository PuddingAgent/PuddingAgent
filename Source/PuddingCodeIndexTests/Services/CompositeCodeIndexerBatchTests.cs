using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services;

namespace PuddingCodeIndexTests.Services;

/// <summary>
/// D3 批量路由门禁：<c>CompositeCodeIndexer</c> 作为**语言侧批量接缝**的聚合实现。
/// <para>
/// 锁定的是「结果语义不混用」：没有语言认领 ⇒ <c>NotApplicable</c>（能力路由，不是失败）；
/// 语言自己报的失败 ⇒ <c>Retryable</c>（按路径退避重试，不升级整仓）；没有按文件也没有批量能力
/// ⇒ <c>ScopeRunRequired</c>（交调用方决定）；同一语言的所有文件只调用一次批量接口。
/// </para>
/// </summary>
[TestClass]
public sealed class CompositeCodeIndexerBatchTests
{
    private const string WorkspaceId = "ws-batch";
    private const string ScopeId = "scope-batch";

    private static CodeWorkspaceDescriptor Descriptor() =>
        new(WorkspaceId, ScopeId, @"C:\repo", ProjectFilePaths: []);

    private static CodeIndexBatchContext Context() =>
        new("config-1", "policy-1", Generation: 7);

    private static FakeLanguageIndexer BatchLanguage(string language, params string[] extensions)
    {
        var indexer = new FakeLanguageIndexer(language, extensions);
        return indexer;
    }

    [TestMethod]
    public async Task RoutesEachFileToItsOwningLanguageInOneBatchCall()
    {
        var csharp = BatchLanguage("C#", ".cs");
        var typescript = BatchLanguage("TypeScript/JavaScript", ".ts", ".tsx");
        var composite = new CompositeCodeIndexer([csharp, typescript]);

        var result = await composite.UpdateFilesAsync(
            Descriptor(),
            [@"C:\repo\a.cs", @"C:\repo\b.ts", @"C:\repo\c.cs"],
            Context());

        Assert.HasCount(1, csharp.BatchCalls, "同一语言的多个文件必须只调用一次批量接口");
        CollectionAssert.AreEquivalent(
            new[] { @"C:\repo\a.cs", @"C:\repo\c.cs" },
            csharp.LastBatchFilePaths!.ToArray());
        Assert.HasCount(1, typescript.BatchCalls);
        CollectionAssert.AreEquivalent(new[] { @"C:\repo\b.ts" }, typescript.LastBatchFilePaths!.ToArray());

        Assert.AreEqual("config-1", csharp.LastBatchContext!.ConfigurationFingerprint);
        Assert.AreEqual("policy-1", csharp.LastBatchContext.ParserPolicyFingerprint);
        Assert.AreEqual(7, csharp.LastBatchContext.Generation);
    }

    [TestMethod]
    public async Task ReportsNotApplicableWhenNoLanguageOwnsTheFile()
    {
        var csharp = BatchLanguage("C#", ".cs");
        var composite = new CompositeCodeIndexer([csharp]);

        var result = await composite.UpdateFilesAsync(
            Descriptor(),
            [@"C:\repo\README.md", @"C:\repo\a.cs"],
            Context());

        var markdown = result.Outcomes.Single(outcome => outcome.FilePath.EndsWith("README.md", StringComparison.Ordinal));
        Assert.AreEqual(
            CodeIndexConsumerStatus.NotApplicable,
            markdown.Status,
            "「没有语言认领」是能力路由结果，不是索引失败");
        Assert.IsNotNull(markdown.Reason);
        CollectionAssert.AreEqual(
            new[] { @"C:\repo\a.cs" },
            csharp.LastBatchFilePaths!.ToArray(),
            "不归它管的文件不得进入它的批次");
    }

    [TestMethod]
    public async Task PassesThroughTheOwnersAppliedPayloadUnchanged()
    {
        var csharp = BatchLanguage("C#", ".cs");
        var payload = new CodeFileIndexPayload(
            @"C:\repo\a.cs",
            [new CodeSymbolRecord(WorkspaceId, ScopeId, @"C:\repo\a.cs", "sym-a", "A", CodeSymbolKind.Class, 1, 3, "class A", null)],
            [],
            []);
        csharp.BatchOutcomes =
        [
            new CodeFileIndexOutcome(@"C:\repo\a.cs", CodeIndexConsumerStatus.Applied, payload),
        ];

        var result = await new CompositeCodeIndexer([csharp]).UpdateFilesAsync(
            Descriptor(),
            [@"C:\repo\a.cs"],
            Context());

        var outcome = result.Outcomes.Single();
        Assert.AreEqual(CodeIndexConsumerStatus.Applied, outcome.Status);
        Assert.AreSame(payload, outcome.Payload, "提取结果必须原样交给调用方提交（不在这里写库）");
        Assert.AreEqual("config-1", result.ConfigurationFingerprint);
    }

    [TestMethod]
    public async Task PropagatesRetryableReasonsInsteadOfEscalating()
    {
        var csharp = BatchLanguage("C#", ".cs");
        csharp.BatchOutcomes =
        [
            new CodeFileIndexOutcome(@"C:\repo\a.cs", CodeIndexConsumerStatus.Retryable, Reason: "parse failed"),
        ];

        var result = await new CompositeCodeIndexer([csharp]).UpdateFilesAsync(
            Descriptor(),
            [@"C:\repo\a.cs"],
            Context());

        var outcome = result.Outcomes.Single();
        Assert.AreEqual(CodeIndexConsumerStatus.Retryable, outcome.Status);
        Assert.AreEqual("parse failed", outcome.Reason);
    }

    [TestMethod]
    public async Task FallsBackToThePerFileCapabilityAndMapsItsResult()
    {
        // 只实现逐文件能力的语言：仍然处理，但结果要映射成 Applied / Retryable（不升级整仓）。
        var perFileOnly = new PerFileOnlyLanguageIndexer("C#", ".cs");
        var composite = new CompositeCodeIndexer([perFileOnly]);

        var applied = await composite.UpdateFilesAsync(Descriptor(), [@"C:\repo\a.cs"], Context());
        Assert.AreEqual(CodeIndexConsumerStatus.Applied, applied.Outcomes.Single().Status);
        Assert.HasCount(1, perFileOnly.IndexedFiles);

        perFileOnly.FailureMessage = "language failure";
        var retryable = await composite.UpdateFilesAsync(Descriptor(), [@"C:\repo\b.cs"], Context());
        Assert.AreEqual(CodeIndexConsumerStatus.Retryable, retryable.Outcomes.Single().Status);
        Assert.AreEqual("language failure", retryable.Outcomes.Single().Reason);
    }

    [TestMethod]
    public async Task RequiresAScopeRunWhenTheOwnerHasNoPerFileOrBatchCapability()
    {
        var workspaceOnly = new NoCapabilityLanguageIndexer("C#", ".cs");
        var composite = new CompositeCodeIndexer([workspaceOnly]);

        var result = await composite.UpdateFilesAsync(Descriptor(), [@"C:\repo\a.cs"], Context());

        var outcome = result.Outcomes.Single();
        Assert.AreEqual(CodeIndexConsumerStatus.ScopeRunRequired, outcome.Status);
        StringAssert.Contains(outcome.Reason!, "no per-file or batch capability");
    }

    [TestMethod]
    public async Task ContainsAThrowingLanguagePerPath()
    {
        var throwing = BatchLanguage("C#", ".cs");
        throwing.BatchException = new InvalidOperationException("workspace exploded");
        var typescript = BatchLanguage("TypeScript/JavaScript", ".ts");

        var result = await new CompositeCodeIndexer([throwing, typescript]).UpdateFilesAsync(
            Descriptor(),
            [@"C:\repo\a.cs", @"C:\repo\b.ts"],
            Context());

        var cs = result.Outcomes.Single(outcome => outcome.FilePath.EndsWith("a.cs", StringComparison.Ordinal));
        Assert.AreEqual(CodeIndexConsumerStatus.Retryable, cs.Status);
        StringAssert.Contains(cs.Reason!, "workspace exploded");

        var ts = result.Outcomes.Single(outcome => outcome.FilePath.EndsWith("b.ts", StringComparison.Ordinal));
        Assert.AreEqual(CodeIndexConsumerStatus.Applied, ts.Status, "一个语言炸掉不得影响其它路径/语言");
    }

    [TestMethod]
    public async Task ReportsAPathTheLanguageSilentlyDropped()
    {
        var csharp = BatchLanguage("C#", ".cs");
        csharp.BatchOutcomes = []; // 语言什么都没报

        var result = await new CompositeCodeIndexer([csharp]).UpdateFilesAsync(
            Descriptor(),
            [@"C:\repo\a.cs"],
            Context());

        var outcome = result.Outcomes.Single();
        Assert.AreEqual(CodeIndexConsumerStatus.Retryable, outcome.Status, "被静默丢掉的路径就是可能丢失的变更");
        StringAssert.Contains(outcome.Reason!, "did not report a result");
    }

    [TestMethod]
    public async Task BatchResultIsOrderedAndDeduplicated()
    {
        var csharp = BatchLanguage("C#", ".cs");
        var composite = new CompositeCodeIndexer([csharp]);

        var result = await composite.UpdateFilesAsync(
            Descriptor(),
            [@"C:\repo\c.cs", @"C:\repo\a.cs", @"C:\repo\a.cs", "   "],
            Context());

        CollectionAssert.AreEqual(
            new[] { @"C:\repo\a.cs", @"C:\repo\c.cs" },
            result.Outcomes.Select(outcome => outcome.FilePath).ToArray(),
            "顺序稳定、重复去除、空项丢弃");
        Assert.HasCount(2, csharp.LastBatchFilePaths!);
    }

    [TestMethod]
    public async Task EmptyBatchDoesNotCallAnyLanguage()
    {
        var csharp = BatchLanguage("C#", ".cs");

        var result = await new CompositeCodeIndexer([csharp]).UpdateFilesAsync(Descriptor(), [], Context());

        Assert.IsEmpty(result.Outcomes);
        Assert.IsEmpty(csharp.BatchCalls);
    }

    [TestMethod]
    public async Task ReportsTheReusedSessionKeyForDiagnostics()
    {
        var csharp = BatchLanguage("C#", ".cs");
        csharp.BatchSessionKey = "roslyn-session-1";

        var result = await new CompositeCodeIndexer([csharp]).UpdateFilesAsync(
            Descriptor(),
            [@"C:\repo\a.cs"],
            Context());

        Assert.AreEqual("C#:roslyn-session-1", result.SessionKey, "批内复用的快照标识必须可观测");
    }

    /// <summary>逐文件能力替身（没有批量能力）。</summary>
    private sealed class PerFileOnlyLanguageIndexer : ILanguageCodeIndexer, ICodeIndexFileUpdater
    {
        public PerFileOnlyLanguageIndexer(string language, params string[] extensions)
        {
            Language = language;
            SupportedExtensions = extensions;
        }

        public string Language { get; }

        public IReadOnlyCollection<string> SupportedExtensions { get; }

        public string? FailureMessage { get; set; }

        public List<string> IndexedFiles { get; } = [];

        public Task<CodeIndexResult> IndexFileAsync(
            CodeWorkspaceDescriptor workspace,
            string filePath,
            CancellationToken cancellationToken = default)
        {
            IndexedFiles.Add(filePath);

            return Task.FromResult(FailureMessage is null
                ? new CodeIndexResult(true, CodeIndexStatus.Completed, "per-file ok",
                    WorkspaceId: workspace.WorkspaceId, ProjectId: workspace.ProjectId)
                : new CodeIndexResult(false, CodeIndexStatus.Failed, FailureMessage,
                    WorkspaceId: workspace.WorkspaceId, ProjectId: workspace.ProjectId));
        }

        public Task<CodeIndexResult> IndexWorkspaceAsync(
            CodeWorkspaceDescriptor workspace,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodeIndexResult(true, CodeIndexStatus.Completed,
                WorkspaceId: workspace.WorkspaceId, ProjectId: workspace.ProjectId));

        public Task<CodeIndexResult> RemoveWorkspaceIndexAsync(
            string workspaceId,
            string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodeIndexResult(true, CodeIndexStatus.Completed));
    }

    /// <summary>全量能力替身（既不支持按文件也不支持批量）。</summary>
    private sealed class NoCapabilityLanguageIndexer : ILanguageCodeIndexer
    {
        public NoCapabilityLanguageIndexer(string language, params string[] extensions)
        {
            Language = language;
            SupportedExtensions = extensions;
        }

        public string Language { get; }

        public IReadOnlyCollection<string> SupportedExtensions { get; }

        public Task<CodeIndexResult> IndexWorkspaceAsync(
            CodeWorkspaceDescriptor workspace,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodeIndexResult(true, CodeIndexStatus.Completed,
                WorkspaceId: workspace.WorkspaceId, ProjectId: workspace.ProjectId));

        public Task<CodeIndexResult> RemoveWorkspaceIndexAsync(
            string workspaceId,
            string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodeIndexResult(true, CodeIndexStatus.Completed));
    }
}
