using PuddingDesktop.Storage;

namespace PuddingDesktop.Tests.Storage;

public sealed class LogRetentionServiceExecutionTests
{
    [Fact]
    public async Task Execute_DeletesPreviewedOldLogsAndPreservesOtherFiles()
    {
        var root = LogRetentionServicePreviewTests.CreateTempDirectory();
        try
        {
            var oldLog = LogRetentionServicePreviewTests.WriteFile(
                root, "logs/system/old.log", "old", TimeSpan.FromDays(2));
            var recentLog = LogRetentionServicePreviewTests.WriteFile(
                root, "logs/system/recent.log", "recent", TimeSpan.FromHours(2));
            var nonLog = LogRetentionServicePreviewTests.WriteFile(
                root, "logs/system/old.bin", "keep", TimeSpan.FromDays(3));
            var service = LogRetentionServicePreviewTests.CreateService();
            var preview = await service.PreviewAsync(root, TimeSpan.FromDays(1), CancellationToken.None);

            var result = await service.ExecuteAsync(preview, progress: null, CancellationToken.None);

            Assert.Equal(1, result.DeletedFiles);
            Assert.Equal(3, result.DeletedBytes);
            Assert.False(File.Exists(oldLog));
            Assert.True(File.Exists(recentLog));
            Assert.True(File.Exists(nonLog));
        }
        finally
        {
            LogRetentionServicePreviewTests.TryDelete(root);
        }
    }

    [Fact]
    public async Task Execute_SkipsFileChangedAfterPreview()
    {
        var root = LogRetentionServicePreviewTests.CreateTempDirectory();
        try
        {
            var oldLog = LogRetentionServicePreviewTests.WriteFile(
                root, "logs/old.log", "old", TimeSpan.FromDays(2));
            var service = LogRetentionServicePreviewTests.CreateService();
            var preview = await service.PreviewAsync(root, TimeSpan.FromDays(1), CancellationToken.None);
            File.AppendAllText(oldLog, "changed");

            var result = await service.ExecuteAsync(preview, progress: null, CancellationToken.None);

            Assert.Equal(0, result.DeletedFiles);
            Assert.Equal(1, result.SkippedFiles);
            Assert.True(File.Exists(oldLog));
        }
        finally
        {
            LogRetentionServicePreviewTests.TryDelete(root);
        }
    }

    [Fact]
    public async Task Execute_SkipsExclusivelyOpenedLog()
    {
        var root = LogRetentionServicePreviewTests.CreateTempDirectory();
        FileStream? lockStream = null;
        try
        {
            var oldLog = LogRetentionServicePreviewTests.WriteFile(
                root, "logs/old.log", "old", TimeSpan.FromDays(2));
            var service = LogRetentionServicePreviewTests.CreateService();
            var preview = await service.PreviewAsync(root, TimeSpan.FromDays(1), CancellationToken.None);
            lockStream = new FileStream(oldLog, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var result = await service.ExecuteAsync(preview, progress: null, CancellationToken.None);

            Assert.Equal(0, result.DeletedFiles);
            Assert.Equal(1, result.SkippedFiles);
            Assert.True(File.Exists(oldLog));
        }
        finally
        {
            lockStream?.Dispose();
            LogRetentionServicePreviewTests.TryDelete(root);
        }
    }

    [Fact]
    public async Task Execute_DoesNotDeleteTamperedCandidateOutsideLogs()
    {
        var root = LogRetentionServicePreviewTests.CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            var outside = LogRetentionServicePreviewTests.WriteFile(
                root, "outside.log", "outside", TimeSpan.FromDays(2));
            var service = LogRetentionServicePreviewTests.CreateService();
            var preview = await service.PreviewAsync(root, TimeSpan.FromDays(1), CancellationToken.None);
            var info = new FileInfo(outside);
            var tampered = preview with
            {
                Candidates =
                [
                    new LogCleanupCandidate
                    {
                        FullPath = outside,
                        RelativePath = "outside.log",
                        Length = info.Length,
                        LastWriteTimeUtc = new DateTimeOffset(info.LastWriteTimeUtc),
                        CreationTimeUtc = new DateTimeOffset(info.CreationTimeUtc),
                    },
                ],
                CandidateBytes = info.Length,
            };

            var result = await service.ExecuteAsync(tampered, progress: null, CancellationToken.None);

            Assert.Equal(0, result.DeletedFiles);
            Assert.Equal(1, result.FailedFiles);
            Assert.True(File.Exists(outside));
        }
        finally
        {
            LogRetentionServicePreviewTests.TryDelete(root);
        }
    }
}
