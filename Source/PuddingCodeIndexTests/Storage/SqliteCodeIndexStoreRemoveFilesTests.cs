using Microsoft.Data.Sqlite;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Storage;

namespace PuddingCodeIndexTests.Storage;

/// <summary>
/// U3-B3: the per-file removal primitive the change pipeline deletes with. It has to remove the file record
/// <b>and</b> everything bound to it, be idempotent, and never leave a half-removed file behind.
/// </summary>
[TestClass]
public sealed class SqliteCodeIndexStoreRemoveFilesTests
{
    private const string WorkspaceId = "ws-remove";
    private const string ProjectId = "proj-remove";

    [TestMethod]
    public async Task RemoveFilesAsync_Removes_File_Record_Symbols_Relations_And_References()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;

        var fileAlpha = fixture.File("Alpha.cs");
        var fileBeta = fixture.File("Beta.cs");
        var alpha = Symbol(fileAlpha, "sym-alpha", "AlphaClass");
        var beta = Symbol(fileBeta, "sym-beta", "BetaClass");

        await store.UpsertFilesAsync(WorkspaceId, ProjectId, [File(fileAlpha), File(fileBeta)]);
        await store.UpsertSymbolsAsync(WorkspaceId, ProjectId, [alpha, beta]);
        await store.UpsertRelationsAsync(WorkspaceId, ProjectId, [
            new CodeRelationRecord(
                WorkspaceId, ProjectId, alpha.SymbolId, beta.SymbolId, CodeRelationKind.Calls, 3, fileAlpha,
                DateTimeOffset.UtcNow)
        ]);
        await store.UpsertReferencesAsync(WorkspaceId, ProjectId, [
            new CodeReferenceRecord(
                WorkspaceId, ProjectId, alpha.SymbolId, beta.SymbolId, fileAlpha, 3, "BetaClass b;",
                DateTimeOffset.UtcNow)
        ]);

        var removed = await store.RemoveFilesAsync(WorkspaceId, ProjectId, [fileAlpha]);

        Assert.AreEqual(1, removed, "one file record was really deleted");
        Assert.IsEmpty(await store.GetSymbolsByFileAsync(WorkspaceId, ProjectId, fileAlpha),
            "the symbols of the removed file must be gone");
        Assert.IsNull(await store.GetSymbolAsync(WorkspaceId, ProjectId, alpha.SymbolId));
        Assert.IsEmpty(await store.ListRelationsAsync(WorkspaceId, ProjectId, alpha.SymbolId),
            "relations of the removed file's symbols must be gone");
        Assert.IsEmpty(await store.ListReferencesAsync(WorkspaceId, ProjectId, beta.SymbolId),
            "references pointing at symbols of the removed file must be gone");

        // Nothing else may be touched.
        Assert.HasCount(1, await store.GetSymbolsByFileAsync(WorkspaceId, ProjectId, fileBeta));
        var files = await store.ListFilesAsync(WorkspaceId, ProjectId);
        Assert.HasCount(1, files);
        Assert.AreEqual(fileBeta, files[0].FilePath);
    }

    [TestMethod]
    public async Task RemoveFilesAsync_RemovesIncomingEdgesThatComeFromOtherFiles()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;

        var removedFile = fixture.File("Removed.cs");
        var keptFile = fixture.File("Kept.cs");
        var thirdFile = fixture.File("Third.cs");
        var removed = Symbol(removedFile, "sym-removed", "RemovedClass");
        var kept = Symbol(keptFile, "sym-kept", "KeptClass");
        var third = Symbol(thirdFile, "sym-third", "ThirdClass");

        await store.UpsertFilesAsync(WorkspaceId, ProjectId, [
            File(removedFile), File(keptFile), File(thirdFile),
        ]);
        await store.UpsertSymbolsAsync(WorkspaceId, ProjectId, [removed, kept, third]);
        await store.UpsertRelationsAsync(WorkspaceId, ProjectId, [
            // 入边：来自别的文件的符号指向被删文件的符号。
            new CodeRelationRecord(WorkspaceId, ProjectId, kept.SymbolId, removed.SymbolId,
                CodeRelationKind.Calls, 7, keptFile, DateTimeOffset.UtcNow),
            // 出边：被删文件的符号指向别的符号。
            new CodeRelationRecord(WorkspaceId, ProjectId, removed.SymbolId, third.SymbolId,
                CodeRelationKind.Implements, 8, removedFile, DateTimeOffset.UtcNow),
            // 无关边：两个保留文件之间，必须原样留下。
            new CodeRelationRecord(WorkspaceId, ProjectId, kept.SymbolId, third.SymbolId,
                CodeRelationKind.References, 9, keptFile, DateTimeOffset.UtcNow),
        ]);
        await store.UpsertReferencesAsync(WorkspaceId, ProjectId, [
            new CodeReferenceRecord(WorkspaceId, ProjectId, kept.SymbolId, removed.SymbolId,
                keptFile, 7, "RemovedClass x;", DateTimeOffset.UtcNow),
            new CodeReferenceRecord(WorkspaceId, ProjectId, kept.SymbolId, third.SymbolId,
                keptFile, 9, "ThirdClass y;", DateTimeOffset.UtcNow),
        ]);

        await store.RemoveFilesAsync(WorkspaceId, ProjectId, [removedFile]);

        // 入边与出边都按原合同清除 —— 只按 SourceFilePath 删会漏掉入边。
        Assert.IsEmpty(await store.ListIncomingRelationsAsync(WorkspaceId, ProjectId, removed.SymbolId),
            "指向被删符号的入边必须清除");
        Assert.IsEmpty(await store.ListRelationsAsync(WorkspaceId, ProjectId, removed.SymbolId),
            "被删符号的出边必须清除");
        Assert.IsEmpty(await store.ListReferencesAsync(WorkspaceId, ProjectId, removed.SymbolId),
            "指向被删符号的引用必须清除");

        // 无关图不得被牵连。
        Assert.HasCount(1, await store.ListRelationsAsync(WorkspaceId, ProjectId, kept.SymbolId));
        Assert.HasCount(1, await store.ListIncomingRelationsAsync(WorkspaceId, ProjectId, third.SymbolId));
        Assert.HasCount(1, await store.ListReferencesAsync(WorkspaceId, ProjectId, third.SymbolId));
        Assert.HasCount(1, await store.GetSymbolsByFileAsync(WorkspaceId, ProjectId, keptFile));
    }

    [TestMethod]
    public async Task RemoveFilesAsync_RemovesASymbolsSelfReference()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;

        var filePath = fixture.File("Self.cs");
        var self = Symbol(filePath, "sym-self", "SelfClass");

        await store.UpsertFilesAsync(WorkspaceId, ProjectId, [File(filePath)]);
        await store.UpsertSymbolsAsync(WorkspaceId, ProjectId, [self]);
        await store.UpsertRelationsAsync(WorkspaceId, ProjectId, [
            new CodeRelationRecord(WorkspaceId, ProjectId, self.SymbolId, self.SymbolId,
                CodeRelationKind.References, 3, filePath, DateTimeOffset.UtcNow),
        ]);
        await store.UpsertReferencesAsync(WorkspaceId, ProjectId, [
            new CodeReferenceRecord(WorkspaceId, ProjectId, self.SymbolId, self.SymbolId,
                filePath, 3, "SelfClass s;", DateTimeOffset.UtcNow),
        ]);

        await store.RemoveFilesAsync(WorkspaceId, ProjectId, [filePath]);

        Assert.IsEmpty(await store.ListRelationsAsync(WorkspaceId, ProjectId, self.SymbolId));
        Assert.IsEmpty(await store.ListIncomingRelationsAsync(WorkspaceId, ProjectId, self.SymbolId));
        Assert.IsEmpty(await store.ListReferencesAsync(WorkspaceId, ProjectId, self.SymbolId));
    }

    [TestMethod]
    public async Task RemoveFilesAsync_OnlyTouchesTheRequestedWorkspaceAndProject()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;

        const string otherProjectId = "proj-remove-other";
        var filePath = fixture.File("Shared.cs");
        const string symbolId = "sym-shared";

        await store.UpsertFilesAsync(WorkspaceId, ProjectId, [File(filePath)]);
        await store.UpsertSymbolsAsync(WorkspaceId, ProjectId, [Symbol(filePath, symbolId, "SharedClass")]);
        await store.UpsertRelationsAsync(WorkspaceId, ProjectId, [
            new CodeRelationRecord(WorkspaceId, ProjectId, symbolId, symbolId,
                CodeRelationKind.Calls, 1, filePath, DateTimeOffset.UtcNow),
        ]);

        // 另一个 project 里有同样的 file path 与 symbol id。
        await store.UpsertFilesAsync(WorkspaceId, otherProjectId, [
            new CodeFileRecord(WorkspaceId, otherProjectId, filePath, "C#", DateTimeOffset.UtcNow),
        ]);
        await store.UpsertSymbolsAsync(WorkspaceId, otherProjectId, [
            new CodeSymbolRecord(WorkspaceId, otherProjectId, filePath, symbolId, "SharedClass",
                CodeSymbolKind.Class, 1, 5, "class SharedClass", Container: null),
        ]);
        await store.UpsertRelationsAsync(WorkspaceId, otherProjectId, [
            new CodeRelationRecord(WorkspaceId, otherProjectId, symbolId, symbolId,
                CodeRelationKind.Calls, 1, filePath, DateTimeOffset.UtcNow),
        ]);

        await store.RemoveFilesAsync(WorkspaceId, ProjectId, [filePath]);

        Assert.IsNull(await store.GetSymbolAsync(WorkspaceId, ProjectId, symbolId));
        Assert.IsNotNull(await store.GetSymbolAsync(WorkspaceId, otherProjectId, symbolId),
            "另一个 project 的同名符号不得被删除");
        Assert.HasCount(1, await store.ListRelationsAsync(WorkspaceId, otherProjectId, symbolId),
            "另一个 project 的图不得被删除");
    }

    [TestMethod]
    public async Task RemoveFilesAsync_DeletesTheFileRecordAndGraphRowsOfAFileWithoutSymbols()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;

        var filePath = fixture.File("Empty.cs");
        var otherPath = fixture.File("Other.cs");
        var other = Symbol(otherPath, "sym-other", "OtherClass");

        await store.UpsertFilesAsync(WorkspaceId, ProjectId, [File(filePath), File(otherPath)]);
        await store.UpsertSymbolsAsync(WorkspaceId, ProjectId, [other]);

        // 文件已经没有符号行了，但图里还留着以它 SourceFilePath 归属的行（符号行先被清掉的情况）。
        await store.UpsertRelationsAsync(WorkspaceId, ProjectId, [
            new CodeRelationRecord(WorkspaceId, ProjectId, "sym-vanished", other.SymbolId,
                CodeRelationKind.Calls, 4, filePath, DateTimeOffset.UtcNow),
        ]);
        await store.UpsertReferencesAsync(WorkspaceId, ProjectId, [
            new CodeReferenceRecord(WorkspaceId, ProjectId, "sym-vanished", other.SymbolId,
                filePath, 4, "OtherClass o;", DateTimeOffset.UtcNow),
        ]);

        var removed = await store.RemoveFilesAsync(WorkspaceId, ProjectId, [filePath]);

        Assert.AreEqual(1, removed, "没有符号的文件记录仍必须被删除");
        Assert.IsEmpty(await store.GetSymbolsByFileAsync(WorkspaceId, ProjectId, filePath));
        Assert.IsEmpty(await store.ListRelationsAsync(WorkspaceId, ProjectId, other.SymbolId),
            "归属该文件的残留图行必须清除");
        Assert.IsEmpty(await store.ListReferencesAsync(WorkspaceId, ProjectId, other.SymbolId),
            "归属该文件的残留引用行必须清除");
        Assert.HasCount(1, await store.GetSymbolsByFileAsync(WorkspaceId, ProjectId, otherPath),
            "无关文件不得受影响");
    }

    [TestMethod]
    public async Task RemoveFilesAsync_Is_Idempotent_For_Paths_That_Are_Not_Indexed()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;

        var unknown = fixture.File("never-indexed.cs");

        Assert.AreEqual(0, await store.RemoveFilesAsync(WorkspaceId, ProjectId, [unknown]),
            "removing an unknown path is a safe no-op");
        Assert.AreEqual(0, await store.RemoveFilesAsync(WorkspaceId, ProjectId, [unknown]),
            "removing it again is still a safe no-op");
        Assert.AreEqual(0, await store.RemoveFilesAsync(WorkspaceId, ProjectId, []),
            "an empty batch removes nothing");
        Assert.AreEqual(0, await store.RemoveFilesAsync(WorkspaceId, ProjectId, ["   "]),
            "blank entries are ignored, not treated as a path");

        var indexed = fixture.File("Once.cs");
        await store.UpsertFilesAsync(WorkspaceId, ProjectId, [File(indexed)]);

        Assert.AreEqual(1, await store.RemoveFilesAsync(WorkspaceId, ProjectId, [indexed]));
        Assert.AreEqual(0, await store.RemoveFilesAsync(WorkspaceId, ProjectId, [indexed]),
            "the second removal of the same path is a no-op");
    }

    [TestMethod]
    public async Task RemoveFilesAsync_Leaves_No_Half_Removed_State_When_A_Delete_Fails_Midway()
    {
        using var fixture = Fixture.Create();
        var store = fixture.Store;

        var fileAlpha = fixture.File("Alpha.cs");
        var fileBeta = fixture.File("Beta.cs");

        await store.UpsertFilesAsync(WorkspaceId, ProjectId, [File(fileAlpha), File(fileBeta)]);
        await store.UpsertSymbolsAsync(WorkspaceId, ProjectId, [
            Symbol(fileAlpha, "sym-alpha", "AlphaClass"),
            Symbol(fileBeta, "sym-beta", "BetaClass"),
        ]);

        // Deterministic fault injection: deleting the second file record aborts. Alpha is processed first, so
        // a per-file (rather than per-batch) transaction would leave Alpha half removed.
        await fixture.FailDeleteOfFileRecordAsync(fileBeta);

        await Assert.ThrowsAsync<SqliteException>(
            () => store.RemoveFilesAsync(WorkspaceId, ProjectId, [fileAlpha, fileBeta]));

        var files = await store.ListFilesAsync(WorkspaceId, ProjectId);
        Assert.HasCount(2, files, "the failed batch must not remove anything");
        Assert.HasCount(1, await store.GetSymbolsByFileAsync(WorkspaceId, ProjectId, fileAlpha));
        Assert.HasCount(1, await store.GetSymbolsByFileAsync(WorkspaceId, ProjectId, fileBeta));
    }

    private static CodeFileRecord File(string filePath) =>
        new(WorkspaceId, ProjectId, filePath, "C#", DateTimeOffset.UtcNow);

    private static CodeSymbolRecord Symbol(string filePath, string symbolId, string name) =>
        new(WorkspaceId, ProjectId, filePath, symbolId, name, CodeSymbolKind.Class, 1, 5, $"class {name}", Container: null);

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, string databasePath)
        {
            Root = root;
            DatabasePath = databasePath;
            Store = new SqliteCodeIndexStore(databasePath);
        }

        public string Root { get; }

        public string DatabasePath { get; }

        public SqliteCodeIndexStore Store { get; }

        public string File(string fileName) => System.IO.Path.Combine(Root, fileName);

        public static Fixture Create()
        {
            var root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "pudding-u3b3-store-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new Fixture(root, System.IO.Path.Combine(root, "db", "code-index.db"));
        }

        /// <summary>Adds a trigger that aborts the deletion of one specific file record.</summary>
        public async Task FailDeleteOfFileRecordAsync(string filePath)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Pooling = false,
            }.ToString());

            await connection.OpenAsync();

            var escaped = filePath.Replace("'", "''", StringComparison.Ordinal);

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TRIGGER IF NOT EXISTS u3b3_fail_delete_of_file_record
                BEFORE DELETE ON CodeFiles
                WHEN OLD.FilePath = '{escaped}'
                BEGIN
                    SELECT RAISE(ABORT, 'U3-B3 injected delete failure');
                END;
                """;

            await command.ExecuteNonQueryAsync();
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // the temp directory is best-effort cleanup
            }
        }
    }
}
