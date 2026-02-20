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

    #region DefineContractAsync Tests

    [TestMethod]
    public async Task DefineContractAsync_CreatesContractWithCorrectStructure()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"swarm-test-{Guid.NewGuid():N}");
        try
        {
            var manager = new ContractManager(testDir);
            var specification = @"
                Create file src/Auth/LoginService.cs
                Implement class LoginService with method LoginAsync
                The service should handle user authentication
            ";

            // Act
            var contract = await manager.DefineContractAsync(specification);

            // Assert
            Assert.IsNotNull(contract);
            Assert.IsNotNull(contract.Id);
            Assert.IsTrue(contract.Id.StartsWith("contract-"));
            Assert.AreEqual(specification, contract.Specification);
            Assert.IsNotNull(contract.Files);
            Assert.IsNotNull(contract.Symbols);
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    [TestMethod]
    public async Task DefineContractAsync_ExtractsFilePathsFromSpecification()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"swarm-test-{Guid.NewGuid():N}");
        try
        {
            var manager = new ContractManager(testDir);
            var specification = @"
                Create file src/Auth/LoginService.cs
                Modify src/Auth/IAuthService.cs
                Add validation in file src/Auth/Validators.cs
            ";

            // Act
            var contract = await manager.DefineContractAsync(specification);

            // Assert
            Assert.IsNotNull(contract);
            Assert.IsTrue(contract.Files.Count > 0, "Should extract at least one file path");
            CollectionAssert.Contains(contract.Files.ToList(), "src/Auth/LoginService.cs");
            CollectionAssert.Contains(contract.Files.ToList(), "src/Auth/IAuthService.cs");
            CollectionAssert.Contains(contract.Files.ToList(), "src/Auth/Validators.cs");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    [TestMethod]
    public async Task DefineContractAsync_ExtractsSymbolsFromSpecification()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"swarm-test-{Guid.NewGuid():N}");
        try
        {
            var manager = new ContractManager(testDir);
            var specification = @"
                Create class AuthService
                Implement interface IAuthService
                Create UserManager for user management
            ";

            // Act
            var contract = await manager.DefineContractAsync(specification);

            // Assert
            Assert.IsNotNull(contract);
            Assert.IsTrue(contract.Symbols.Count > 0, "Should extract at least one symbol");
            // The implementation extracts class/interface names
            var symbols = contract.Symbols.ToList();
            Assert.IsTrue(symbols.Contains("AuthService"), "Should contain AuthService");
            Assert.IsTrue(symbols.Contains("IAuthService"), "Should contain IAuthService");
            Assert.IsTrue(symbols.Contains("UserManager"), "Should contain UserManager");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    [TestMethod]
    public async Task DefineContractAsync_FiltersOutKeywords()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"swarm-test-{Guid.NewGuid():N}");
        try
        {
            var manager = new ContractManager(testDir);
            var specification = @"
                Create class public static void string class interface
                The AuthService should be public and static
            ";

            // Act
            var contract = await manager.DefineContractAsync(specification);

            // Assert
            Assert.IsNotNull(contract);
            // Should not contain C# keywords
            CollectionAssert.DoesNotContain(contract.Symbols.ToList(), "class");
            CollectionAssert.DoesNotContain(contract.Symbols.ToList(), "interface");
            CollectionAssert.DoesNotContain(contract.Symbols.ToList(), "public");
            CollectionAssert.DoesNotContain(contract.Symbols.ToList(), "static");
            CollectionAssert.DoesNotContain(contract.Symbols.ToList(), "void");
            CollectionAssert.DoesNotContain(contract.Symbols.ToList(), "string");
            CollectionAssert.DoesNotContain(contract.Symbols.ToList(), "The");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    #endregion

    #region ValidateContractAsync Tests

    [TestMethod]
    public async Task ValidateContractAsync_ReturnsFalseForNonExistentContract()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"swarm-test-{Guid.NewGuid():N}");
        try
        {
            var manager = new ContractManager(testDir);
            await manager.InitializeSwarmDirectoryAsync();

            // Act
            var result = await manager.ValidateContractAsync("contract-nonexistent", testDir);

            // Assert
            Assert.IsFalse(result, "Should return false for non-existent contract");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    [TestMethod]
    public async Task ValidateContractAsync_ValidatesSymbolExistence()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"swarm-test-{Guid.NewGuid():N}");
        var worktreePath = Path.Combine(testDir, "worktree");
        try
        {
            var manager = new ContractManager(testDir);
            await manager.InitializeSwarmDirectoryAsync();

            // Use IAuthService pattern which the implementation extracts well
            var specification = "IAuthService";
            var contract = await manager.DefineContractAsync(specification);

            // Verify the symbol was extracted
            Assert.IsTrue(contract.Symbols.Contains("IAuthService"), "IAuthService should be extracted as a symbol");

            // Create worktree directory with implementation
            Directory.CreateDirectory(worktreePath);
            var sourceFile = Path.Combine(worktreePath, "IAuthService.cs");
            await File.WriteAllTextAsync(sourceFile, @"
                namespace TestNamespace
                {
                    public interface IAuthService
                    {
                        Task<bool> LoginAsync(string user, string password);
                    }
                }
            ");

            // Act
            var result = await manager.ValidateContractAsync(contract.Id, worktreePath);

            // Assert
            Assert.IsTrue(result, $"Should return true when all symbols exist. Symbols: {string.Join(", ", contract.Symbols)}");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    [TestMethod]
    public async Task ValidateContractAsync_ReturnsFalseWhenSymbolMissing()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"swarm-test-{Guid.NewGuid():N}");
        var worktreePath = Path.Combine(testDir, "worktree");
        try
        {
            var manager = new ContractManager(testDir);
            await manager.InitializeSwarmDirectoryAsync();

            // Create a contract with specific symbols
            var specification = "Create class MissingService with method MissingMethod";
            var contract = await manager.DefineContractAsync(specification);

            // Create worktree directory WITHOUT the implementation
            Directory.CreateDirectory(worktreePath);
            var otherFile = Path.Combine(worktreePath, "OtherService.cs");
            await File.WriteAllTextAsync(otherFile, @"
                namespace TestNamespace
                {
                    public class OtherService
                    {
                        public void OtherMethod() { }
                    }
                }
            ");

            // Act
            var result = await manager.ValidateContractAsync(contract.Id, worktreePath);

            // Assert
            Assert.IsFalse(result, "Should return false when symbols are missing");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    #endregion

    #region Contract Persistence Tests

    [TestMethod]
    public async Task DefineContractAsync_PersistsContractToJsonFile()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"swarm-test-{Guid.NewGuid():N}");
        try
        {
            var manager = new ContractManager(testDir);
            await manager.InitializeSwarmDirectoryAsync();
            var specification = "Create class PersistentService";

            // Act
            var contract = await manager.DefineContractAsync(specification);

            // Assert - Check file exists
            // ContractManager uses baseDirectory as the swarm root, so contracts go in {baseDirectory}/contracts
            var contractsDir = Path.Combine(testDir, "contracts");
            var contractFilePath = Path.Combine(contractsDir, $"{contract.Id}.json");
            Assert.IsTrue(File.Exists(contractFilePath), $"Contract file should be persisted. Expected path: {contractFilePath}");

            // Verify file content
            var jsonContent = await File.ReadAllTextAsync(contractFilePath);
            Assert.IsNotNull(jsonContent);
            Assert.IsTrue(jsonContent.Contains(contract.Id));
            Assert.IsTrue(jsonContent.Contains("PersistentService"));
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    [TestMethod]
    public async Task DefineContractAsync_PersistsContractWithAllProperties()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"swarm-test-{Guid.NewGuid():N}");
        try
        {
            var manager = new ContractManager(testDir);
            await manager.InitializeSwarmDirectoryAsync();
            var specification = @"
                Create file src/Example.cs
                Implement class ExampleService
                With method ProcessData
            ";

            // Act
            var contract = await manager.DefineContractAsync(specification);

            // Assert - Load and verify persisted contract
            // ContractManager uses baseDirectory as the swarm root, so contracts go in {baseDirectory}/contracts
            var contractsDir = Path.Combine(testDir, "contracts");
            var contractFilePath = Path.Combine(contractsDir, $"{contract.Id}.json");
            Assert.IsTrue(File.Exists(contractFilePath), $"Contract file should exist. Path: {contractFilePath}");
            
            var jsonContent = await File.ReadAllTextAsync(contractFilePath);

            // Verify all properties are persisted
            Assert.IsTrue(jsonContent.Contains("\"Id\""));
            Assert.IsTrue(jsonContent.Contains("\"Files\""));
            Assert.IsTrue(jsonContent.Contains("\"Symbols\""));
            Assert.IsTrue(jsonContent.Contains("\"Specification\""));
            Assert.IsTrue(jsonContent.Contains("ExampleService"));
            Assert.IsTrue(jsonContent.Contains("ProcessData"));
            Assert.IsTrue(jsonContent.Contains("src/Example.cs"));
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    [TestMethod]
    public async Task LoadContractAsync_DeserializesPersistedContract()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"swarm-test-{Guid.NewGuid():N}");
        try
        {
            var manager = new ContractManager(testDir);
            await manager.InitializeSwarmDirectoryAsync();
            // Use interface pattern which is extracted reliably
            var specification = "IAuthService";

            // Create and persist contract
            var originalContract = await manager.DefineContractAsync(specification);

            // Verify contract was persisted
            var contractsDir = Path.Combine(testDir, "contracts");
            var contractFilePath = Path.Combine(contractsDir, $"{originalContract.Id}.json");
            Assert.IsTrue(File.Exists(contractFilePath), "Contract should be persisted to disk");

            // Act - Verify contract can be loaded and validated
            var worktreePath = Path.Combine(testDir, "worktree");
            Directory.CreateDirectory(worktreePath);
            
            // Create implementation to make validation succeed
            var implFile = Path.Combine(worktreePath, "IAuthService.cs");
            await File.WriteAllTextAsync(implFile, @"
                namespace TestNamespace
                {
                    public interface IAuthService
                    {
                        Task<bool> LoginAsync(string user, string password);
                    }
                }
            ");
            
            var isValid = await manager.ValidateContractAsync(originalContract.Id, worktreePath);

            // Assert
            Assert.IsTrue(isValid, $"Contract should be loadable and validatable after persistence. Symbols: {string.Join(", ", originalContract.Symbols)}");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    [TestMethod]
    public async Task DefineContractAsync_GeneratesUniqueContractId()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"swarm-test-{Guid.NewGuid():N}");
        try
        {
            var manager = new ContractManager(testDir);

            // Act - Create multiple contracts
            var contract1 = await manager.DefineContractAsync("Create class Service1");
            var contract2 = await manager.DefineContractAsync("Create class Service2");
            var contract3 = await manager.DefineContractAsync("Create class Service3");

            // Assert
            Assert.IsNotNull(contract1.Id);
            Assert.IsNotNull(contract2.Id);
            Assert.IsNotNull(contract3.Id);
            
            // All IDs should be unique
            Assert.AreNotEqual(contract1.Id, contract2.Id);
            Assert.AreNotEqual(contract2.Id, contract3.Id);
            Assert.AreNotEqual(contract1.Id, contract3.Id);
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }

    #endregion
}
