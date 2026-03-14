namespace PuddingCodeTests.Core;

using PuddingCode.Abstractions;
using PuddingCode.Models;

[TestClass]
public sealed class ToolRegistryTests
{
    private ToolRegistry _registry = null!;

    [TestInitialize]
    public void Setup()
    {
        _registry = new ToolRegistry();
    }

    // ──── Registration Tests ────

    [TestMethod]
    public void Register_AddsToolSuccessfully()
    {
        // Arrange
        var tool = new MockTool("test_tool", "Test description");

        // Act
        _registry.Register(tool);

        // Assert
        var retrieved = _registry.GetTool("test_tool");
        Assert.IsNotNull(retrieved);
        Assert.AreEqual("test_tool", retrieved.Name);
        Assert.AreEqual("Test description", retrieved.Description);
    }

    [TestMethod]
    public void Register_ReplacesExistingToolWithSameName()
    {
        // Arrange
        var tool1 = new MockTool("test_tool", "First");
        var tool2 = new MockTool("test_tool", "Second");

        // Act
        _registry.Register(tool1);
        _registry.Register(tool2);

        // Assert
        var retrieved = _registry.GetTool("test_tool");
        Assert.IsNotNull(retrieved);
        Assert.AreEqual("Second", retrieved.Description);
    }

    [TestMethod]
    public void Register_CaseInsensitiveLookup()
    {
        // Arrange
        var tool = new MockTool("TestTool", "Test");

        // Act
        _registry.Register(tool);

        // Assert
        Assert.IsNotNull(_registry.GetTool("testtool"));
        Assert.IsNotNull(_registry.GetTool("TESTTOOL"));
        Assert.IsNotNull(_registry.GetTool("TestTool"));
    }

    // ──── Retrieval Tests ────

    [TestMethod]
    public void GetTool_ReturnsNullForNonExistentTool()
    {
        // Act
        var result = _registry.GetTool("non_existent");

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetAllTools_ReturnsAllRegisteredTools()
    {
        // Arrange
        _registry.Register(new MockTool("tool1", "First"));
        _registry.Register(new MockTool("tool2", "Second"));
        _registry.Register(new MockTool("tool3", "Third"));

        // Act
        var tools = _registry.GetAllTools();

        // Assert
        Assert.AreEqual(3, tools.Count);
        CollectionAssert.Contains(tools.Select(t => t.Name).ToList(), "tool1");
        CollectionAssert.Contains(tools.Select(t => t.Name).ToList(), "tool2");
        CollectionAssert.Contains(tools.Select(t => t.Name).ToList(), "tool3");
    }

    [TestMethod]
    public void GetAllTools_ReturnsReadOnlyList()
    {
        // Arrange
        _registry.Register(new MockTool("tool1", "First"));

        // Act
        var tools = _registry.GetAllTools();

        // Assert
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<ITool>)tools).Add(new MockTool("tool2", "Second")));
    }

    // ──── Execution Tests ────

    [TestMethod]
    public async Task ExecuteTool_ViaGetToolAndExecute()
    {
        // Arrange
        var tool = new MockTool("test_tool", "Test");
        _registry.Register(tool);

        // Act
        var retrieved = _registry.GetTool("test_tool");
        var result = await retrieved!.ExecuteAsync("{}", CancellationToken.None);

        // Assert
        Assert.AreEqual("Executed with: {}", result);
    }

    // ──── Edge Cases ────

    [TestMethod]
    public void Register_MultipleToolsWithDifferentNames()
    {
        // Arrange
        var tools = new[]
        {
            new MockTool("shell", "Shell tool"),
            new MockTool("file", "File tool"),
            new MockTool("git", "Git tool")
        };

        // Act
        foreach (var tool in tools)
        {
            _registry.Register(tool);
        }

        // Assert
        foreach (var tool in tools)
        {
            var retrieved = _registry.GetTool(tool.Name);
            Assert.IsNotNull(retrieved);
            Assert.AreEqual(tool.Description, retrieved.Description);
        }
    }

    [TestMethod]
    public void GetAllTools_EmptyRegistry_ReturnsEmptyList()
    {
        // Act
        var tools = _registry.GetAllTools();

        // Assert
        Assert.AreEqual(0, tools.Count);
    }
}

// Mock ITool implementation for testing
file sealed class MockTool(string name, string description) : ITool
{
    public string Name => name;
    public string Description => description;
    public ToolParameterSchema Parameters => new([], []);

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        => Task.FromResult($"Executed with: {argumentsJson}");
}
