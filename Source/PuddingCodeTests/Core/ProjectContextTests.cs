namespace PuddingCodeTests.Core;

[TestClass]
public sealed class ProjectContextTests
{
    private string _tempPath = null!;
    private ProjectContext _context = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"pudding_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempPath);
        _context = new ProjectContext(_tempPath);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempPath))
        {
            try { Directory.Delete(_tempPath, true); } catch { }
        }
    }

    // ──── Constructor Tests ────

    [TestMethod]
    public void Constructor_SetsRootPathToFullPath()
    {
        // Assert
        Assert.AreEqual(Path.GetFullPath(_tempPath), _context.RootPath);
    }

    [TestMethod]
    public void Constructor_SetsNameToDirectoryName()
    {
        // Assert
        var expectedName = Path.GetFileName(_tempPath);
        Assert.AreEqual(expectedName, _context.Name);
    }

    [TestMethod]
    public void Constructor_FromRelativePath_ResolvesToFullPath()
    {
        // Arrange
        var originalDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _tempPath;
            var relativeContext = new ProjectContext(".");

            // Assert
            Assert.AreEqual(Path.GetFullPath("."), relativeContext.RootPath);
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
        }
    }

    // ──── Resolve Tests ────

    [TestMethod]
    public void Resolve_RelativePath_ReturnsAbsolutePathInProject()
    {
        // Act
        var result = _context.Resolve("src/Program.cs");

        // Assert
        var expected = Path.Combine(_tempPath, "src", "Program.cs");
        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void Resolve_AbsolutePath_ReturnsSamePath()
    {
        // Arrange
        var absolutePath = Path.Combine(_tempPath, "test.txt");

        // Act
        var result = _context.Resolve(absolutePath);

        // Assert
        Assert.AreEqual(absolutePath, result);
    }

    [TestMethod]
    public void Resolve_NormalizesPathSeparators()
    {
        // Act
        var result = _context.Resolve("src\\subdir/../file.txt");

        // Assert
        StringAssert.Contains(result, "file.txt");
    }

    // ──── Contains Tests ────

    [TestMethod]
    public void Contains_PathInsideProject_ReturnsTrue()
    {
        // Arrange
        var insidePath = Path.Combine(_tempPath, "src", "Program.cs");

        // Act
        var result = _context.Contains(insidePath);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Contains_PathOutsideProject_ReturnsFalse()
    {
        // Arrange
        var outsidePath = Path.Combine(Path.GetTempPath(), "outside.txt");

        // Act
        var result = _context.Contains(outsidePath);

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void Contains_CaseInsensitive()
    {
        // Arrange
        var mixedCasePath = _tempPath.ToUpper();

        // Act
        var result = _context.Contains(mixedCasePath);

        // Assert
        Assert.IsTrue(result);
    }

    // ──── GetRelativePath Tests ────

    [TestMethod]
    public void GetRelativePath_AbsolutePathInsideProject_ReturnsRelativePath()
    {
        // Arrange
        var absolutePath = Path.Combine(_tempPath, "src", "Program.cs");

        // Act
        var result = _context.GetRelativePath(absolutePath);

        // Assert
        Assert.AreEqual("src\\Program.cs", result);
    }

    [TestMethod]
    public void GetRelativePath_RootPath_ReturnsDot()
    {
        // Act
        var result = _context.GetRelativePath(_context.RootPath);

        // Assert
        Assert.AreEqual(".", result);
    }

    // ──── Static Factory Tests ────

    [TestMethod]
    public void FromCurrentDirectory_CreatesContextWithCurrentDir()
    {
        // Act
        var context = ProjectContext.FromCurrentDirectory();

        // Assert
        Assert.AreEqual(Environment.CurrentDirectory, context.RootPath);
    }

    // ──── Edge Cases ────

    [TestMethod]
    public void Resolve_EmptyRelativePath_ReturnsRootPath()
    {
        // Act
        var result = _context.Resolve("");

        // Assert
        Assert.AreEqual(_context.RootPath, result);
    }

    [TestMethod]
    public void Contains_PathWithTraversal_StaysWithinProject()
    {
        // Arrange
        var traversedPath = Path.Combine(_tempPath, "src", "..", "test.txt");

        // Act
        var result = _context.Contains(traversedPath);

        // Assert
        Assert.IsTrue(result);
    }
}
