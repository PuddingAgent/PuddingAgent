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
