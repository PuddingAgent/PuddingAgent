using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Models;
using PuddingCode.Swarm;
using PuddingCode.Tools;

namespace PuddingCodeTests.Swarm.Integration;

/// <summary>
/// Integration tests for scope isolation enforcement.
/// Verifies that workers are restricted to their assigned scopes and cannot
/// modify files outside their allowed paths.
/// </summary>
[TestClass]
public sealed class ScopeIsolationTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _scopePath;
    private readonly string _outsidePath;
    private readonly FileTool _fileTool;

    public ScopeIsolationTests()
    {
        // Create isolated test directory structure
        _testDir = Path.Combine(Path.GetTempPath(), $"scope-isolation-test-{Guid.NewGuid():N}");
        _scopePath = Path.Combine(_testDir, "scope");
        _outsidePath = Path.Combine(_testDir, "outside");
        
        Directory.CreateDirectory(_scopePath);
        Directory.CreateDirectory(_outsidePath);
        
        // Create test files
        File.WriteAllText(Path.Combine(_scopePath, "Allowed.cs"), "// Allowed file");
        File.WriteAllText(Path.Combine(_outsidePath, "Blocked.cs"), "// Blocked file");
        
        _fileTool = new FileTool();
    }

    [TestMethod]
    public async Task ScopedFileTool_Write_OutsideScope_BlocksOperation()
    {
        // Arrange: Create worker with scope limited to _scopePath
        var scope = new WorkerScope(
            AllowedPaths: [$"{_scopePath}/*"],
            AllowedSymbols: []
        );
        var scopedTool = new ScopedFileTool(_fileTool, scope);
        
        var blockedFile = Path.Combine(_outsidePath, "NewFile.cs");
        var escapedPath = blockedFile.Replace("\\", "\\\\");
        var args = $$"""{"action": "write", "path": "{{escapedPath}}", "content": "// Should be blocked"}""";

        // Act
        var result = await scopedTool.ExecuteAsync(args);

        // Assert
        Assert.Contains("Error", result, "Should return error");
        Assert.Contains("scope", result, "Error should mention scope");
        Assert.IsFalse(File.Exists(blockedFile), "File should not be created outside scope");
    }

    [TestMethod]
    public async Task ScopedFileTool_List_OutsideScope_BlocksOperation()
    {
        // Arrange
        var scope = new WorkerScope(
            AllowedPaths: [$"{_scopePath}/*"],
            AllowedSymbols: []
        );
        var scopedTool = new ScopedFileTool(_fileTool, scope);
        
        var escapedPath = _outsidePath.Replace("\\", "\\\\");
        var args = $$"""{"action": "list", "path": "{{escapedPath}}"}""";

        // Act
        var result = await scopedTool.ExecuteAsync(args);

        // Assert
        Assert.Contains("Error", result, "Should return error");
        Assert.Contains("cannot modify", result, "Should mention cannot modify");
    }

    [TestMethod]
    public async Task ScopedFileTool_Write_WithinScope_Succeeds()
    {
        // Arrange
        var scope = new WorkerScope(
            AllowedPaths: [$"{_scopePath}/*"],
            AllowedSymbols: []
        );
        var scopedTool = new ScopedFileTool(_fileTool, scope);
        
        var allowedFile = Path.Combine(_scopePath, "NewAllowed.cs");
        var escapedPath = allowedFile.Replace("\\", "\\\\");
        var args = $$"""{"action": "write", "path": "{{escapedPath}}", "content": "// Allowed content"}""";

        // Act
        var result = await scopedTool.ExecuteAsync(args);

        // Assert
        Assert.IsTrue(result.Contains("Written", StringComparison.OrdinalIgnoreCase) || result.Contains("Success", StringComparison.OrdinalIgnoreCase), 
            $"Should succeed, got: {result}");
        Assert.IsTrue(File.Exists(allowedFile), "File should be created within scope");
    }

    [TestMethod]
    public async Task MultipleWorkers_NonOverlappingScopes_IsolatedFromEachOther()
    {
        // Arrange: Create two workers with non-overlapping scopes
        var authScope = Path.Combine(_testDir, "Auth");
        var apiScope = Path.Combine(_testDir, "Api");
        Directory.CreateDirectory(authScope);
        Directory.CreateDirectory(apiScope);
        
        var worker1Scope = new WorkerScope(
            AllowedPaths: [$"{authScope}/*"],
            AllowedSymbols: []
        );
        var worker2Scope = new WorkerScope(
            AllowedPaths: [$"{apiScope}/*"],
            AllowedSymbols: []
        );
        
        var worker1Tool = new ScopedFileTool(_fileTool, worker1Scope);
        var worker2Tool = new ScopedFileTool(_fileTool, worker2Scope);
        
        // Act: Worker 1 tries to write to Worker 2's scope
        var worker1BlockedFile = Path.Combine(apiScope, "FromWorker1.cs");
        var escapedPath1 = worker1BlockedFile.Replace("\\", "\\\\");
        var args1 = $$"""{"action": "write", "path": "{{escapedPath1}}", "content": "// From Worker 1"}""";
        var result1 = await worker1Tool.ExecuteAsync(args1);
        
        // Worker 2 tries to write to Worker 1's scope
        var worker2BlockedFile = Path.Combine(authScope, "FromWorker2.cs");
        var escapedPath2 = worker2BlockedFile.Replace("\\", "\\\\");
        var args2 = $$"""{"action": "write", "path": "{{escapedPath2}}", "content": "// From Worker 2"}""";
        var result2 = await worker2Tool.ExecuteAsync(args2);
        
        // Both workers can write to their own scopes
        var worker1AllowedFile = Path.Combine(authScope, "Worker1File.cs");
        var escapedPath3 = worker1AllowedFile.Replace("\\", "\\\\");
        var args3 = $$"""{"action": "write", "path": "{{escapedPath3}}", "content": "// Worker 1 own file"}""";
        var result3 = await worker1Tool.ExecuteAsync(args3);
        
        var worker2AllowedFile = Path.Combine(apiScope, "Worker2File.cs");
        var escapedPath4 = worker2AllowedFile.Replace("\\", "\\\\");
        var args4 = $$"""{"action": "write", "path": "{{escapedPath4}}", "content": "// Worker 2 own file"}""";
        var result4 = await worker2Tool.ExecuteAsync(args4);

        // Assert
        Assert.Contains("Error", result1, "Worker 1 should be blocked from Worker 2's scope");
        Assert.Contains("Error", result2, "Worker 2 should be blocked from Worker 1's scope");
        Assert.IsFalse(File.Exists(worker1BlockedFile), "Worker 1 file should not exist in Worker 2's scope");
        Assert.IsFalse(File.Exists(worker2BlockedFile), "Worker 2 file should not exist in Worker 1's scope");
        
        Assert.IsTrue(result3.Contains("Written", StringComparison.OrdinalIgnoreCase) || result3.Contains("Success", StringComparison.OrdinalIgnoreCase), 
            $"Worker 1 should write to own scope, got: {result3}");
        Assert.IsTrue(result4.Contains("Written", StringComparison.OrdinalIgnoreCase) || result4.Contains("Success", StringComparison.OrdinalIgnoreCase), 
            $"Worker 2 should write to own scope, got: {result4}");
        Assert.IsTrue(File.Exists(worker1AllowedFile), "Worker 1 file should exist in own scope");
        Assert.IsTrue(File.Exists(worker2AllowedFile), "Worker 2 file should exist in own scope");
    }

    [TestMethod]
    public async Task MultipleWorkers_OverlappingScopes_AllowsSharedAccess()
    {
        // Arrange: Create two workers with overlapping scopes
        var sharedScope = Path.Combine(_testDir, "Shared");
        Directory.CreateDirectory(sharedScope);
        
        var worker1Scope = new WorkerScope(
            AllowedPaths: [$"{sharedScope}/*", $"{_scopePath}/*"],
            AllowedSymbols: []
        );
        var worker2Scope = new WorkerScope(
            AllowedPaths: [$"{sharedScope}/*", $"{_outsidePath}/*"],
            AllowedSymbols: []
        );
        
        var worker1Tool = new ScopedFileTool(_fileTool, worker1Scope);
        var worker2Tool = new ScopedFileTool(_fileTool, worker2Scope);
        
        // Act: Both workers write to shared scope
        var sharedFile1 = Path.Combine(sharedScope, "FromWorker1.cs");
        var escapedPath1 = sharedFile1.Replace("\\", "\\\\");
        var args1 = $$"""{"action": "write", "path": "{{escapedPath1}}", "content": "// Worker 1 in shared"}""";
        var result1 = await worker1Tool.ExecuteAsync(args1);
        
        var sharedFile2 = Path.Combine(sharedScope, "FromWorker2.cs");
        var escapedPath2 = sharedFile2.Replace("\\", "\\\\");
        var args2 = $$"""{"action": "write", "path": "{{escapedPath2}}", "content": "// Worker 2 in shared"}""";
        var result2 = await worker2Tool.ExecuteAsync(args2);
        
        // Worker 1 blocked from outside, Worker 2 can access outside
        var worker1Blocked = Path.Combine(_outsidePath, "Blocked.cs");
        var escapedPath3 = worker1Blocked.Replace("\\", "\\\\");
        var args3 = $$"""{"action": "write", "path": "{{escapedPath3}}", "content": "// Should be blocked"}""";
        var result3 = await worker1Tool.ExecuteAsync(args3);

        // Assert
        Assert.IsTrue(result1.Contains("Written", StringComparison.OrdinalIgnoreCase) || result1.Contains("Success", StringComparison.OrdinalIgnoreCase), 
            "Worker 1 should write to shared");
        Assert.IsTrue(result2.Contains("Written", StringComparison.OrdinalIgnoreCase) || result2.Contains("Success", StringComparison.OrdinalIgnoreCase), 
            "Worker 2 should write to shared");
        Assert.Contains("Error", result3, "Worker 1 should be blocked from outside");
    }

    [TestMethod]
    public async Task ScopeViolation_AttemptLogged_VerifiableThroughException()
    {
        // Arrange: Setup scope violation scenario
        var scope = new WorkerScope(
            AllowedPaths: [$"{_scopePath}/*"],
            AllowedSymbols: []
        );
        var scopedTool = new ScopedFileTool(_fileTool, scope);
        
        var violations = new List<string>();
        var testFiles = new List<string>
        {
            Path.Combine(_outsidePath, "Violation1.cs"),
            Path.Combine(_outsidePath, "Violation2.cs"),
            Path.Combine(_testDir, "Violation3.cs")
        };

        // Act: Attempt multiple violations
        foreach (var filePath in testFiles)
        {
            var escapedPath = filePath.Replace("\\", "\\\\");
            var args = $$"""{"action": "write", "path": "{{escapedPath}}", "content": "// Violation"}""";
            var result = await scopedTool.ExecuteAsync(args);
            
            if (result.Contains("Error"))
            {
                violations.Add($"{filePath}: {result}");
            }
        }

        // Assert
        Assert.HasCount(3, violations, "All violations should be logged/recorded");
        foreach (var violation in violations)
        {
            Assert.Contains("Error", violation, "Each violation should return error");
            Assert.Contains("scope", violation, "Error should mention scope");
        }
        
        // Verify no files were created
        foreach (var filePath in testFiles)
        {
            Assert.IsFalse(File.Exists(filePath), $"File {filePath} should not be created");
        }
    }

    [TestMethod]
    public void IsPathAllowed_WildcardPatterns_WorkCorrectly()
    {
        // Arrange
        var scope = new WorkerScope(
            AllowedPaths: [$"{_scopePath}/*"],
            AllowedSymbols: []
        );
        var scopedTool = new ScopedFileTool(_fileTool, scope);
        
        var withinScope1 = Path.Combine(_scopePath, "File1.cs");
        var withinScope2 = Path.Combine(_scopePath, "SubDir", "File2.cs");
        var withinScope3 = Path.Combine(_scopePath, "Deep", "Nested", "File3.cs");
        var outsideScope1 = Path.Combine(_outsidePath, "File4.cs");
        var outsideScope2 = Path.Combine(_testDir, "File5.cs");

        // Assert
        Assert.IsTrue(scopedTool.IsPathAllowed(withinScope1), "Direct child should be allowed");
        Assert.IsTrue(scopedTool.IsPathAllowed(withinScope2), "Nested child should be allowed");
        Assert.IsTrue(scopedTool.IsPathAllowed(withinScope3), "Deep nested child should be allowed");
        Assert.IsFalse(scopedTool.IsPathAllowed(outsideScope1), "Outside path should be blocked");
        Assert.IsFalse(scopedTool.IsPathAllowed(outsideScope2), "Parent path should be blocked");
    }

    [TestMethod]
    public void IsPathAllowed_MultiplePatterns_MatchesAny()
    {
        // Arrange
        var anotherPath = Path.Combine(_testDir, "Another");
        Directory.CreateDirectory(anotherPath);
        
        var scope = new WorkerScope(
            AllowedPaths: [$"{_scopePath}/*", $"{anotherPath}/*"],
            AllowedSymbols: []
        );
        var scopedTool = new ScopedFileTool(_fileTool, scope);
        
        var path1 = Path.Combine(_scopePath, "File1.cs");
        var path2 = Path.Combine(anotherPath, "File2.cs");
        var path3 = Path.Combine(_outsidePath, "Blocked.cs");

        // Assert
        Assert.IsTrue(scopedTool.IsPathAllowed(path1), "Should match first pattern");
        Assert.IsTrue(scopedTool.IsPathAllowed(path2), "Should match second pattern");
        Assert.IsFalse(scopedTool.IsPathAllowed(path3), "Should not match any pattern");
    }

    [TestMethod]
    public async Task ScopedFileTool_ExactPathPattern_AllowsExactMatch()
    {
        // Arrange: Exact path without wildcard
        var specificFile = Path.Combine(_scopePath, "Specific.cs");
        var scope = new WorkerScope(
            AllowedPaths: [specificFile],
            AllowedSymbols: []
        );
        var scopedTool = new ScopedFileTool(_fileTool, scope);
        
        var allowedFile = specificFile;
        var escapedPath = allowedFile.Replace("\\", "\\\\");
        var args = $$"""{"action": "write", "path": "{{escapedPath}}", "content": "// Exact match"}""";
        
        var blockedFile = Path.Combine(_scopePath, "Other.cs");
        var escapedPath2 = blockedFile.Replace("\\", "\\\\");
        var args2 = $$"""{"action": "write", "path": "{{escapedPath2}}", "content": "// Should be blocked"}""";

        // Act
        var result1 = await scopedTool.ExecuteAsync(args);
        var result2 = await scopedTool.ExecuteAsync(args2);

        // Assert
        Assert.IsTrue(result1.Contains("Written", StringComparison.OrdinalIgnoreCase) || result1.Contains("Success", StringComparison.OrdinalIgnoreCase), 
            "Exact file should be allowed");
        Assert.Contains("Error", result2, "Other files should be blocked");
    }

    [TestMethod]
    public async Task ScopedFileTool_DirectoryTraversalAttack_Blocked()
    {
        // Arrange
        var scope = new WorkerScope(
            AllowedPaths: [$"{_scopePath}/*"],
            AllowedSymbols: []
        );
        var scopedTool = new ScopedFileTool(_fileTool, scope);
        
        // Try to escape scope using directory traversal
        var traversalPath = Path.Combine(_scopePath, "..", "outside", "Traversal.cs");
        var normalizedPath = Path.GetFullPath(traversalPath);
        var escapedPath = traversalPath.Replace("\\", "\\\\");
        var args = $$"""{"action": "write", "path": "{{escapedPath}}", "content": "// Traversal attack"}""";

        // Act
        var result = await scopedTool.ExecuteAsync(args);

        // Assert
        Assert.Contains("Error", result, "Traversal attack should be blocked");
        Assert.IsFalse(File.Exists(normalizedPath), "Traversal file should not be created");
    }

    [TestMethod]
    public async Task ScopedFileTool_EmptyScope_BlocksEverything()
    {
        // Arrange: Empty scope
        var scope = new WorkerScope(
            AllowedPaths: [],
            AllowedSymbols: []
        );
        var scopedTool = new ScopedFileTool(_fileTool, scope);
        
        var anyFile = Path.Combine(_scopePath, "AnyFile.cs");
        var escapedPath = anyFile.Replace("\\", "\\\\");
        var args = $$"""{"action": "write", "path": "{{escapedPath}}", "content": "// Should be blocked"}""";

        // Act
        var result = await scopedTool.ExecuteAsync(args);

        // Assert
        Assert.Contains("Error", result, "Empty scope should block everything");
        Assert.IsFalse(File.Exists(anyFile), "No files should be created");
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
