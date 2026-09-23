using PuddingCodeIndex.Services.CodeIndex;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// U3-B3: what the change pipeline does with a batch — applied per file, and the retrieval answer changes
/// accordingly. The first test is the "修好了" criterion of this slice; it was run red before the fix and is
/// kept as the regression lock.
/// </summary>
[TestClass]
public sealed class CodeIndexRemovalCorrectnessTests
{
    /// <summary>
    /// A6 hard criterion: a file that was indexed, then deleted, then observed by the change pipeline must
    /// no longer be returned by the index queries.
    /// </summary>
    [TestMethod]
    public async Task Deleted_File_Is_No_Longer_Queryable_After_The_Pipeline_Handles_Its_Batch()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        var gonePath = harness.Combine("gone.cs");
        await harness.SeedIndexedFileAsync("gone.cs", "GoneClass");

        // Positive control: before the deletion the seeded file *is* queryable, so an empty result later
        // cannot be explained by a broken seed or a dead query.
        Assert.HasCount(1, await harness.SearchSymbolsAsync("GoneClass"));
        Assert.HasCount(1, await harness.Store.GetSymbolsByFileAsync(
            MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, gonePath));

        Assert.IsTrue(harness.PublishChange("gone.cs", IndexChangeKind.Deleted));
        harness.AdvancePastDebounce();
        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync());

        // The hard criterion.
        Assert.IsEmpty(
            await harness.SearchSymbolsAsync("GoneClass"),
            "a deleted file's symbols must not be returned by the index any more");
        Assert.IsEmpty(
            await harness.Store.GetSymbolsByFileAsync(
                MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, gonePath),
            "the deleted file must not keep symbol rows");
        Assert.IsFalse(
            (await harness.Store.ListFilesAsync(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId))
                .Any(file => string.Equals(file.FilePath, gonePath, StringComparison.OrdinalIgnoreCase)),
            "the deleted file must not keep a file record");

        var status = harness.Status();
        Assert.AreEqual(1, status.RemovedFileCount, "the removal was applied, not merely counted");
        Assert.AreEqual(1, status.RemovalObservationCount);
    }

    /// <summary>
    /// A5: a rename clears the old path and indexes the new one — the batch carries both halves.
    /// </summary>
    [TestMethod]
    public async Task Renamed_File_Clears_The_Old_Path_And_Indexes_The_New_One()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        var oldPath = harness.Combine("old-name.cs");
        var newPath = harness.Combine("new-name.cs");
        await harness.SeedIndexedFileAsync("old-name.cs", "RenamedClass");
        await File.WriteAllTextAsync(newPath, "class RenamedClass { }");

        Assert.IsTrue(harness.PublishChange("new-name.cs", IndexChangeKind.Renamed, oldRelativePath: "old-name.cs"));
        harness.AdvancePastDebounce();
        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync());

        Assert.IsEmpty(
            await harness.Store.GetSymbolsByFileAsync(
                MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, oldPath),
            "the old path must be cleared");
        Assert.IsFalse(
            (await harness.Store.ListFilesAsync(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId))
                .Any(file => string.Equals(file.FilePath, oldPath, StringComparison.OrdinalIgnoreCase)),
            "the old path must not keep a file record");

        Assert.HasCount(1, harness.Indexer.IndexedFiles, "the new path must be indexed");
        Assert.AreEqual(newPath, harness.Indexer.IndexedFiles[0]);

        var status = harness.Status();
        Assert.AreEqual(1, status.RemovedFileCount);
        Assert.AreEqual(1, status.IncrementallyIndexedFileCount);
        Assert.AreEqual(0, status.ScopeEscalationCount);
    }

    /// <summary>
    /// A changed path that vanished before the batch was applied ends as a removal — the same "final state
    /// wins" rule the coalescer uses — instead of lingering as a stale entry.
    /// </summary>
    [TestMethod]
    public async Task Changed_Path_That_Vanished_Before_Application_Is_Removed()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        var vanishedPath = harness.Combine("vanished.cs");
        await harness.SeedIndexedFileAsync("vanished.cs", "VanishedClass");

        // No file is created on disk: the observation is stale by the time the batch is applied.
        Assert.IsTrue(harness.PublishChange("vanished.cs", IndexChangeKind.Changed));
        harness.AdvancePastDebounce();
        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync());

        Assert.IsEmpty(await harness.SearchSymbolsAsync("VanishedClass"));
        Assert.IsEmpty(await harness.Store.GetSymbolsByFileAsync(
            MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, vanishedPath));

        var status = harness.Status();
        Assert.AreEqual(1, status.RemovedFileCount);
        Assert.IsEmpty(harness.Indexer.IndexedFiles, "a vanished path must not be handed to the indexer");
        Assert.AreEqual(0, status.ScopeEscalationCount);
    }

    /// <summary>
    /// Failure path: an indexer that declines a file must not lose the change — the scope escalates to a
    /// scope-level run, which is observable instead of silent.
    /// </summary>
    [TestMethod]
    public async Task Per_File_Indexer_Refusal_Escalates_To_A_Scope_Level_Run()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        var filePath = harness.Combine("unsupported.xyz");
        await File.WriteAllTextAsync(filePath, "not a source file of any indexer");

        harness.Indexer.IndexFileFailureMessage = "not an indexable file";

        Assert.IsTrue(harness.PublishChange("unsupported.xyz", IndexChangeKind.Created));
        harness.AdvancePastDebounce();
        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync());

        Assert.HasCount(1, harness.Indexer.IndexedFiles, "the file was attempted per file first");
        Assert.AreEqual(filePath, harness.Indexer.IndexedFiles[0]);
        Assert.AreEqual(1, harness.Indexer.CallCount, "the refusal must escalate to a scope-level run");
        Assert.IsTrue(
            string.Equals(
                Path.GetFullPath(harness.Root).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(harness.Indexer.CalledProjectPaths[0]).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase),
            "the escalated run must be a scope-level run for the scope root");

        var status = harness.Status();
        Assert.AreEqual(1, status.ScopeEscalationCount);
        Assert.AreEqual(0, status.IncrementallyIndexedFileCount);
        Assert.AreEqual(1, status.CommittedVersion, "the escalated run was committed");
    }

    /// <summary>
    /// An indexer that implements <b>only</b> the full-workspace port has no per-file capability: the batch
    /// must escalate to a scope-level run instead of losing the change (and the escalation must be visible).
    /// </summary>
    [TestMethod]
    public async Task Indexer_Without_Per_File_Capability_Escalates_To_A_Scope_Level_Run()
    {
        var workspaceOnlyIndexer = new WorkspaceOnlyCodeIndexer();
        using var harness = new MaintenanceHarness(indexer: workspaceOnlyIndexer);
        await harness.StartWithActiveScopeAsync();

        await File.WriteAllTextAsync(harness.Combine("a.cs"), "class A { }");

        Assert.IsTrue(harness.PublishChange("a.cs", IndexChangeKind.Created));
        harness.AdvancePastDebounce();
        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync());

        Assert.AreEqual(1, workspaceOnlyIndexer.CallCount, "the batch must be handled by a scope-level run");

        var status = harness.Status();
        Assert.AreEqual(1, status.ScopeEscalationCount);
        Assert.AreEqual(0, status.IncrementallyIndexedFileCount);
    }
}
