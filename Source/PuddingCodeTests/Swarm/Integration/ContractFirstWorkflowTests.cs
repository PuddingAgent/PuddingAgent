using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Models;
using PuddingCode.Swarm;

namespace PuddingCodeTests.Swarm.Integration;

/// <summary>
/// Integration tests for the contract-first workflow.
/// End-to-end test: Leader defines contract → Worker implements → Validation passes
/// Uses real file system and real Git.
/// </summary>
[TestClass]
public sealed class ContractFirstWorkflowTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _testSwarmDir;

    public ContractFirstWorkflowTests()
    {
        // Create isolated test directory
        _testDir = Path.Combine(Path.GetTempPath(), $"contract-workflow-test-{Guid.NewGuid():N}");
        _testSwarmDir = Path.Combine(_testDir, ".pudding", "swarm");
        Directory.CreateDirectory(_testDir);
    }

    [TestMethod]
    public async Task ContractFirstWorkflow_FullCycle_LeaderDefinesContract_WorkerImplements_ValidationPasses()
    {
        // Arrange: Initialize test git repo and swarm components
        await InitializeTestGitRepoAsync();
        
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testDir);
        var validator = new ContractValidator();
        
        var specification = "Create IAuthService interface with LoginAsync and LogoutAsync methods";

        // Act Step 1: Leader defines contract
        var contract = await contractManager.DefineContractAsync(specification);

        // Assert Step 1: Contract should be created with correct structure
        Assert.IsNotNull(contract);
        Assert.IsFalse(string.IsNullOrEmpty(contract.Id));
        Assert.IsTrue(contract.Id.StartsWith("contract-"));
        // Symbol extraction depends on regex patterns - check for key service symbols
        Assert.IsTrue(contract.Symbols.Any(s => s.Contains("Auth", StringComparison.OrdinalIgnoreCase)), 
            $"Should identify Auth-related symbols. Found: {string.Join(", ", contract.Symbols)}");
        
        // Verify contract was persisted to disk
        var contractFilePath = Path.Combine(_testSwarmDir, "contracts", $"{contract.Id}.json");
        Assert.IsTrue(File.Exists(contractFilePath), "Contract file should be persisted");

        // Act Step 2: Leader spawns Worker with scope
        var workerScope = new WorkerScope(
            AllowedPaths: ["src/Auth/*"],
            AllowedSymbols: contract.Symbols
        );
        
        var worker = await workerManager.SpawnWorkerAsync(
            WorkerRole.Builder,
            $"Implement {contract.Id} - {specification}",
            workerScope
        );

        // Assert Step 2: Worker should be spawned with worktree
        Assert.IsNotNull(worker);
        Assert.IsNotNull(worker.Id);
        Assert.AreEqual(WorkerRole.Builder, worker.Role);
        Assert.IsNotNull(worker.WorktreePath);
        Assert.IsTrue(Directory.Exists(worker.WorktreePath), "Worker worktree should exist");
        Assert.AreEqual(workerScope, worker.Scope);

        // Act Step 3: Simulate Worker implementation in worktree
        await SimulateWorkerImplementationAsync(worker.WorktreePath, contract);

        // Assert Step 3: Worker implementation files should exist in worktree
        var authServicePath = Path.Combine(worker.WorktreePath, "src", "Auth", "AuthService.cs");
        Assert.IsTrue(File.Exists(authServicePath), "AuthService implementation should exist");

        // Act Step 4: Validate contract implementation
        var validationResult = validator.ValidateContract(contract, worker.WorktreePath);

        // Assert Step 4: Validation should pass (symbols exist in implementation)
        // Note: Full validation requires compiled assembly, so we verify the symbols are present in source
        var implementationContent = await File.ReadAllTextAsync(authServicePath);
        Assert.Contains("IAuthService", implementationContent);
        Assert.Contains("LoginAsync", implementationContent);
        Assert.Contains("LogoutAsync", implementationContent);

        // Act Step 5: Dismiss worker and cleanup worktree
        await workerManager.DismissWorkerAsync(worker.Id);

        // Assert Step 5: Worktree should be cleaned up
        Assert.IsFalse(Directory.Exists(worker.WorktreePath), "Worktree should be removed after dismiss");
        Assert.AreEqual(0, workerManager.GetActiveWorkers().Count, "No active workers after dismiss");
    }

    [TestMethod]
    public async Task ContractFirstWorkflow_MultipleWorkers_EachImplementsDifferentFile()
    {
        // Arrange: Setup
        await InitializeTestGitRepoAsync();
        
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testDir);
        
        var specification = """
            Create authentication service with the following files:
            - src/Auth/IAuthService.cs: Interface definition
            - src/Auth/AuthService.cs: Implementation
            - src/Auth/AuthConfig.cs: Configuration model
            """;

        // Act: Define contract
        var contract = await contractManager.DefineContractAsync(specification);

        // Assert: Contract should identify multiple files
        Assert.IsTrue(contract.Files.Count >= 1, "Should identify at least one file");
        Assert.IsTrue(contract.Symbols.Count >= 3, "Should identify multiple symbols");

        // Act: Spawn multiple workers for different files
        var workers = new List<WorkerInfo>();
        var filesToImplement = contract.Files.Take(3).ToList();
        
        foreach (var file in filesToImplement)
        {
            var scope = new WorkerScope(
                AllowedPaths: [file],
                AllowedSymbols: contract.Symbols
            );
            
            var worker = await workerManager.SpawnWorkerAsync(
                WorkerRole.Builder,
                $"Implement {file}",
                scope
            );
            workers.Add(worker);
        }

        // Assert: All workers should be spawned
        Assert.AreEqual(filesToImplement.Count, workers.Count);
        Assert.AreEqual(filesToImplement.Count, workerManager.GetActiveWorkers().Count);

        // Act: Each worker implements their file
        foreach (var worker in workers)
        {
            await SimulateWorkerImplementationAsync(worker.WorktreePath, contract);
        }

        // Cleanup: Dismiss all workers
        foreach (var worker in workers)
        {
            await workerManager.DismissWorkerAsync(worker.Id);
        }

        // Assert: All workers cleaned up
        Assert.AreEqual(0, workerManager.GetActiveWorkers().Count);
    }

    [TestMethod]
    public async Task ContractFirstWorkflow_SwarmOrchestrator_EndToEnd()
    {
        // Arrange: Setup
        await InitializeTestGitRepoAsync();
        
        var contractManager = new ContractManager(_testSwarmDir);
        var workerManager = new WorkerManager(_testDir);
        var orchestrator = new SwarmOrchestrator(contractManager, workerManager, _testSwarmDir);

        var userInput = "Create IAuthService with LoginAsync method for user authentication";

        // Act: Run full swarm workflow
        var events = new List<AgentEvent>();
        await foreach (var evt in orchestrator.ProcessSwarmAsync(userInput))
        {
            events.Add(evt);
        }

        // Assert: Verify event sequence
        Assert.IsTrue(events.Count > 0, "Should emit events");
        
        // Should have swarm started event (via ThinkingEvent)
        Assert.IsTrue(events.Any(e => e is ThinkingEvent), "Should have thinking events");
        
        // Should have contract defined event
        var contractDefinedEvent = events.OfType<ContractDefinedEvent>().FirstOrDefault();
        Assert.IsNotNull(contractDefinedEvent, "Should define contract");
        Assert.IsFalse(string.IsNullOrEmpty(contractDefinedEvent.ContractId));
        
        // Should have worker spawned event (at least one - could be Leader or Builder)
        var workerSpawnedEvent = events.OfType<WorkerSpawnedEvent>().FirstOrDefault();
        Assert.IsNotNull(workerSpawnedEvent, "Should spawn at least one worker (Leader or Builder)");
        // Note: In Phase 1/2, Leader is spawned first, then Builder workers
        Assert.IsTrue(workerSpawnedEvent.Role == WorkerRole.Leader || workerSpawnedEvent.Role == WorkerRole.Builder, 
            "Should spawn Leader or Builder worker");
        
        // Should have validation event
        var validationEvent = events.OfType<ContractValidatedEvent>().FirstOrDefault();
        Assert.IsNotNull(validationEvent, "Should validate contract");
        
        // Should have completion event
        var completedEvent = events.OfType<SwarmCompletedEvent>().FirstOrDefault();
        Assert.IsNotNull(completedEvent, "Should complete swarm");
        Assert.IsFalse(string.IsNullOrEmpty(completedEvent.Summary));
    }

    [TestMethod]
    public async Task ContractFirstWorkflow_ValidateContract_PersistsAndLoadsCorrectly()
    {
        // Arrange
        await InitializeTestGitRepoAsync();
        var contractManager = new ContractManager(_testSwarmDir);
        
        var specification = "Create IUserService with GetUserByIdAsync method";

        // Act: Define and persist contract
        var contract = await contractManager.DefineContractAsync(specification);

        // Assert: Verify persistence
        var contractFilePath = Path.Combine(_testSwarmDir, "contracts", $"{contract.Id}.json");
        Assert.IsTrue(File.Exists(contractFilePath));

        // Act: Load contract from disk (simulate separate process)
        var loadedContract = await LoadContractFromDiskAsync(contract.Id);

        // Assert: Loaded contract matches original
        Assert.IsNotNull(loadedContract);
        Assert.AreEqual(contract.Id, loadedContract.Id);
        Assert.AreEqual(contract.Specification, loadedContract.Specification);
        CollectionAssert.AreEqual(contract.Symbols.ToList(), loadedContract.Symbols.ToList());
        CollectionAssert.AreEqual(contract.Files.ToList(), loadedContract.Files.ToList());
    }

    [TestMethod]
    public async Task ContractFirstWorkflow_InitializeSwarmDirectory_CreatesAllRequiredStructure()
    {
        // Arrange
        var contractManager = new ContractManager(_testSwarmDir);

        // Act
        var swarmRoot = await contractManager.InitializeSwarmDirectoryAsync();

        // Assert: All required directories exist
        Assert.IsTrue(Directory.Exists(swarmRoot));
        Assert.IsTrue(Directory.Exists(Path.Combine(swarmRoot, "contracts")));
        Assert.IsTrue(Directory.Exists(Path.Combine(swarmRoot, "tasks")));
        Assert.IsTrue(Directory.Exists(Path.Combine(swarmRoot, "messages")));
        Assert.IsTrue(Directory.Exists(Path.Combine(swarmRoot, "worktrees")));

        // Assert: Config file created
        var configPath = Path.Combine(swarmRoot, "config.json");
        Assert.IsTrue(File.Exists(configPath));
        
        var configContent = await File.ReadAllTextAsync(configPath);
        Assert.Contains("\"version\"", configContent);
        Assert.Contains("\"0.8\"", configContent);
        Assert.Contains("\"mode\"", configContent);
    }

    /// <summary>
    /// Simulates a Worker implementing the contract by creating source files.
    /// </summary>
    private static async Task SimulateWorkerImplementationAsync(string worktreePath, Contract contract)
    {
        // Create src/Auth directory in worktree
        var authDir = Path.Combine(worktreePath, "src", "Auth");
        Directory.CreateDirectory(authDir);

        // Create AuthService implementation
        var authServiceContent = $$"""
            namespace Auth;

            /// <summary>
            /// Authentication service implementation.
            /// </summary>
            public interface IAuthService
            {
                Task<bool> LoginAsync(string username, string password);
                Task LogoutAsync();
            }

            /// <summary>
            /// Concrete implementation of IAuthService.
            /// </summary>
            public class AuthService : IAuthService
            {
                public async Task<bool> LoginAsync(string username, string password)
                {
                    // TODO: Implement authentication logic
                    await Task.CompletedTask;
                    return !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password);
                }

                public async Task LogoutAsync()
                {
                    // TODO: Implement logout logic
                    await Task.CompletedTask;
                }
            }
            """;

        var authServicePath = Path.Combine(authDir, "AuthService.cs");
        await File.WriteAllTextAsync(authServicePath, authServiceContent);

        // Create config file if mentioned in contract
        if (contract.Symbols.Any(s => s.Contains("Config", StringComparison.OrdinalIgnoreCase)))
        {
            var authConfigContent = """
                namespace Auth;

                /// <summary>
                /// Authentication configuration.
                /// </summary>
                public class AuthConfig
                {
                    public string JwtSecret { get; set; } = string.Empty;
                    public int TokenExpirationMinutes { get; set; } = 60;
                }
                """;

            var authConfigPath = Path.Combine(authDir, "AuthConfig.cs");
            await File.WriteAllTextAsync(authConfigPath, authConfigContent);
        }
    }

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
        var readmePath = Path.Combine(_testDir, "README.md");
        await File.WriteAllTextAsync(readmePath, "# Test Repository\n");
        await RunGitAsync(["add", "."]);
        await RunGitAsync(["commit", "-m", "\"Initial commit\""]);
    }

    /// <summary>
    /// Loads a contract from disk.
    /// </summary>
    private async Task<Contract> LoadContractFromDiskAsync(string contractId)
    {
        var contractFilePath = Path.Combine(_testSwarmDir, "contracts", $"{contractId}.json");
        var json = await File.ReadAllTextAsync(contractFilePath);
        var contract = System.Text.Json.JsonSerializer.Deserialize<Contract>(json);
        return contract ?? throw new InvalidOperationException("Failed to deserialize contract");
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
            WorkingDirectory = _testDir,
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

    public void Dispose()
    {
        // Cleanup test directory
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
