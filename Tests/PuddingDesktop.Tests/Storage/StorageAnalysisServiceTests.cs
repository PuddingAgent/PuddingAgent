using PuddingDesktop.Storage;

namespace PuddingDesktop.Tests.Storage;

public sealed class StorageAnalysisServiceTests
{
    [Fact]
    public async Task Analyze_CountsEachFileInExactlyOneCategory()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteSizedFile(root, "logs/system.log", 10);
            WriteSizedFile(root, "databases/pudding.db", 20);
            WriteSizedFile(root, "sessions/s1/events.jsonl", 30);
            WriteSizedFile(root, "browser/downloads/report.bin", 40);
            WriteSizedFile(root, "browser/workbench/user-data/state.bin", 50);
            WriteSizedFile(root, "build-validation/output.dll", 60);
            WriteSizedFile(root, "misc/value.bin", 70);

            var snapshot = await CreateService().AnalyzeAsync(root, progress: null, CancellationToken.None);

            Assert.Equal(280, snapshot.LogicalBytes);
            Assert.Equal(7, snapshot.Categories.Sum(item => item.FileCount));
            AssertCategory(snapshot, StorageCategoryKind.Logs, 10, 1);
            AssertCategory(snapshot, StorageCategoryKind.DatabaseAndIndex, 20, 1);
            AssertCategory(snapshot, StorageCategoryKind.ConversationAndMemory, 30, 1);
            AssertCategory(snapshot, StorageCategoryKind.AssetsAndDownloads, 40, 1);
            AssertCategory(snapshot, StorageCategoryKind.Browser, 50, 1);
            AssertCategory(snapshot, StorageCategoryKind.UnexpectedBuildOutput, 60, 1);
            AssertCategory(snapshot, StorageCategoryKind.Other, 70, 1);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Analyze_ReportsFinalProgress()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteSizedFile(root, "logs/a.log", 16);
            var reports = new List<StorageScanProgress>();
            var progress = new InlineProgress<StorageScanProgress>(reports.Add);

            await CreateService().AnalyzeAsync(root, progress, CancellationToken.None);

            var final = Assert.Single(reports);
            Assert.Equal(1, final.ScannedFileCount);
            Assert.Equal(16, final.ScannedBytes);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Analyze_HonorsCancellation()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteSizedFile(root, "logs/a.log", 1);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => CreateService().AnalyzeAsync(root, progress: null, cts.Token));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static StorageAnalysisService CreateService()
    {
        var validator = new DataRootSafetyValidator();
        return new StorageAnalysisService(validator, new StorageCategoryCatalog());
    }

    private static void AssertCategory(
        StorageSnapshot snapshot,
        StorageCategoryKind kind,
        long bytes,
        long files)
    {
        var category = Assert.Single(snapshot.Categories, item => item.Definition.Kind == kind);
        Assert.Equal(bytes, category.LogicalBytes);
        Assert.Equal(files, category.FileCount);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PuddingAgent", "storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteSizedFile(string root, string relativePath, int length)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
