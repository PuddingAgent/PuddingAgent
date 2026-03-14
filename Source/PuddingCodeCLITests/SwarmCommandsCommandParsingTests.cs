namespace PuddingCodeCLITests.Commands;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCodeCLI.Commands;
using System.Reflection;

/// <summary>
/// Tests for the <see cref="SwarmCommands"/> class command parsing and routing logic.
/// </summary>
[TestClass]
public sealed class SwarmCommandsCommandParsingTests
{
    [TestMethod]
    public void HandleCommand_EmptyInput_DoesNotThrow()
    {
        // Arrange
        var swarmCommands = CreateSwarmCommands();

        // Act & Assert
        swarmCommands.HandleCommand("");
        // No exception = pass
    }

    [TestMethod]
    public void HandleCommand_NonSwarmCommand_IgnoresSilently()
    {
        // Arrange
        var swarmCommands = CreateSwarmCommands();

        // Act & Assert
        swarmCommands.HandleCommand("/help");
        swarmCommands.HandleCommand("/exit");
        // No exception = pass
    }

    [TestMethod]
    public void HandleCommand_SwarmOnly_NoException()
    {
        // Arrange
        var swarmCommands = CreateSwarmCommands();

        // Act & Assert
        swarmCommands.HandleCommand("/swarm");
        // Should show usage, no exception
    }

    [TestMethod]
    public void HandleCommand_SwarmHelp_NoException()
    {
        // Arrange
        var swarmCommands = CreateSwarmCommands();

        // Act & Assert
        swarmCommands.HandleCommand("/swarm help");
        // Should show usage, no exception
    }

    // Note: Tests for /swarm status and /swarm cancel require proper mocking
    // which is beyond the scope of these parsing tests. These commands would
    // need IWorkerManager and GitSnapshotService mocks to test properly.

    // Note: Case insensitive and task description tests require proper dependencies
    // to be mocked. These are tested manually or via integration tests.

    private static SwarmCommands CreateSwarmCommands()
    {
        // Create with null dependencies for command parsing tests
        // These tests only verify command routing, not execution
        return new SwarmCommands(
            orchestratorFactory: null!,
            workerManager: null!,
            snapshotService: null!,
            projectRoot: Path.GetTempPath());
    }
}
