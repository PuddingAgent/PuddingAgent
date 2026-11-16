using PuddingDesktop.Guardian;

namespace PuddingDesktop.GuardianTests;

/// <summary>
/// Guardian 隔离构建工作区回收的用例。
/// 背景：每构建一次就在 <c>temp/build/guardian</c> 下留一个约 1.6 GB 的目录，
/// 此前从不回收，实测累积到 62.5 GB。
/// </summary>
public sealed class GuardianWorkspaceRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pudding-guardian-retention",
        Guid.NewGuid().ToString("N"));

    public GuardianWorkspaceRetentionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string CreateWorkspace(string name, DateTimeOffset lastWrite)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "candidate.dll"), "x");
        Directory.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return name;
    }

    [Fact]
    public void Prune_DeletesOldestAndKeepsTheTwoNewest()
    {
        var now = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
        CreateWorkspace("w1", now.AddHours(-5));
        CreateWorkspace("w2", now.AddHours(-4));
        CreateWorkspace("w3", now.AddHours(-3));
        CreateWorkspace("w4", now.AddHours(-2));
        CreateWorkspace("w5", now.AddHours(-1));

        var deleted = GuardianWorkspaceRetention.Prune(
            _root, keepCount: 2, minimumAge: TimeSpan.Zero, now: now);

        Assert.Equal(new[] { "w1", "w2", "w3" }, deleted.OrderBy(item => item).ToArray());
        Assert.False(Directory.Exists(Path.Combine(_root, "w1")));
        Assert.False(Directory.Exists(Path.Combine(_root, "w2")));
        Assert.False(Directory.Exists(Path.Combine(_root, "w3")));
        Assert.True(Directory.Exists(Path.Combine(_root, "w4")));
        Assert.True(Directory.Exists(Path.Combine(_root, "w5")));
    }

    [Fact]
    public void Prune_LeavesDirectoriesInsideTheProtectionWindowAlone()
    {
        var now = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 5; index++)
            CreateWorkspace("fresh" + index, now.AddMinutes(-index));

        var deleted = GuardianWorkspaceRetention.Prune(
            _root, keepCount: 0, minimumAge: TimeSpan.FromMinutes(30), now: now);

        Assert.Empty(deleted);
        Assert.Equal(5, Directory.GetDirectories(_root).Length);
    }

    [Fact]
    public void Prune_DeletesNothingWhenRootIsMissing()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        var deleted = GuardianWorkspaceRetention.Prune(missing);

        Assert.Empty(deleted);
    }

    [Fact]
    public void Prune_WithZeroKeepCount_RemovesEveryOldWorkspace()
    {
        var now = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
        CreateWorkspace("a", now.AddHours(-3));
        CreateWorkspace("b", now.AddHours(-2));
        CreateWorkspace("c", now.AddHours(-1));

        var deleted = GuardianWorkspaceRetention.Prune(
            _root, keepCount: 0, minimumAge: TimeSpan.Zero, now: now);

        Assert.Equal(new[] { "a", "b", "c" }, deleted.OrderBy(item => item).ToArray());
        Assert.Empty(Directory.GetDirectories(_root));
    }
}
