using PuddingDesktop.Storage;

namespace PuddingDesktop.Tests.Storage;

public sealed class LogRetentionServicePreviewTests
{
    [Fact]
    public async Task Preview_SelectsOnlyAllowedLogsOlderThanOneDay()
    {
        var root = CreateTempDirectory();
        try
        {
            var oldLog = WriteFile(root, "logs/system/old.log", "1234567890", TimeSpan.FromDays(2));
            var oldArchive = WriteFile(root, "logs/archive/old.zip", "12345", TimeSpan.FromDays(3));
            WriteFile(root, "logs/system/recent.log", "recent", TimeSpan.FromHours(2));
            WriteFile(root, "logs/system/old.png", "not-a-log", TimeSpan.FromDays(4));
            WriteFile(root, "outside.log", "outside", TimeSpan.FromDays(4));

            var preview = await CreateService().PreviewAsync(
                root,
                TimeSpan.FromDays(1),
                CancellationToken.None);

            Assert.Equal(2, preview.Candidates.Count);
            Assert.Equal(15, preview.CandidateBytes);
            Assert.Contains(preview.Candidates, item => PathEquals(item.FullPath, oldLog));
            Assert.Contains(preview.Candidates, item => PathEquals(item.FullPath, oldArchive));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Preview_MissingLogsDirectoryReturnsEmptyPlan()
    {
        var root = CreateTempDirectory();
        try
        {
            var preview = await CreateService().PreviewAsync(
                root,
                TimeSpan.FromDays(1),
                CancellationToken.None);

            Assert.Empty(preview.Candidates);
            Assert.Equal(0, preview.CandidateBytes);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Preview_RejectsRetentionShorterThanOneDay()
    {
        var root = CreateTempDirectory();
        try
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => CreateService().PreviewAsync(
                    root,
                    TimeSpan.FromHours(23),
                    CancellationToken.None));
        }
        finally
        {
            TryDelete(root);
        }
    }

    internal static LogRetentionService CreateService()
        => new(new DataRootSafetyValidator());

    internal static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PuddingAgent", "storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static string WriteFile(
        string root,
        string relativePath,
        string content,
        TimeSpan age)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    internal static bool PathEquals(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    internal static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
