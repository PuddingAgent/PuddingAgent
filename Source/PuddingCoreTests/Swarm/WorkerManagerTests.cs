using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Models;
using PuddingCode.Swarm;

namespace PuddingCodeTests.Swarm;

[TestClass]
[DoNotParallelize]
public class WorkerManagerTests
{
    /// <summary>
    /// 清理所有测试前留下的 Git worktree 和分支。
    /// </summary>
    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        // 清理可能从之前测试运行中遗留的临时目录
        var tempPath = Path.GetTempPath();
        var workerTestDirs = Directory.GetDirectories(tempPath, "worker-test-*");
        
        foreach (var dir in workerTestDirs)
        {
            try
            {
                // 先清理 worktree
                var worktreesDir = Path.Combine(dir, ".pudding", "worktrees");
                if (Directory.Exists(worktreesDir))
                {
                    foreach (var worktree in Directory.GetDirectories(worktreesDir))
                    {
                        try
                        {
                            // 从 git 中移除 worktree
                            await RunGitAsync(dir, ["worktree", "remove", "-f", worktree]);
                        }
                        catch
                        {
                            // 忽略失败
                        }
                    }
                    Directory.Delete(worktreesDir, true);
                }
                
                // 删除分支
                var branches = await RunGitAsync(dir, ["branch", "--list", "swarm/*"]);
                foreach (var branch in branches.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var branchName = branch.Trim('*', ' ');
                    if (!string.IsNullOrWhiteSpace(branchName))
                    {
                        try
                        {
                            await RunGitAsync(dir, ["branch", "-D", branchName]);
                        }
                        catch
                        {
                            // 忽略失败
                        }
                    }
                }
                
                Directory.Delete(dir, true);
            }
            catch
            {
                // 忽略清理失败
            }
        }
        
        await Task.CompletedTask;
    }

    /// <summary>
    /// 清理所有测试后留下的 Git worktree 和分支。
    /// </summary>
    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        var tempPath = Path.GetTempPath();
        var workerTestDirs = Directory.GetDirectories(tempPath, "worker-test-*");
        
        foreach (var dir in workerTestDirs)
        {
            try
            {
                // 先清理 worktree
                var worktreesDir = Path.Combine(dir, ".pudding", "worktrees");
                if (Directory.Exists(worktreesDir))
                {
                    foreach (var worktree in Directory.GetDirectories(worktreesDir))
                    {
                        try
                        {
                            await RunGitAsync(dir, ["worktree", "remove", "-f", worktree]);
                        }
                        catch
                        {
                            // 忽略失败
                        }
                    }
                    Directory.Delete(worktreesDir, true);
                }
                
                // 删除分支
                var branches = await RunGitAsync(dir, ["branch", "--list", "swarm/*"]);
                foreach (var branch in branches.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var branchName = branch.Trim('*', ' ');
                    if (!string.IsNullOrWhiteSpace(branchName))
                    {
                        try
                        {
                            await RunGitAsync(dir, ["branch", "-D", branchName]);
                        }
                        catch
                        {
                            // 忽略失败
                        }
                    }
                }
                
                Directory.Delete(dir, true);
            }
            catch
            {
                // 忽略清理失败
            }
        }
        
        await Task.CompletedTask;
    }

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
            Assert.Contains(worker.Id, worker.WorktreePath);
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
            Assert.HasCount(2, workers);
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
            Assert.IsEmpty(workers);
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
            Assert.StartsWith("Builder-", worker.Name);
            Assert.IsGreaterThan(worker.Name.Length, 8); // "Builder-" + at least some chars
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
            Assert.HasCount(1, workers);
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
        await RunGitAsync(testDir, ["commit", "-m \"Initial commit\"", "--allow-empty"]);

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
            // 先强制移除所有 worktree
            var worktreesDir = Path.Combine(testDir, ".pudding", "worktrees");
            if (Directory.Exists(worktreesDir))
            {
                foreach (var worktree in Directory.GetDirectories(worktreesDir))
                {
                    try
                    {
                        // 使用 -f 强制移除 worktree
                        await RunGitAsync(testDir, ["worktree", "remove", "-f", worktree]);
                    }
                    catch
                    {
                        // 忽略移除失败
                    }
                }
                // 删除 worktrees 目录
                try
                {
                    Directory.Delete(worktreesDir, true);
                }
                catch
                {
                    // 忽略删除失败
                }
            }

            // 使用 git worktree prune 清理死链接
            try
            {
                await RunGitAsync(testDir, ["worktree", "prune"]);
            }
            catch
            {
                // 忽略 prune 失败
            }

            // 删除所有 worktree 分支
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

            // 最后删除目录
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
