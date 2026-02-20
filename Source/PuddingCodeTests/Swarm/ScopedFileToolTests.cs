namespace PuddingCodeTests.Swarm;

using PuddingCode.Swarm;
using PuddingCode.Tools;

[TestClass]
public sealed class ScopedFileToolTests
{
    private ScopedFileTool _scopedTool = null!;
    private FileTool _innerTool = null!;
    private string _tempPath = null!;
    private string _scopePath = null!;

    [TestInitialize]
    public void Setup()
    {
        // 创建临时目录结构
        _tempPath = Path.Combine(Path.GetTempPath(), $"pudding_scoped_test_{Guid.NewGuid()}");
        _scopePath = Path.Combine(_tempPath, "scope");
        
        Directory.CreateDirectory(_scopePath);
        
        // 创建作用域内的文件
        Directory.CreateDirectory(Path.Combine(_scopePath, "Auth"));
        File.WriteAllText(Path.Combine(_scopePath, "Auth", "Login.cs"), "// Login");
        
        // 创建作用域外的文件
        Directory.CreateDirectory(Path.Combine(_tempPath, "Api"));
        File.WriteAllText(Path.Combine(_tempPath, "Api", "Controller.cs"), "// Controller");
        
        // 创建 FileTool 和 WorkerScope
        _innerTool = new FileTool();
        var scope = new WorkerScope(
            AllowedPaths: [$"{_scopePath}/*"],
            AllowedSymbols: []
        );
        
        _scopedTool = new ScopedFileTool(_innerTool, scope);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempPath))
        {
            try { Directory.Delete(_tempPath, true); } catch { }
        }
    }

    // ──── Path Within Scope Tests ────

    [TestMethod]
    public async Task ExecuteAsync_Write_WithinScope_Succeeds()
    {
        // Arrange
        var filePath = Path.Combine(_scopePath, "Auth", "NewFile.cs");
        var escapedPath = filePath.Replace("\\", "\\\\");
        var args = $$"""{"action": "write", "path": "{{escapedPath}}", "content": "// New content"}""";

        // Act
        var result = await _scopedTool.ExecuteAsync(args);

        // Assert
        Assert.IsTrue(result.Contains("Written"));
        Assert.IsTrue(File.Exists(filePath));
    }

    [TestMethod]
    public async Task ExecuteAsync_List_WithinScope_Succeeds()
    {
        // Arrange
        var escapedPath = _scopePath.Replace("\\", "\\\\");
        var args = $$"""{"action": "list", "path": "{{escapedPath}}"}""";

        // Act
        var result = await _scopedTool.ExecuteAsync(args);

        // Assert - should contain the Auth subdirectory (full path)
        Assert.IsTrue(result.Contains(_scopePath, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ExecuteAsync_Read_WithinScope_Succeeds()
    {
        // Arrange
        var filePath = Path.Combine(_scopePath, "Auth", "Login.cs");
        var escapedPath = filePath.Replace("\\", "\\\\");
        var args = $$"""{"action": "read", "path": "{{escapedPath}}"}""";

        // Act
        var result = await _scopedTool.ExecuteAsync(args);

        // Assert
        Assert.AreEqual("// Login", result.Trim());
    }

    // ──── Path Outside Scope Tests ────

    [TestMethod]
    public async Task ExecuteAsync_Write_OutsideScope_ReturnsError()
    {
        // Arrange
        var filePath = Path.Combine(_tempPath, "Api", "NewController.cs");
        var escapedPath = filePath.Replace("\\", "\\\\");
        var args = $$"""{"action": "write", "path": "{{escapedPath}}", "content": "// Blocked"}""";

        // Act
        var result = await _scopedTool.ExecuteAsync(args);

        // Assert
        Assert.IsTrue(result.Contains("Error: your scope is"));
        Assert.IsTrue(result.Contains("cannot modify"));
        Assert.IsFalse(File.Exists(filePath));
    }

    [TestMethod]
    public async Task ExecuteAsync_List_OutsideScope_ReturnsError()
    {
        // Arrange
        var escapedPath = _tempPath.Replace("\\", "\\\\");
        var args = $$"""{"action": "list", "path": "{{escapedPath}}"}""";

        // Act
        var result = await _scopedTool.ExecuteAsync(args);

        // Assert
        Assert.IsTrue(result.Contains("Error: your scope is"));
        Assert.IsTrue(result.Contains("cannot modify"));
    }

    [TestMethod]
    public async Task ExecuteAsync_Write_OutsideScope_ErrorMessageIncludesScopeInfo()
    {
        // Arrange
        var filePath = Path.Combine(_tempPath, "Api", "Blocked.cs");
        var escapedPath = filePath.Replace("\\", "\\\\");
        var args = $$"""{"action": "write", "path": "{{escapedPath}}", "content": "blocked"}""";

        // Act
        var result = await _scopedTool.ExecuteAsync(args);

        // Assert
        Assert.IsTrue(result.Contains(_scopePath));
    }

    // ──── IsPathAllowed Tests ────

    [TestMethod]
    public void IsPathAllowed_PathWithinScope_ReturnsTrue()
    {
        // Arrange
        var path = Path.Combine(_scopePath, "Auth", "Test.cs");

        // Act
        var result = _scopedTool.IsPathAllowed(path);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void IsPathAllowed_PathOutsideScope_ReturnsFalse()
    {
        // Arrange
        var path = Path.Combine(_tempPath, "Api", "Test.cs");

        // Act
        var result = _scopedTool.IsPathAllowed(path);

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void IsPathAllowed_EmptyPath_ReturnsFalse()
    {
        // Act
        var result = _scopedTool.IsPathAllowed("");

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void IsPathAllowed_NullPath_ReturnsFalse()
    {
        // Act
        var result = _scopedTool.IsPathAllowed(null!);

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void IsPathAllowed_RelativePath_WithinScope_ReturnsTrue()
    {
        // Arrange
        var currentDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _scopePath;
            var relativePath = "Auth/Test.cs";

            // Act
            var result = _scopedTool.IsPathAllowed(relativePath);

            // Assert
            Assert.IsTrue(result);
        }
        finally
        {
            Environment.CurrentDirectory = currentDir;
        }
    }

    // ──── Wildcard Pattern Tests ────

    [TestMethod]
    public void IsPathAllowed_WildcardPattern_MatchesSubdirectories()
    {
        // Arrange
        var scope = new WorkerScope(
            AllowedPaths: [$"{_scopePath}/*"],
            AllowedSymbols: []
        );
        var tool = new ScopedFileTool(_innerTool, scope);
        var deepPath = Path.Combine(_scopePath, "Auth", "Services", "Deep.cs");

        // Act
        var result = tool.IsPathAllowed(deepPath);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void IsPathAllowed_WildcardPattern_DoesNotMatchOutside()
    {
        // Arrange
        var scope = new WorkerScope(
            AllowedPaths: [$"{_scopePath}/*"],
            AllowedSymbols: []
        );
        var tool = new ScopedFileTool(_innerTool, scope);
        var outsidePath = Path.Combine(_tempPath, "Outside.cs");

        // Act
        var result = tool.IsPathAllowed(outsidePath);

        // Assert
        Assert.IsFalse(result);
    }

    // ──── Multiple Allowed Paths Tests ────

    [TestMethod]
    public void IsPathAllowed_MultipleAllowedPaths_MatchesAny()
    {
        // Arrange
        var anotherPath = Path.Combine(_tempPath, "Another");
        Directory.CreateDirectory(anotherPath);
        
        var scope = new WorkerScope(
            AllowedPaths: [$"{_scopePath}/*", $"{anotherPath}/*"],
            AllowedSymbols: []
        );
        var tool = new ScopedFileTool(_innerTool, scope);
        
        var path1 = Path.Combine(_scopePath, "File1.cs");
        var path2 = Path.Combine(anotherPath, "File2.cs");
        var path3 = Path.Combine(_tempPath, "Blocked.cs");

        // Act & Assert
        Assert.IsTrue(tool.IsPathAllowed(path1));
        Assert.IsTrue(tool.IsPathAllowed(path2));
        Assert.IsFalse(tool.IsPathAllowed(path3));
    }

    // ──── Edge Cases ────

    [TestMethod]
    public async Task ExecuteAsync_InvalidAction_ReturnsError()
    {
        // Arrange
        var filePath = Path.Combine(_scopePath, "Test.cs");
        var escapedPath = filePath.Replace("\\", "\\\\");
        var args = $$"""{"action": "invalid", "path": "{{escapedPath}}"}""";

        // Act
        var result = await _scopedTool.ExecuteAsync(args);

        // Assert
        Assert.IsTrue(result.Contains("Unknown action"));
    }

    [TestMethod]
    public async Task ExecuteAsync_MissingPath_ReturnsError()
    {
        // Arrange
        var args = """{"action": "read"}""";

        // Act
        var result = await _scopedTool.ExecuteAsync(args);

        // Assert
        Assert.IsTrue(result.Contains("path is required"));
    }

    [TestMethod]
    public void Constructor_PreservesInnerToolProperties()
    {
        // Act & Assert
        Assert.AreEqual("file", _scopedTool.Name);
        Assert.IsTrue(_scopedTool.Description.Contains("file"));
        Assert.IsNotNull(_scopedTool.Parameters);
    }

    [TestMethod]
    public void ExecuteAsync_Read_DoesNotCheckScope()
    {
        // 根据实现，read 操作不进行作用域检查
        // 这个测试验证 read 操作可以读取作用域外的文件
        // 注意：如果需要限制 read，可以在 ScopedFileTool 中添加检查
        
        var filePath = Path.Combine(_tempPath, "Api", "Controller.cs");
        var escapedPath = filePath.Replace("\\", "\\\\");
        var args = $$"""{"action": "read", "path": "{{escapedPath}}"}""";

        // Act
        var result = _scopedTool.ExecuteAsync(args).Result;

        // Assert - read 操作应该成功（因为没有 scope 检查）
        Assert.IsTrue(result.Contains("// Controller"));
    }
}
