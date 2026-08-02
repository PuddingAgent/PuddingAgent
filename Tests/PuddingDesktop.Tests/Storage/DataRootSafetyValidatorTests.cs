using PuddingDesktop.Storage;

namespace PuddingDesktop.Tests.Storage;

public sealed class DataRootSafetyValidatorTests
{
    [Fact]
    public void ValidateLogRoot_AcceptsDirectRealLogsDirectory()
    {
        var root = CreateTempDirectory();
        try
        {
            var logs = Directory.CreateDirectory(Path.Combine(root, "logs")).FullName;
            var result = new DataRootSafetyValidator().ValidateLogRoot(root, requireLogDirectory: true);

            Assert.Equal(Path.GetFullPath(root), result.DataRoot, ignoreCase: true);
            Assert.Equal(Path.GetFullPath(logs), result.LogRoot, ignoreCase: true);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ValidateDataRoot_RejectsDriveRoot()
    {
        var driveRoot = Path.GetPathRoot(Path.GetTempPath());
        Assert.False(string.IsNullOrWhiteSpace(driveRoot));

        var exception = Assert.Throws<StorageSafetyException>(
            () => new DataRootSafetyValidator().ValidateDataRoot(driveRoot!));

        Assert.Contains("磁盘根目录", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateLogRoot_AllowsMissingLogsForEmptyPreview()
    {
        var root = CreateTempDirectory();
        try
        {
            var result = new DataRootSafetyValidator().ValidateLogRoot(
                root,
                requireLogDirectory: false);

            Assert.False(Directory.Exists(result.LogRoot));
            Assert.Equal("logs", Path.GetFileName(result.LogRoot), ignoreCase: true);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void IsDescendantOf_RejectsParentAndSiblingPaths()
    {
        var root = CreateTempDirectory();
        var child = Directory.CreateDirectory(Path.Combine(root, "logs")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(root)!, $"sibling-{Guid.NewGuid():N}")).FullName;
        try
        {
            var validator = new DataRootSafetyValidator();
            Assert.True(validator.IsDescendantOf(child, root));
            Assert.False(validator.IsDescendantOf(root, root));
            Assert.False(validator.IsDescendantOf(sibling, root));
        }
        finally
        {
            TryDelete(root);
            TryDelete(sibling);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PuddingAgent", "storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
