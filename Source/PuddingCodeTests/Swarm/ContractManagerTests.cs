using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Swarm;

namespace PuddingCodeTests.Swarm;

[TestClass]
public class ContractManagerTests
{
    [TestMethod]
    public async Task InitializeSwarmDirectoryAsync_CreatesAllSubdirectories()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"swarm-test-{Guid.NewGuid():N}");
        try
        {
            // Don't pre-create directory - let ContractManager do it
            var manager = new ContractManager(testDir);

            // Act
            var swarmRoot = await manager.InitializeSwarmDirectoryAsync();

            // Assert
            Assert.IsTrue(Directory.Exists(swarmRoot));
            Assert.IsTrue(Directory.Exists(Path.Combine(swarmRoot, "contracts")));
            Assert.IsTrue(Directory.Exists(Path.Combine(swarmRoot, "tasks")));
            Assert.IsTrue(Directory.Exists(Path.Combine(swarmRoot, "messages")));
            Assert.IsTrue(Directory.Exists(Path.Combine(swarmRoot, "worktrees")));
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    [TestMethod]
    public async Task InitializeSwarmDirectoryAsync_CreatesConfigJson()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"swarm-test-{Guid.NewGuid():N}");
        try
        {
            // Don't pre-create directory - let ContractManager do it
            var manager = new ContractManager(testDir);

            // Act
            var swarmRoot = await manager.InitializeSwarmDirectoryAsync();
            var configPath = Path.Combine(swarmRoot, "config.json");

            // Assert
            Assert.IsTrue(File.Exists(configPath));
            var configContent = await File.ReadAllTextAsync(configPath);
            Assert.IsTrue(configContent.Contains("\"version\""));
            Assert.IsTrue(configContent.Contains("\"0.8\""));
            Assert.IsTrue(configContent.Contains("\"mode\""));
            Assert.IsTrue(configContent.Contains("\"local\""));
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }
}
