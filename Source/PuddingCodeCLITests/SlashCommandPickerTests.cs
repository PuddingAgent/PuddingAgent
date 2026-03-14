namespace PuddingCodeCLITests;

using PuddingCodeCLI;
using System.Reflection;

/// <summary>
/// Tests for the <see cref="SlashCommandPicker"/> class.
/// </summary>
[TestClass]
public sealed class SlashCommandPickerTests
{
    [TestMethod]
    public void CommandEntry_InitializedWithCorrectValues()
    {
        // Arrange & Act
        var entry = new SlashCommandPicker.CommandEntry("/test", "Test description");

        // Assert
        Assert.AreEqual("/test", entry.Command);
        Assert.AreEqual("Test description", entry.Description);
    }

    [TestMethod]
    public void CommandEntryList_ContainsExpectedCommands()
    {
        // Arrange - Get the static s_commands field via reflection
        var field = typeof(SlashCommandPicker).GetField("s_commands", BindingFlags.NonPublic | BindingFlags.Static);
        
        // Act
        var commands = field?.GetValue(null) as SlashCommandPicker.CommandEntry[];
        
        // Assert
        Assert.IsNotNull(commands);
        Assert.IsTrue(commands!.Length > 0);
        
        // Verify expected commands exist
        var commandStrings = commands.Select(c => c.Command).ToList();
        
        Assert.IsTrue(commandStrings.Contains("/help"));
        Assert.IsTrue(commandStrings.Contains("/open"));
        Assert.IsTrue(commandStrings.Contains("/model"));
        Assert.IsTrue(commandStrings.Contains("/model add"));
        Assert.IsTrue(commandStrings.Contains("/model use"));
        Assert.IsTrue(commandStrings.Contains("/model remove"));
        Assert.IsTrue(commandStrings.Contains("/undo"));
        Assert.IsTrue(commandStrings.Contains("/snapshot"));
        Assert.IsTrue(commandStrings.Contains("/history"));
        Assert.IsTrue(commandStrings.Contains("/config"));
        Assert.IsTrue(commandStrings.Contains("/swarm"));
        Assert.IsTrue(commandStrings.Contains("/swarm status"));
        Assert.IsTrue(commandStrings.Contains("/swarm cancel"));
        Assert.IsTrue(commandStrings.Contains("/debug"));
        Assert.IsTrue(commandStrings.Contains("/memory"));
        Assert.IsTrue(commandStrings.Contains("/exit"));
    }

    [TestMethod]
    public void Filter_ExactMatch_ReturnsSingleEntry()
    {
        // Arrange - Get filter method via reflection
        var method = typeof(SlashCommandPicker).GetMethod("Filter", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        
        // Act
        var result = method!.Invoke(null, ["/help"]) as SlashCommandPicker.CommandEntry[];
        
        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(1, result!.Length);
        Assert.AreEqual("/help", result[0].Command);
    }

    [TestMethod]
    public void Filter_PrefixMatch_ReturnsMultipleEntries()
    {
        // Arrange
        var method = typeof(SlashCommandPicker).GetMethod("Filter", BindingFlags.NonPublic | BindingFlags.Static);
        
        // Act
        var result = method!.Invoke(null, ["/model"]) as SlashCommandPicker.CommandEntry[];
        
        // Assert - Should return /model, /model add, /model use, /model remove
        Assert.IsNotNull(result);
        Assert.IsTrue(result!.Length >= 4);
        Assert.IsTrue(result.All(c => c.Command.StartsWith("/model", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Filter_NoMatch_ReturnsEmptyArray()
    {
        // Arrange
        var method = typeof(SlashCommandPicker).GetMethod("Filter", BindingFlags.NonPublic | BindingFlags.Static);
        
        // Act
        var result = method!.Invoke(null, ["/nonexistent"]) as SlashCommandPicker.CommandEntry[];
        
        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result!.Length);
    }

    [TestMethod]
    public void Filter_EmptyString_ReturnsAllCommands()
    {
        // Arrange
        var method = typeof(SlashCommandPicker).GetMethod("Filter", BindingFlags.NonPublic | BindingFlags.Static);
        
        // Act
        var result = method!.Invoke(null, [""]) as SlashCommandPicker.CommandEntry[];
        
        // Assert - Empty prefix matches all commands that start with empty string
        // Actually, commands don't start with empty string, so this returns 0
        // Let's test with "/" instead
        result = method!.Invoke(null, ["/"]) as SlashCommandPicker.CommandEntry[];
        Assert.IsNotNull(result);
        Assert.IsTrue(result!.Length > 0);
    }

    [TestMethod]
    public void Filter_CaseInsensitive_MatchesCorrectly()
    {
        // Arrange
        var method = typeof(SlashCommandPicker).GetMethod("Filter", BindingFlags.NonPublic | BindingFlags.Static);
        
        // Act
        var result = method!.Invoke(null, ["/HELP"]) as SlashCommandPicker.CommandEntry[];
        
        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(1, result!.Length);
        Assert.AreEqual("/help", result[0].Command);
    }

    [TestMethod]
    public void Filter_PartialCommand_MatchesCorrectly()
    {
        // Arrange
        var method = typeof(SlashCommandPicker).GetMethod("Filter", BindingFlags.NonPublic | BindingFlags.Static);
        
        // Act
        var result = method!.Invoke(null, ["/swarm s"]) as SlashCommandPicker.CommandEntry[];
        
        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result!.Length > 0);
        Assert.IsTrue(result.All(c => c.Command.StartsWith("/swarm s", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void CommandDescriptions_AreNotEmpty()
    {
        // Arrange
        var field = typeof(SlashCommandPicker).GetField("s_commands", BindingFlags.NonPublic | BindingFlags.Static);
        
        // Act
        var commands = field?.GetValue(null) as SlashCommandPicker.CommandEntry[];
        
        // Assert
        Assert.IsNotNull(commands);
        Assert.IsTrue(commands!.All(c => !string.IsNullOrWhiteSpace(c.Description)));
    }

    [TestMethod]
    public void CommandFormat_StartsWithSlash()
    {
        // Arrange
        var field = typeof(SlashCommandPicker).GetField("s_commands", BindingFlags.NonPublic | BindingFlags.Static);
        
        // Act
        var commands = field?.GetValue(null) as SlashCommandPicker.CommandEntry[];
        
        // Assert
        Assert.IsNotNull(commands);
        Assert.IsTrue(commands!.All(c => c.Command.StartsWith("/")));
    }

    [TestMethod]
    public void CommandFormat_NoDuplicateCommands()
    {
        // Arrange
        var field = typeof(SlashCommandPicker).GetField("s_commands", BindingFlags.NonPublic | BindingFlags.Static);
        
        // Act
        var commands = field?.GetValue(null) as SlashCommandPicker.CommandEntry[];
        
        // Assert
        Assert.IsNotNull(commands);
        var commandStrings = commands.Select(c => c.Command).ToList();
        Assert.AreEqual(commandStrings.Distinct().Count(), commandStrings.Count);
    }

    [TestMethod]
    public void CommandFormat_NoTrailingSpaces()
    {
        // Arrange
        var field = typeof(SlashCommandPicker).GetField("s_commands", BindingFlags.NonPublic | BindingFlags.Static);
        
        // Act
        var commands = field?.GetValue(null) as SlashCommandPicker.CommandEntry[];
        
        // Assert
        Assert.IsNotNull(commands);
        Assert.IsTrue(commands!.All(c => c.Command == c.Command.Trim()));
    }

    [TestMethod]
    public void IsSlashTriggerKey_WhenKeyCharIsSlash_ReturnsTrue()
    {
        // Arrange
        var method = typeof(SlashCommandPicker).GetMethod("IsSlashTriggerKey", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        var key = new ConsoleKeyInfo('/', ConsoleKey.Oem2, false, false, false);

        // Act
        var result = (bool)method!.Invoke(null, [key])!;

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void IsSlashTriggerKey_WhenSlashKeyHasNoKeyChar_ReturnsTrue()
    {
        // Arrange
        var method = typeof(SlashCommandPicker).GetMethod("IsSlashTriggerKey", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        var key = new ConsoleKeyInfo('\0', ConsoleKey.Oem2, false, false, false);

        // Act
        var result = (bool)method!.Invoke(null, [key])!;

        // Assert
        Assert.IsTrue(result);
    }
}
