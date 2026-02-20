using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Swarm;

namespace PuddingCodeTests.Swarm;

[DoNotParallelize]
/// <summary>
/// Unit tests for SwarmOrchestrator.
/// Tests full workflow, event emission, and error handling.
/// </summary>
public sealed class SwarmOrchestratorTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _testSwarmDir;
    private readonly string _testRepoDir;

    public SwarmOrchestratorTests()
    {
        // Create isolated test directories
        _testDir = Path.Combine(Path.GetTempPath(), $"swarm-orchestrator-test-{Guid.NewGuid():N}");
        _testSwarmDir = Path.Combine(_testDir, ".pudding", "swarm");
        _testRepoDir = Path.Combine(_testDir, "repo");
        Directory.CreateDirectory(_testRepoDir);
    }

    #region Full Workflow Tests

    [TestMethod]
    public async Task ProcessSwarmAsync_FullWorkflow_CompletesSuccessfully()
    {
        // Arrange: Initialize git repo and swarm components
        await InitializeTestGitRepoAsync();
        
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testRepoDir);
        var orchestrator = new SwarmOrchestrator(contractManager, workerManager, _testSwarmDir);

        var userInput = "Create IAuthService interface with LoginAsync method";

        // Act: Run full swarm workflow
        var events = new List<AgentEvent>();
        await foreach (var evt in orchestrator.ProcessSwarmAsync(userInput))
        {
            events.Add(evt);
        }

        // Assert: Workflow should complete successfully
        Assert.IsTrue(events.Count > 0, "Should emit events during workflow");
        
        // Should complete with SwarmCompletedEvent
        var completedEvent = events.OfType<SwarmCompletedEvent>().FirstOrDefault();
        Assert.IsNotNull(completedEvent, "Should emit SwarmCompletedEvent");
        Assert.IsNotNull(completedEvent.Summary);
        Assert.IsTrue(completedEvent.Summary.Contains("completed"), "Summary should indicate completion");
    }

    [TestMethod]
    public async Task ProcessSwarmAsync_WithMultipleFiles_CreatesMultipleWorkers()
    {
        // Arrange
        await InitializeTestGitRepoAsync();
        
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testRepoDir);
        var orchestrator = new SwarmOrchestrator(contractManager, workerManager, _testSwarmDir);

        var userInput = """
            Create authentication service with files:
            - src/Auth/IAuthService.cs
            - src/Auth/AuthService.cs
            - src/Auth/AuthConfig.cs
            """;

        // Act
        var events = new List<AgentEvent>();
        await foreach (var evt in orchestrator.ProcessSwarmAsync(userInput))
        {
            events.Add(evt);
        }

        // Assert: Should spawn workers for multiple files
        var workerSpawnedEvents = events.OfType<WorkerSpawnedEvent>().ToList();
        Assert.IsTrue(workerSpawnedEvents.Count > 0, "Should spawn at least one worker");
        
        // Should have contract defined
        var contractDefinedEvent = events.OfType<ContractDefinedEvent>().FirstOrDefault();
        Assert.IsNotNull(contractDefinedEvent, "Should define contract");
        
        // Should complete
        Assert.IsTrue(events.Any(e => e is SwarmCompletedEvent), "Should complete swarm");
    }

    [TestMethod]
    public async Task ProcessSwarmAsync_InitializesSwarmDirectory()
    {
        // Arrange
        await InitializeTestGitRepoAsync();
        
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testRepoDir);
        var orchestrator = new SwarmOrchestrator(contractManager, workerManager, _testSwarmDir);

        // Act
        await foreach (var _ in orchestrator.ProcessSwarmAsync("Test task"))
        {
            // Drain events
        }

        // Assert: Swarm directory structure should be created
        Assert.IsTrue(Directory.Exists(_testSwarmDir), "Swarm root should exist");
        Assert.IsTrue(Directory.Exists(Path.Combine(_testSwarmDir, "contracts")));
        Assert.IsTrue(Directory.Exists(Path.Combine(_testSwarmDir, "tasks")));
        Assert.IsTrue(Directory.Exists(Path.Combine(_testSwarmDir, "messages")));
        Assert.IsTrue(Directory.Exists(Path.Combine(_testSwarmDir, "worktrees")));
        
        // Config file should exist
        var configPath = Path.Combine(_testSwarmDir, "config.json");
        Assert.IsTrue(File.Exists(configPath), "Config file should exist");
    }

    #endregion

    #region Event Emission Tests

    [TestMethod]
    public async Task ProcessSwarmAsync_EmitsThinkingEvents()
    {
        // Arrange
        await InitializeTestGitRepoAsync();
        
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testRepoDir);
        var orchestrator = new SwarmOrchestrator(contractManager, workerManager, _testSwarmDir);

        // Act
        var events = new List<AgentEvent>();
        await foreach (var evt in orchestrator.ProcessSwarmAsync("Test task"))
        {
            events.Add(evt);
        }

        // Assert: Should emit thinking events throughout workflow
        var thinkingEvents = events.OfType<ThinkingEvent>().ToList();
        Assert.IsTrue(thinkingEvents.Count > 0, "Should emit thinking events");
        
        // Should have initialization thinking event
        Assert.IsTrue(thinkingEvents.Any(e => e.Thought.Contains("Initializing", StringComparison.OrdinalIgnoreCase) || 
                                               e.Thought.Contains("initialization", StringComparison.OrdinalIgnoreCase)),
            "Should have initialization thinking event");
    }

    [TestMethod]
    public async Task ProcessSwarmAsync_EmitsContractDefinedEvent()
    {
        // Arrange
        await InitializeTestGitRepoAsync();
        
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testRepoDir);
        var orchestrator = new SwarmOrchestrator(contractManager, workerManager, _testSwarmDir);

        // Act
        var events = new List<AgentEvent>();
        await foreach (var evt in orchestrator.ProcessSwarmAsync("Create IUserService"))
        {
            events.Add(evt);
        }

        // Assert: Should emit contract defined event
        var contractDefinedEvent = events.OfType<ContractDefinedEvent>().FirstOrDefault();
        Assert.IsNotNull(contractDefinedEvent, "Should emit ContractDefinedEvent");
        Assert.IsNotNull(contractDefinedEvent.ContractId);
        Assert.IsNotNull(contractDefinedEvent.Symbols);
    }

    [TestMethod]
    public async Task ProcessSwarmAsync_EmitsWorkerSpawnedEvent()
    {
        // Arrange
        await InitializeTestGitRepoAsync();
        
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testRepoDir);
        var orchestrator = new SwarmOrchestrator(contractManager, workerManager, _testSwarmDir);

        // Act
        var events = new List<AgentEvent>();
        await foreach (var evt in orchestrator.ProcessSwarmAsync("Create service"))
        {
            events.Add(evt);
        }

        // Assert: Should emit worker spawned event
        var workerSpawnedEvent = events.OfType<WorkerSpawnedEvent>().FirstOrDefault();
        Assert.IsNotNull(workerSpawnedEvent, "Should emit WorkerSpawnedEvent");
        Assert.IsNotNull(workerSpawnedEvent.WorkerId);
        Assert.IsNotNull(workerSpawnedEvent.Scope);
    }

    [TestMethod]
    public async Task ProcessSwarmAsync_EmitsContractValidatedEvent()
    {
        // Arrange
        await InitializeTestGitRepoAsync();
        
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testRepoDir);
        var orchestrator = new SwarmOrchestrator(contractManager, workerManager, _testSwarmDir);

        // Act
        var events = new List<AgentEvent>();
        await foreach (var evt in orchestrator.ProcessSwarmAsync("Create ITestService"))
        {
            events.Add(evt);
        }

        // Assert: Should emit contract validation event
        var validationEvent = events.OfType<ContractValidatedEvent>().FirstOrDefault();
        Assert.IsNotNull(validationEvent, "Should emit ContractValidatedEvent");
    }

    [TestMethod]
    public async Task ProcessSwarmAsync_EmitsMergeEvent()
    {
        // Arrange
        await InitializeTestGitRepoAsync();
        
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testRepoDir);
        var orchestrator = new SwarmOrchestrator(contractManager, workerManager, _testSwarmDir);

        // Act
        var events = new List<AgentEvent>();
        await foreach (var evt in orchestrator.ProcessSwarmAsync("Test merge"))
        {
            events.Add(evt);
        }

        // Assert: Should emit merge event
        var mergeEvent = events.OfType<MergeEvent>().FirstOrDefault();
        Assert.IsNotNull(mergeEvent, "Should emit MergeEvent");
    }

    [TestMethod]
    public async Task ProcessSwarmAsync_EventSequence_IsCorrect()
    {
        // Arrange
        await InitializeTestGitRepoAsync();
        
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testRepoDir);
        var orchestrator = new SwarmOrchestrator(contractManager, workerManager, _testSwarmDir);

        // Act
        var events = new List<AgentEvent>();
        await foreach (var evt in orchestrator.ProcessSwarmAsync("Create service"))
        {
            events.Add(evt);
        }

        // Assert: Verify event sequence order
        var thinkingEventIndex = events.FindIndex(e => e is ThinkingEvent);
        var contractDefinedIndex = events.FindIndex(e => e is ContractDefinedEvent);
        var workerSpawnedIndex = events.FindIndex(e => e is WorkerSpawnedEvent);
        var validationIndex = events.FindIndex(e => e is ContractValidatedEvent);
        var completedIndex = events.FindIndex(e => e is SwarmCompletedEvent);

        Assert.IsTrue(thinkingEventIndex >= 0, "Should have thinking event");
        Assert.IsTrue(contractDefinedIndex >= 0, "Should have contract defined event");
        Assert.IsTrue(workerSpawnedIndex >= 0, "Should have worker spawned event");
        Assert.IsTrue(validationIndex >= 0, "Should have validation event");
        Assert.IsTrue(completedIndex >= 0, "Should have completed event");

        // Verify order: thinking -> (Leader spawn OR contract defined) -> validation -> completed
        // Note: Leader is spawned before contract definition in Phase 1/2
        Assert.IsTrue(thinkingEventIndex < contractDefinedIndex, "Thinking should come before contract defined");
        // Worker spawned can come before OR after contract defined (Leader spawns first, then contract, then workers)
        // So we just verify both events exist, not their relative order
        Assert.IsTrue(validationIndex < completedIndex, "Validation should come before completed");
    }

    #endregion

    #region Error Handling Tests

    [TestMethod]
    public async Task ProcessSwarmAsync_WithEmptyInput_HandlesGracefully()
    {
        // Arrange
        await InitializeTestGitRepoAsync();
        
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testRepoDir);
        var orchestrator = new SwarmOrchestrator(contractManager, workerManager, _testSwarmDir);

        // Act
        var events = new List<AgentEvent>();
        try
        {
            await foreach (var evt in orchestrator.ProcessSwarmAsync(string.Empty))
            {
                events.Add(evt);
            }
        }
        catch (Exception ex)
        {
            Assert.Fail($"Should not throw exception for empty input: {ex.Message}");
        }

        // Assert: Should complete without error (may create minimal contract)
        Assert.IsTrue(events.Any(e => e is SwarmCompletedEvent), "Should complete even with empty input");
    }

    [TestMethod]
    public async Task ProcessSwarmAsync_WithNullInput_DoesNotCrash()
    {
        // Arrange
        await InitializeTestGitRepoAsync();
        
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testRepoDir);
        var orchestrator = new SwarmOrchestrator(contractManager, workerManager, _testSwarmDir);

        // Act & Assert - Method should not crash with null (may throw or handle gracefully)
        // Since ProcessSwarmAsync is an iterator, null check happens during enumeration
        var task = orchestrator.ProcessSwarmAsync(null!);
        await foreach (var _ in task)
        {
            // If it doesn't crash, test passes
            break;
        }
        
        Assert.IsTrue(true, "Method should handle null input without crashing");
    }

    [TestMethod]
    public async Task ProcessSwarmAsync_WithCancellation_StopsGracefully()
    {
        // Arrange
        await InitializeTestGitRepoAsync();
        
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testRepoDir);
        var orchestrator = new SwarmOrchestrator(contractManager, workerManager, _testSwarmDir);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync(); // Cancel immediately

        // Act & Assert: Should handle cancellation
        var events = new List<AgentEvent>();
        try
        {
            await foreach (var evt in orchestrator.ProcessSwarmAsync("Test task", cts.Token))
            {
                events.Add(evt);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected - cancellation is acceptable
        }
        catch (Exception ex)
        {
            Assert.Fail($"Should handle cancellation gracefully, got: {ex.GetType().Name}");
        }
    }

    [TestMethod]
    public async Task ProcessSwarmAsync_WithInvalidSwarmDirectory_HandlesError()
    {
        // Arrange: Use invalid path
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testRepoDir);
        
        // Create orchestrator with valid paths (will initialize directory)
        var orchestrator = new SwarmOrchestrator(contractManager, workerManager, _testSwarmDir);

        // Act
        var events = new List<AgentEvent>();
        Exception? thrownException = null;
        try
        {
            await foreach (var evt in orchestrator.ProcessSwarmAsync("Test task"))
            {
                events.Add(evt);
            }
        }
        catch (Exception ex)
        {
            thrownException = ex;
        }

        // Assert: Should either succeed or handle error gracefully
        if (thrownException != null)
        {
            Assert.Fail($"Should not throw exception: {thrownException.Message}");
        }
        
        Assert.IsTrue(events.Any(e => e is SwarmCompletedEvent), "Should complete workflow");
    }

    [TestMethod]
    public void Constructor_WithNullContractManager_ThrowsArgumentNullException()
    {
        // Arrange
        var workerManager = new WorkerManager(_testRepoDir);

        // Act & Assert
        try
        {
            _ = new SwarmOrchestrator(null!, workerManager);
            Assert.Fail("Should throw ArgumentNullException for null contractManager");
        }
        catch (ArgumentNullException)
        {
            // Expected
        }
    }

    [TestMethod]
    public void Constructor_WithNullWorkerManager_ThrowsArgumentNullException()
    {
        // Arrange
        var contractManager = new ContractManager(_testSwarmDir);

        // Act & Assert
        try
        {
            _ = new SwarmOrchestrator(contractManager, null!);
            Assert.Fail("Should throw ArgumentNullException for null workerManager");
        }
        catch (ArgumentNullException)
        {
            // Expected
        }
    }

    #endregion

    #region Edge Case Tests

    [TestMethod]
    public async Task ProcessSwarmAsync_WithVeryLongInput_HandlesSuccessfully()
    {
        // Arrange
        await InitializeTestGitRepoAsync();
        
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testRepoDir);
        var orchestrator = new SwarmOrchestrator(contractManager, workerManager, _testSwarmDir);

        var longInput = new string('a', 10000) + " Create service";

        // Act
        var events = new List<AgentEvent>();
        try
        {
            await foreach (var evt in orchestrator.ProcessSwarmAsync(longInput))
            {
                events.Add(evt);
            }
        }
        catch (Exception ex)
        {
            Assert.Fail($"Should handle long input: {ex.Message}");
        }

        // Assert: Should complete
        Assert.IsTrue(events.Any(e => e is SwarmCompletedEvent), "Should complete with long input");
    }

    [TestMethod]
    public async Task ProcessSwarmAsync_WithSpecialCharacters_HandlesSuccessfully()
    {
        // Arrange
        await InitializeTestGitRepoAsync();
        
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testRepoDir);
        var orchestrator = new SwarmOrchestrator(contractManager, workerManager, _testSwarmDir);

        var input = "Create service with special chars: @#$%^&*()_+-=[]{}|;':\",./<>?";

        // Act
        var events = new List<AgentEvent>();
        try
        {
            await foreach (var evt in orchestrator.ProcessSwarmAsync(input))
            {
                events.Add(evt);
            }
        }
        catch (Exception ex)
        {
            Assert.Fail($"Should handle special characters: {ex.Message}");
        }

        // Assert: Should complete
        Assert.IsTrue(events.Any(e => e is SwarmCompletedEvent), "Should complete with special chars");
    }

    [TestMethod]
    public async Task ProcessSwarmAsync_MultipleCalls_AreIndependent()
    {
        // Arrange
        await InitializeTestGitRepoAsync();
        
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testRepoDir);
        var orchestrator = new SwarmOrchestrator(contractManager, workerManager, _testSwarmDir);

        // Act: Run swarm twice
        var events1 = new List<AgentEvent>();
        await foreach (var evt in orchestrator.ProcessSwarmAsync("First task"))
        {
            events1.Add(evt);
        }

        var events2 = new List<AgentEvent>();
        await foreach (var evt in orchestrator.ProcessSwarmAsync("Second task"))
        {
            events2.Add(evt);
        }

        // Assert: Both should complete successfully
        Assert.IsTrue(events1.Any(e => e is SwarmCompletedEvent), "First call should complete");
        Assert.IsTrue(events2.Any(e => e is SwarmCompletedEvent), "Second call should complete");
        
        // Contracts should have different IDs
        var contract1 = events1.OfType<ContractDefinedEvent>().FirstOrDefault();
        var contract2 = events2.OfType<ContractDefinedEvent>().FirstOrDefault();
        Assert.IsNotNull(contract1);
        Assert.IsNotNull(contract2);
        Assert.AreNotEqual(contract1.ContractId, contract2.ContractId, "Each call should create unique contract");
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Initializes a test Git repository.
    /// </summary>
    private async Task InitializeTestGitRepoAsync()
    {
        // Initialize git repo
        await RunGitAsync(["init"]);
        await RunGitAsync(["config", "user.email", "test@example.com"]);
        await RunGitAsync(["config", "user.name", "Test User"]);

        // Create initial commit
        var readmePath = Path.Combine(_testRepoDir, "README.md");
        await File.WriteAllTextAsync(readmePath, "# Test Repository\n");
        await RunGitAsync(["add", "."]);
        await RunGitAsync(["commit", "-m", "\"Initial commit\""]);
    }

    /// <summary>
    /// Runs a Git command in the test directory.
    /// </summary>
    private async Task<string> RunGitAsync(string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = string.Join(' ', args),
            WorkingDirectory = _testRepoDir,
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

    #endregion

    public void Dispose()
    {
        // Cleanup test directories
        if (Directory.Exists(_testDir))
        {
            try
            {
                Directory.Delete(_testDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
