using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Models;
using PuddingCode.Swarm;

namespace PuddingCodeTests.Swarm;

[TestClass]
public class WorkerManagerTests
{
    [TestMethod]
    public async Task SpawnWorkerAsync_CreatesUniqueWorkerId()
    {
        // Arrange
        var testDir = await CreateTestGitRepoAsync();
        try
        {
            var manager = new WorkerManager(testDir);
            var scope = new WorkerScope(["src/*"], []);

            // Act
            var worker1 = await manager.SpawnWorkerAsync(WorkerRole.Builder, "Task 1", scope);
            var worker2 = await manager.SpawnWorkerAsync(WorkerRole.QA, "Task 2", scope);

            // Assert
            Assert.IsNotNull(worker1.Id);
            Assert.IsNotNull(worker2.Id);
            Assert.AreNotEqual(worker1.Id, worker2.Id);
            Assert.AreEqual(WorkerRole.Builder, worker1.Role);
            Assert.AreEqual(WorkerRole.QA, worker2.Role);
        }
        finally
        {
            await CleanupTestRepoAsync(testDir);
        }
    }

    [TestMethod]
    public async Task SpawnWorkerAsync_CreatesGitWorktree()
    {
        // Arrange
        var testDir = await CreateTestGitRepoAsync();
        try
        {
            var manager = new WorkerManager(testDir);
            var scope = new WorkerScope(["src/*"], []);

            // Act
            var worker = await manager.SpawnWorkerAsync(WorkerRole.Builder, "Implement feature", scope);

            // Assert
            Assert.IsNotNull(worker.WorktreePath);
            Assert.IsTrue(Directory.Exists(worker.WorktreePath));
            Assert.IsTrue(worker.WorktreePath.Contains(worker.Id));
        }
        finally
        {
            await CleanupTestRepoAsync(testDir);
        }
    }

    [TestMethod]
    public async Task SpawnWorkerAsync_TracksWorkerInActiveList()
    {
        // Arrange
        var testDir = await CreateTestGitRepoAsync();
        try
        {
            var manager = new WorkerManager(testDir);
            var scope = new WorkerScope(["src/*"], []);

            // Act
            await manager.SpawnWorkerAsync(WorkerRole.Builder, "Task 1", scope);
            await manager.SpawnWorkerAsync(WorkerRole.QA, "Task 2", scope);
            var workers = manager.GetActiveWorkers();

            // Assert
            Assert.AreEqual(2, workers.Count);
            Assert.IsTrue(workers.Any(w => w.Role == WorkerRole.Builder));
            Assert.IsTrue(workers.Any(w => w.Role == WorkerRole.QA));
        }
        finally
        {
            await CleanupTestRepoAsync(testDir);
        }
    }

    [TestMethod]
    public async Task DismissWorkerAsync_RemovesWorkerFromTracking()
    {
        // Arrange
        var testDir = await CreateTestGitRepoAsync();
        try
        {
            var manager = new WorkerManager(testDir);
            var scope = new WorkerScope(["src/*"], []);
            var worker = await manager.SpawnWorkerAsync(WorkerRole.Builder, "Task", scope);

            // Act
            await manager.DismissWorkerAsync(worker.Id);
            var workers = manager.GetActiveWorkers();

            // Assert
            Assert.AreEqual(0, workers.Count);
        }
        finally
        {
            await CleanupTestRepoAsync(testDir);
        }
    }

    [TestMethod]
    public async Task DismissWorkerAsync_RemovesGitWorktree()
    {
        // Arrange
        var testDir = await CreateTestGitRepoAsync();
        try
        {
            var manager = new WorkerManager(testDir);
            var scope = new WorkerScope(["src/*"], []);
            var worker = await manager.SpawnWorkerAsync(WorkerRole.Builder, "Task", scope);
            var worktreePath = worker.WorktreePath;

            // Act
            await manager.DismissWorkerAsync(worker.Id);

            // Assert
            Assert.IsFalse(Directory.Exists(worktreePath));
        }
        finally
        {
            await CleanupTestRepoAsync(testDir);
        }
    }

    [TestMethod]
    public async Task DismissWorkerAsync_ThrowsForUnknownWorkerId()
    {
        // Arrange
        var testDir = await CreateTestGitRepoAsync();
        try
        {
            var manager = new WorkerManager(testDir);

            // Act & Assert
            try
            {
                await manager.DismissWorkerAsync("non-existent-worker");
                Assert.Fail("Expected InvalidOperationException");
            }
            catch (InvalidOperationException)
            {
                // Expected
            }
        }
        finally
        {
            await CleanupTestRepoAsync(testDir);
        }
    }

    [TestMethod]
    public async Task SpawnWorkerAsync_CreatesCorrectWorkerName()
    {
        // Arrange
        var testDir = await CreateTestGitRepoAsync();
        try
        {
            var manager = new WorkerManager(testDir);
            var scope = new WorkerScope(["src/*"], []);

            // Act
            var worker = await manager.SpawnWorkerAsync(WorkerRole.Builder, "Task", scope);

            // Assert
            Assert.IsNotNull(worker.Name);
            Assert.IsTrue(worker.Name.StartsWith("Builder-"));
            Assert.AreEqual(11, worker.Name.Length); // "Builder-" + 4 chars
        }
        finally
        {
            await CleanupTestRepoAsync(testDir);
        }
    }

    [TestMethod]
    public async Task GetActiveWorkers_ReturnsReadOnlyList()
    {
        // Arrange
        var testDir = await CreateTestGitRepoAsync();
        try
        {
            var manager = new WorkerManager(testDir);
            var scope = new WorkerScope(["src/*"], []);
            await manager.SpawnWorkerAsync(WorkerRole.Builder, "Task", scope);

            // Act
            var workers = manager.GetActiveWorkers();

            // Assert
            Assert.IsInstanceOfType<IReadOnlyList<WorkerInfo>>(workers);
            Assert.AreEqual(1, workers.Count);
        }
        finally
        {
            await CleanupTestRepoAsync(testDir);
        }
    }

    /// <summary>
    /// 创建测试用的 Git 仓库。
    /// </summary>
    private static async Task<string> CreateTestGitRepoAsync()
    {
        var testDir = Path.Combine(Path.GetTempPath(), $"worker-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);

        // Initialize git repo
        await RunGitAsync(testDir, ["init"]);
        await RunGitAsync(testDir, ["config", "user.email", "test@example.com"]);
        await RunGitAsync(testDir, ["config", "user.name", "Test User"]);

        // Create initial commit
        var readmePath = Path.Combine(testDir, "README.md");
        await File.WriteAllTextAsync(readmePath, "# Test Repo");
        await RunGitAsync(testDir, ["add", "."]);
        await RunGitAsync(testDir, ["commit", "-m", "Initial commit"]);

        return testDir;
    }

    /// <summary>
    /// 清理测试仓库（包括 worktrees）。
    /// </summary>
    private static async Task CleanupTestRepoAsync(string testDir)
    {
        if (!Directory.Exists(testDir))
            return;

        try
        {
            // 尝试清理 worktrees（如果存在）
            var worktreesDir = Path.Combine(testDir, ".pudding", "worktrees");
            if (Directory.Exists(worktreesDir))
            {
                try
                {
                    Directory.Delete(worktreesDir, true);
                }
                catch
                {
                    // 忽略清理失败
                }
            }

            // 删除分支
            var branches = await RunGitAsync(testDir, ["branch", "--list", "swarm/*"]);
            foreach (var branch in branches.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var branchName = branch.Trim('*', ' ');
                if (!string.IsNullOrWhiteSpace(branchName))
                {
                    try
                    {
                        await RunGitAsync(testDir, ["branch", "-D", branchName]);
                    }
                    catch
                    {
                        // 忽略删除失败
                    }
                }
            }

            Directory.Delete(testDir, true);
        }
        catch
        {
            // 忽略清理失败
        }
    }

    /// <summary>
    /// 运行 Git 命令。
    /// </summary>
    private static async Task<string> RunGitAsync(string workDir, string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = string.Join(' ', args),
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git process");
        
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        }

        return output;
    }
}
