using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services;

namespace PuddingCodeIndexTests.Services;

/// <summary>
/// C-2: the aggregate that turns "the registered <see cref="ICodeIndexer"/>" into "all registered
/// languages". Every rule this slice froze is asserted here with fake language implementations, so the
/// suite needs neither Node.js nor Python and runs outside the host.
/// </summary>
[TestClass]
public sealed class CompositeCodeIndexerTests
{
    private const string WorkspaceId = "ws-c2";
    private const string ScopeId = "scope-c2";

    private static CodeWorkspaceDescriptor Descriptor() =>
        new(WorkspaceId, ScopeId, @"C:\repo", ProjectFilePaths: []);

    private static FakeLanguageIndexer CSharp() => new("C#", ".cs");

    private static FakeLanguageIndexer TypeScript() => new("TypeScript/JavaScript", ".ts", ".tsx", ".js", ".jsx");

    private static FakeLanguageIndexer Python() => new("Python", ".py");

    // ── IndexWorkspaceAsync ────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task IndexWorkspaceAsync_Fans_Out_To_Every_Language_With_The_Same_Descriptor()
    {
        var csharp = CSharp();
        var typescript = TypeScript();
        var python = Python();
        var descriptor = Descriptor();

        var result = await new CompositeCodeIndexer([csharp, typescript, python]).IndexWorkspaceAsync(descriptor);

        Assert.AreEqual(1, csharp.WorkspaceCalls, "every language must be asked to index the scope");
        Assert.AreEqual(1, typescript.WorkspaceCalls);
        Assert.AreEqual(1, python.WorkspaceCalls);

        Assert.AreSame(descriptor, csharp.LastWorkspaceDescriptor,
            "the descriptor must reach every language unchanged (no scope splitting)");
        Assert.AreSame(descriptor, typescript.LastWorkspaceDescriptor);
        Assert.AreSame(descriptor, python.LastWorkspaceDescriptor);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(CodeIndexStatus.Completed, result.Status);
    }

    [TestMethod]
    public async Task IndexWorkspaceAsync_Succeeds_When_At_Least_One_Language_Succeeds()
    {
        var csharp = CSharp();
        csharp.WorkspaceMessage = "Indexed 3 files.";

        var typescript = TypeScript();
        typescript.WorkspaceSuccess = false;
        typescript.WorkspaceStatus = CodeIndexStatus.Failed;
        typescript.WorkspaceMessage = "Node.js not available";

        var python = Python();
        python.WorkspaceSuccess = false;
        python.WorkspaceStatus = CodeIndexStatus.Failed;
        python.WorkspaceMessage = "Python not available (tried 'python' and 'python3')";

        var result = await new CompositeCodeIndexer([csharp, typescript, python]).IndexWorkspaceAsync(Descriptor());

        Assert.IsTrue(result.Success,
            "a missing optional toolchain is an environment fact: it must not fail a scope that C# indexed");
        Assert.AreEqual(CodeIndexStatus.Completed, result.Status);
    }

    [TestMethod]
    public async Task IndexWorkspaceAsync_Fails_When_Every_Language_Fails()
    {
        var csharp = CSharp();
        csharp.WorkspaceSuccess = false;
        csharp.WorkspaceStatus = CodeIndexStatus.Failed;
        csharp.WorkspaceMessage = "No .sln, .slnx, or .csproj found in the project.";

        var typescript = TypeScript();
        typescript.WorkspaceSuccess = false;
        typescript.WorkspaceStatus = CodeIndexStatus.Failed;
        typescript.WorkspaceMessage = "Node.js not available";

        var python = Python();
        python.WorkspaceSuccess = false;
        python.WorkspaceStatus = CodeIndexStatus.Failed;
        python.WorkspaceMessage = "Python not available (tried 'python' and 'python3')";

        var result = await new CompositeCodeIndexer([csharp, typescript, python]).IndexWorkspaceAsync(Descriptor());

        Assert.IsFalse(result.Success, "a run in which every language failed must still fail");
        Assert.AreEqual(CodeIndexStatus.Failed, result.Status);
    }

    [TestMethod]
    public async Task IndexWorkspaceAsync_Fails_When_No_Language_Is_Registered()
    {
        var result = await new CompositeCodeIndexer([]).IndexWorkspaceAsync(Descriptor());

        Assert.IsFalse(result.Success, "an aggregate with no language implementation must not report success");
        Assert.AreEqual(CodeIndexStatus.Failed, result.Status);
        Assert.IsNotNull(result.LanguageOutcomes);
        Assert.IsEmpty(result.LanguageOutcomes!);
    }

    [TestMethod]
    public async Task IndexWorkspaceAsync_Merges_Messages_In_Fan_Out_Order_With_The_First_Language_First()
    {
        var csharp = CSharp();
        csharp.WorkspaceMessage = "Indexed 3 files.";

        var typescript = TypeScript();
        typescript.WorkspaceSuccess = false;
        typescript.WorkspaceStatus = CodeIndexStatus.Failed;
        typescript.WorkspaceMessage = "Node.js not available";

        var python = Python();
        python.WorkspaceMessage = "No Python files found.";

        var result = await new CompositeCodeIndexer([csharp, typescript, python]).IndexWorkspaceAsync(Descriptor());

        Assert.AreEqual(
            "Indexed 3 files. | Node.js not available | No Python files found.",
            result.Message,
            "the merge order is the registration order: the first language's message stays first");
    }

    [TestMethod]
    public async Task IndexWorkspaceAsync_Records_A_Per_Language_Outcome()
    {
        var csharp = CSharp();
        csharp.WorkspaceMessage = "Indexed 3 files.";

        var typescript = TypeScript();
        typescript.WorkspaceSuccess = false;
        typescript.WorkspaceStatus = CodeIndexStatus.Failed;
        typescript.WorkspaceMessage = "Node.js not available";

        var python = Python();
        python.WorkspaceMessage = "No Python files found.";

        var result = await new CompositeCodeIndexer([csharp, typescript, python]).IndexWorkspaceAsync(Descriptor());

        var outcomes = result.LanguageOutcomes;
        Assert.IsNotNull(outcomes);
        Assert.HasCount(3, outcomes!);

        Assert.AreEqual("C#", outcomes[0].Language);
        Assert.IsTrue(outcomes[0].Success);
        Assert.AreEqual("Indexed 3 files.", outcomes[0].Message);

        Assert.AreEqual("TypeScript/JavaScript", outcomes[1].Language);
        Assert.IsFalse(outcomes[1].Success);
        Assert.AreEqual(CodeIndexStatus.Failed, outcomes[1].Status);
        Assert.AreEqual("Node.js not available", outcomes[1].Message);

        Assert.AreEqual("Python", outcomes[2].Language);
        Assert.IsTrue(outcomes[2].Success);
    }

    [TestMethod]
    public async Task IndexWorkspaceAsync_Isolates_A_Language_That_Throws()
    {
        var csharp = CSharp();
        csharp.WorkspaceMessage = "Indexed 3 files.";

        var typescript = TypeScript();
        typescript.WorkspaceException = new InvalidOperationException("extractor crashed");

        var python = Python();
        python.WorkspaceMessage = "No Python files found.";

        var result = await new CompositeCodeIndexer([csharp, typescript, python]).IndexWorkspaceAsync(Descriptor());

        Assert.AreEqual(1, python.WorkspaceCalls, "the languages after the throwing one must still run");
        Assert.IsTrue(result.Success, "the other languages succeeded, so the scope is not failed");

        var typescriptOutcome = result.LanguageOutcomes!.Single(outcome => outcome.Language == "TypeScript/JavaScript");
        Assert.IsFalse(typescriptOutcome.Success);
        StringAssert.Contains(typescriptOutcome.Message!, "extractor crashed");
    }

    // ── IndexFileAsync ─────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task IndexFileAsync_Dispatches_A_File_To_Its_Single_Owner()
    {
        var csharp = CSharp();
        var typescript = TypeScript();
        var python = Python();
        typescript.FileResult = new CodeIndexResult(true, CodeIndexStatus.Completed, "file indexed");

        var result = await new CompositeCodeIndexer([csharp, typescript, python])
            .IndexFileAsync(Descriptor(), @"C:\repo\src\app.ts");

        Assert.AreEqual(1, typescript.FileCalls, "the owner of .ts must be asked to index the file");
        Assert.AreEqual(0, csharp.FileCalls, "no other language may be asked");
        Assert.AreEqual(0, python.FileCalls);
        Assert.IsTrue(result.Success);
        Assert.AreEqual("file indexed", result.Message);
    }

    [TestMethod]
    public async Task IndexFileAsync_Matches_The_Extension_Case_Insensitively()
    {
        var csharp = CSharp();
        var typescript = TypeScript();

        await new CompositeCodeIndexer([csharp, typescript]).IndexFileAsync(Descriptor(), @"C:\repo\Program.CS");

        Assert.AreEqual(1, csharp.FileCalls);
        Assert.AreEqual(0, typescript.FileCalls);
    }

    [TestMethod]
    public async Task IndexFileAsync_Returns_Failed_When_No_Language_Owns_The_File()
    {
        var csharp = CSharp();
        var typescript = TypeScript();
        var python = Python();

        var result = await new CompositeCodeIndexer([csharp, typescript, python])
            .IndexFileAsync(Descriptor(), @"C:\repo\README.md");

        Assert.IsFalse(result.Success, "an unowned file must escalate, not be reported as indexed");
        Assert.AreEqual(CodeIndexStatus.Failed, result.Status);
        StringAssert.Contains(result.Message!, "README.md");
        Assert.AreEqual(0, csharp.FileCalls + typescript.FileCalls + python.FileCalls);
    }

    [TestMethod]
    public async Task IndexFileAsync_Returns_Failed_When_The_Owner_Has_No_Per_File_Capability()
    {
        var workspaceOnly = new WorkspaceOnlyLanguageIndexer("C#", ".cs");

        var result = await new CompositeCodeIndexer([workspaceOnly])
            .IndexFileAsync(Descriptor(), @"C:\repo\Thing.cs");

        Assert.IsFalse(result.Success, "no per-file capability must escalate to a scope-level run");
        Assert.AreEqual(CodeIndexStatus.Failed, result.Status);
        Assert.AreEqual(0, workspaceOnly.WorkspaceCalls, "the per-file path must not start a workspace run");
    }

    [TestMethod]
    public async Task IndexFileAsync_Returns_The_Owner_Result_Unchanged()
    {
        var owner = CSharp();
        var stamp = DateTimeOffset.Parse("2026-09-25T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        owner.FileResult = new CodeIndexResult(
            true,
            CodeIndexStatus.Completed,
            "indexed one file",
            StartedAtUtc: stamp,
            CompletedAtUtc: stamp.AddSeconds(1),
            WorkspaceId: "ws-other",
            ProjectId: "scope-other");

        var result = await new CompositeCodeIndexer([owner]).IndexFileAsync(Descriptor(), @"C:\repo\Thing.cs");

        Assert.AreSame(owner.FileResult, result, "the owner's result must not be wrapped or re-merged");
        Assert.AreEqual(owner.FileResult.Success, result.Success);
        Assert.AreEqual(owner.FileResult.Status, result.Status);
        Assert.AreEqual(owner.FileResult.Message, result.Message);
        Assert.AreEqual(owner.FileResult.StartedAtUtc, result.StartedAtUtc);
        Assert.AreEqual(owner.FileResult.CompletedAtUtc, result.CompletedAtUtc);
        Assert.AreEqual(owner.FileResult.WorkspaceId, result.WorkspaceId);
        Assert.AreEqual(owner.FileResult.ProjectId, result.ProjectId);
        Assert.AreEqual(owner.FileResult.LanguageOutcomes, result.LanguageOutcomes,
            "a per-file call is passed through, so no per-language detail is attached");
    }

    // ── RemoveWorkspaceIndexAsync ──────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task RemoveWorkspaceIndexAsync_Fans_Out_To_Every_Language()
    {
        var csharp = CSharp();
        var typescript = TypeScript();
        var python = Python();

        var result = await new CompositeCodeIndexer([csharp, typescript, python])
            .RemoveWorkspaceIndexAsync(WorkspaceId, ScopeId);

        Assert.AreEqual(1, csharp.RemoveCalls, "every language owns rows and must be asked to drop them");
        Assert.AreEqual(1, typescript.RemoveCalls);
        Assert.AreEqual(1, python.RemoveCalls);
        Assert.IsTrue(result.Success);
        Assert.AreEqual("Project index removed.", result.Message);
        Assert.IsNotNull(result.LanguageOutcomes);
        Assert.HasCount(3, result.LanguageOutcomes!);
    }

    [TestMethod]
    public async Task RemoveWorkspaceIndexAsync_Fails_When_One_Language_Fails()
    {
        var csharp = CSharp();
        var typescript = TypeScript();
        typescript.RemoveSuccess = false;
        typescript.RemoveMessage = "Node.js not available";
        var python = Python();

        var result = await new CompositeCodeIndexer([csharp, typescript, python])
            .RemoveWorkspaceIndexAsync(WorkspaceId, ScopeId);

        Assert.IsFalse(result.Success, "rows left behind by one language are stale data, so removal is all-or-nothing");
        Assert.AreEqual(CodeIndexStatus.Failed, result.Status);
        StringAssert.Contains(result.Message!, "TypeScript/JavaScript");
        StringAssert.Contains(result.Message!, "Node.js not available");
        Assert.AreEqual(1, python.RemoveCalls, "a failing language must not stop the remaining ones");
    }

    [TestMethod]
    public async Task RemoveWorkspaceIndexAsync_Fails_When_No_Language_Is_Registered()
    {
        var result = await new CompositeCodeIndexer([]).RemoveWorkspaceIndexAsync(WorkspaceId, ScopeId);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(CodeIndexStatus.Failed, result.Status);
    }
}

/// <summary>
/// Language implementation double: records every call and can be scripted to succeed, fail or throw. It
/// implements the per-file port as well, like the three production implementations do.
/// </summary>
internal sealed class FakeLanguageIndexer : ILanguageCodeIndexer, ICodeIndexFileUpdater
{
    public FakeLanguageIndexer(string language, params string[] supportedExtensions)
    {
        Language = language;
        SupportedExtensions = supportedExtensions;
    }

    public string Language { get; }

    public IReadOnlyCollection<string> SupportedExtensions { get; }

    public bool WorkspaceSuccess { get; set; } = true;

    public CodeIndexStatus WorkspaceStatus { get; set; } = CodeIndexStatus.Completed;

    public string? WorkspaceMessage { get; set; } = "Language ran.";

    public Exception? WorkspaceException { get; set; }

    public int WorkspaceCalls { get; private set; }

    public CodeWorkspaceDescriptor? LastWorkspaceDescriptor { get; private set; }

    public CodeIndexResult FileResult { get; set; } =
        new(true, CodeIndexStatus.Completed, "File indexed.");

    public int FileCalls { get; private set; }

    public bool RemoveSuccess { get; set; } = true;

    public string? RemoveMessage { get; set; } = "Project index removed.";

    public int RemoveCalls { get; private set; }

    public Task<CodeIndexResult> IndexWorkspaceAsync(
        CodeWorkspaceDescriptor workspace,
        CancellationToken cancellationToken = default)
    {
        WorkspaceCalls++;
        LastWorkspaceDescriptor = workspace;

        if (WorkspaceException is { } thrown)
            throw thrown;

        return Task.FromResult(new CodeIndexResult(
            WorkspaceSuccess,
            WorkspaceStatus,
            WorkspaceMessage,
            WorkspaceId: workspace.WorkspaceId,
            ProjectId: workspace.ProjectId));
    }

    public Task<CodeIndexResult> IndexFileAsync(
        CodeWorkspaceDescriptor workspace,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        FileCalls++;
        return Task.FromResult(FileResult);
    }

    public Task<CodeIndexResult> RemoveWorkspaceIndexAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        RemoveCalls++;
        return Task.FromResult(new CodeIndexResult(
            RemoveSuccess,
            RemoveSuccess ? CodeIndexStatus.Completed : CodeIndexStatus.Failed,
            RemoveMessage,
            WorkspaceId: workspaceId,
            ProjectId: projectId));
    }
}

/// <summary>
/// Language implementation double that only implements the full-workspace port: the per-file capability is
/// deliberately optional, and the aggregate must escalate instead of pretending the file was indexed.
/// </summary>
internal sealed class WorkspaceOnlyLanguageIndexer : ILanguageCodeIndexer
{
    public WorkspaceOnlyLanguageIndexer(string language, params string[] supportedExtensions)
    {
        Language = language;
        SupportedExtensions = supportedExtensions;
    }

    public string Language { get; }

    public IReadOnlyCollection<string> SupportedExtensions { get; }

    public int WorkspaceCalls { get; private set; }

    public Task<CodeIndexResult> IndexWorkspaceAsync(
        CodeWorkspaceDescriptor workspace,
        CancellationToken cancellationToken = default)
    {
        WorkspaceCalls++;
        return Task.FromResult(new CodeIndexResult(
            true, CodeIndexStatus.Completed, "workspace-only language",
            WorkspaceId: workspace.WorkspaceId, ProjectId: workspace.ProjectId));
    }

    public Task<CodeIndexResult> RemoveWorkspaceIndexAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CodeIndexResult(true, CodeIndexStatus.Completed));
}
