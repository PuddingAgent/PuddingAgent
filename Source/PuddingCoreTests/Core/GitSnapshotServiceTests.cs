namespace PuddingCodeTests.Core;

[TestClass]
public sealed class GitSnapshotServiceTests
{
    private string _tempPath = null!;
    private GitSnapshotService _service = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"pudding_git_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempPath);
        _service = new GitSnapshotService(_tempPath);

        // Initialize git repo for tests
        await _service.EnsureRepoAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempPath))
        {
            try { Directory.Delete(_tempPath, true); } catch { }
        }
    }

    // ──── IsGitRepo Tests ────

    [TestMethod]
    public void IsGitRepo_ReturnsTrueAfterEnsureRepo()
    {
        // Assert
        Assert.IsTrue(_service.IsGitRepo);
    }

    [TestMethod]
    public void IsGitRepo_ReturnsFalseForNonGitDirectory()
    {
        // Arrange
        var nonGitPath = Path.Combine(Path.GetTempPath(), $"non_git_{Guid.NewGuid()}");
        Directory.CreateDirectory(nonGitPath);
        try
        {
            var service = new GitSnapshotService(nonGitPath);
            
            // Assert
            Assert.IsFalse(service.IsGitRepo);
        }
        finally
        {
            try { Directory.Delete(nonGitPath, true); } catch { }
        }
    }

    // ──── CreateSnapshot Tests ────

    [TestMethod]
    public async Task CreateSnapshot_WithChanges_CreatesCommit()
    {
        // Arrange
        var filePath = Path.Combine(_tempPath, "test.txt");
        File.WriteAllText(filePath, "test content");

        // Act
        var hash = await _service.CreateSnapshotAsync("test snapshot");

        // Assert
        Assert.IsNotNull(hash);
        Assert.IsGreaterThan(hash.Length, 0);
    }

    [TestMethod]
    public async Task CreateSnapshot_NoChanges_ReturnsNull()
    {
        // Act
        var hash = await _service.CreateSnapshotAsync("no changes");

        // Assert
        Assert.IsNull(hash);
    }

    [TestMethod]
    public async Task CreateSnapshot_IncludesLabelInMessage()
    {
        // Arrange
        var filePath = Path.Combine(_tempPath, "test.txt");
        File.WriteAllText(filePath, "test content");

        // Act
        await _service.CreateSnapshotAsync("my custom label");

        // Assert
        var snapshots = await _service.ListSnapshotsAsync(10);
        Assert.HasCount(2, snapshots);
        Assert.AreEqual("my custom label", snapshots[0].Label);
    }

    [TestMethod]
    public async Task CreateSnapshot_MultipleSnapshots_CreatesAll()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempPath, "file1.txt"), "content1");
        await _service.CreateSnapshotAsync("snapshot 1");
        
        File.WriteAllText(Path.Combine(_tempPath, "file2.txt"), "content2");
        await _service.CreateSnapshotAsync("snapshot 2");
        
        File.WriteAllText(Path.Combine(_tempPath, "file3.txt"), "content3");
        await _service.CreateSnapshotAsync("snapshot 3");

        // Act
        var snapshots = await _service.ListSnapshotsAsync(10);

        // Assert
        Assert.HasCount(4, snapshots);
        Assert.AreEqual("snapshot 3", snapshots[0].Label);
        Assert.AreEqual("snapshot 2", snapshots[1].Label);
        Assert.AreEqual("snapshot 1", snapshots[2].Label);
        Assert.AreEqual("init", snapshots[3].Label);
    }

    // ──── Undo Tests ────

    [TestMethod]
    public async Task Undo_SingleSnapshot_ResetsOne()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempPath, "test.txt"), "content");
        await _service.CreateSnapshotAsync("test");

        // Act
        var undone = await _service.UndoAsync(1);

        // Assert
        Assert.AreEqual(1, undone);
    }

    [TestMethod]
    public async Task Undo_MultipleSnapshots_ResetsAll()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempPath, "file1.txt"), "content1");
        await _service.CreateSnapshotAsync("snap1");
        
        File.WriteAllText(Path.Combine(_tempPath, "file2.txt"), "content2");
        await _service.CreateSnapshotAsync("snap2");

        // Act
        var undone = await _service.UndoAsync(2);

        // Assert
        Assert.AreEqual(2, undone);
    }

    [TestMethod]
    public async Task Undo_NoSnapshots_ReturnsZero()
    {
        // Arrange: Create a new test directory with 2 commits
        var tempPath = Path.Combine(Path.GetTempPath(), $"pudding_git_test2_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempPath);
        var service = new GitSnapshotService(tempPath);
        
        try
        {
            // Initialize repo
            await service.EnsureRepoAsync();
            
            // Create another commit
            File.WriteAllText(Path.Combine(tempPath, "test.txt"), "content");
            await service.CreateSnapshotAsync("test snapshot");
            
            // Act: Undo 1 snapshot
            var undone = await service.UndoAsync(1);
            
            // Assert: Can undo 1
            Assert.AreEqual(1, undone);
        }
        finally
        {
            try { Directory.Delete(tempPath, true); } catch { }
        }
    }

    [TestMethod]
    public async Task Undo_ZeroCount_ReturnsZero()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempPath, "test.txt"), "content");
        await _service.CreateSnapshotAsync("test");

        // Act
        var undone = await _service.UndoAsync(0);

        // Assert
        Assert.AreEqual(0, undone);
    }

    // ──── ListSnapshots Tests ────

    [TestMethod]
    public async Task ListSnapshots_ReturnsMostRecentFirst()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempPath, "file1.txt"), "content1");
        await _service.CreateSnapshotAsync("first");
        
        await Task.Delay(10); // Ensure different timestamps
        
        File.WriteAllText(Path.Combine(_tempPath, "file2.txt"), "content2");
        await _service.CreateSnapshotAsync("second");

        // Act
        var snapshots = await _service.ListSnapshotsAsync(10);

        // Assert
        Assert.HasCount(3, snapshots);
        Assert.AreEqual("second", snapshots[0].Label);
        Assert.AreEqual("first", snapshots[1].Label);
        Assert.IsTrue(snapshots[0].Timestamp > snapshots[1].Timestamp);
    }

    [TestMethod]
    public async Task ListSnapshots_RespectsMaxCount()
    {
        // Arrange
        for (int i = 0; i < 15; i++)
        {
            File.WriteAllText(Path.Combine(_tempPath, $"file{i}.txt"), $"content{i}");
            await _service.CreateSnapshotAsync($"snapshot{i}");
        }

        // Act
        var snapshots = await _service.ListSnapshotsAsync(10);

        // Assert
        Assert.HasCount(10, snapshots);
    }

    [TestMethod]
    public async Task ListSnapshots_NoSnapshots_ReturnsEmptyList()
    {
        // Act
        var snapshots = await _service.ListSnapshotsAsync(10);

        // Assert: Only initial commit exists (label "init")
        Assert.HasCount(1, snapshots);
        Assert.AreEqual("init", snapshots[0].Label);
    }

    [TestMethod]
    public async Task ListSnapshots_SnapshotEntryHasCorrectFields()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempPath, "test.txt"), "content");
        await _service.CreateSnapshotAsync("test label");

        // Act
        var snapshots = await _service.ListSnapshotsAsync(1);

        // Assert
        var entry = snapshots[0];
        Assert.IsNotNull(entry.Hash);
        Assert.IsNotNull(entry.ShortHash);
        Assert.AreEqual("test label", entry.Label);
        Assert.IsTrue(entry.Timestamp <= DateTimeOffset.Now);
    }

    // ──── Restore Tests ────

    [TestMethod]
    public async Task Restore_ValidHash_ResetsToCommit()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempPath, "test.txt"), "original");
        await _service.CreateSnapshotAsync("original");
        
        var filePath = Path.Combine(_tempPath, "test.txt");
        File.WriteAllText(filePath, "modified");
        
        var snapshots = await _service.ListSnapshotsAsync(1);
        var hash = snapshots[0].Hash;

        // Act
        var success = await _service.RestoreAsync(hash);

        // Assert
        Assert.IsTrue(success);
        var restoredContent = File.ReadAllText(filePath);
        Assert.AreEqual("original", restoredContent);
    }

    [TestMethod]
    public async Task Restore_InvalidHash_ReturnsFalse()
    {
        // Act
        var success = await _service.RestoreAsync("invalid_hash_xyz");

        // Assert
        Assert.IsFalse(success);
    }

    [TestMethod]
    public async Task Restore_NoGitRepo_ReturnsFalse()
    {
        // Arrange
        var nonGitPath = Path.Combine(Path.GetTempPath(), $"non_git_restore_{Guid.NewGuid()}");
        Directory.CreateDirectory(nonGitPath);
        var service = new GitSnapshotService(nonGitPath);
        
        try
        {
            // Act
            var success = await service.RestoreAsync("abc123");
            
            // Assert
            Assert.IsFalse(success);
        }
        finally
        {
            try { Directory.Delete(nonGitPath, true); } catch { }
        }
    }

    // ──── EnsureRepo Tests ────

    [TestMethod]
    public async Task EnsureRepo_CreatesGitRepository()
    {
        // Arrange
        var newPath = Path.Combine(Path.GetTempPath(), $"new_repo_{Guid.NewGuid()}");
        Directory.CreateDirectory(newPath);
        var service = new GitSnapshotService(newPath);
        
        try
        {
            // Act
            await service.EnsureRepoAsync();
            
            // Assert
            Assert.IsTrue(service.IsGitRepo);
        }
        finally
        {
            try { Directory.Delete(newPath, true); } catch { }
        }
    }

    [TestMethod]
    public async Task EnsureRepo_AlreadyRepo_DoesNothing()
    {
        // Act
        await _service.EnsureRepoAsync();
        
        // Assert
        Assert.IsTrue(_service.IsGitRepo);
    }

    // ──── Edge Cases ────

    [TestMethod]
    public void Constructor_ThrowsOnNullPath()
    {
        // Act & Assert
        Assert.ThrowsExactly<ArgumentNullException>(() => new GitSnapshotService(null!));
    }

    [TestMethod]
    public async Task CreateSnapshot_LabelWithSpecialCharacters()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempPath, "test.txt"), "content");

        // Act
        var hash = await _service.CreateSnapshotAsync("test with \"quotes\" and 'apostrophes'");

        // Assert
        Assert.IsNotNull(hash);
    }
}
