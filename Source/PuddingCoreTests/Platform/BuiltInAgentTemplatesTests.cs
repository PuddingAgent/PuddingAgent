using PuddingCode.Platform;
using System.Text.RegularExpressions;

namespace PuddingCoreTests.Platform;

[TestClass]
public sealed partial class BuiltInAgentTemplatesTests
{
    [TestMethod]
    public void BuiltInTemplates_Use_Runtime_Tool_Ids_For_File_Tools()
    {
        var toolNames = BuiltInAgentTemplates.GetAll()
            .SelectMany(t => (t.Capability?.AllowedToolNames ?? [])
                .Concat(t.Capability?.DefaultToolNames ?? [])
                .Concat(t.Capability?.RequiresGrantToolNames ?? []))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.IsTrue(toolNames.Contains("file_read"));
        Assert.IsTrue(toolNames.Contains("list_dir"));
        Assert.IsTrue(toolNames.Contains("file_write"));
        Assert.IsTrue(toolNames.Contains("file_patch"));
        Assert.IsTrue(toolNames.Contains("apply_patch"));
        Assert.IsTrue(toolNames.Contains("terminal_start"));
        Assert.IsTrue(toolNames.Contains("terminal_wait"));
        Assert.IsTrue(toolNames.Contains("terminal_read"));
        Assert.IsTrue(toolNames.Contains("terminal_status"));
        Assert.IsTrue(toolNames.Contains("terminal_cancel"));
        Assert.IsTrue(toolNames.Contains("terminal_input"));
        Assert.IsTrue(toolNames.Contains("shell"));
        Assert.IsFalse(toolNames.Contains("terminal_execute"));
        Assert.IsFalse(toolNames.Contains("file.read"));
        Assert.IsFalse(toolNames.Contains("file.write"));
        Assert.IsFalse(toolNames.Contains("bash"));
        Assert.IsFalse(toolNames.Contains("python"));

        foreach (var toolName in toolNames)
            Assert.IsTrue(ValidToolIdRegex().IsMatch(toolName), toolName);
    }

    [TestMethod]
    public void TaskAndCodeTemplates_Prefer_Background_Terminal_Tools()
    {
        foreach (var template in new[] { BuiltInAgentTemplates.WorkspaceTaskAgent, BuiltInAgentTemplates.CodeAgent })
        {
            var tools = (template.Capability?.AllowedToolNames ?? [])
                .Concat(template.Capability?.DefaultToolNames ?? [])
                .Concat(template.Capability?.RequiresGrantToolNames ?? [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.IsTrue(tools.Contains("terminal_start"), template.TemplateId);
            Assert.IsTrue(tools.Contains("terminal_wait"), template.TemplateId);
            Assert.IsTrue(tools.Contains("terminal_read"), template.TemplateId);
            Assert.IsTrue(tools.Contains("terminal_cancel"), template.TemplateId);
            Assert.IsTrue(tools.Contains("shell"), template.TemplateId);
            Assert.IsFalse(tools.Contains("terminal_execute"), template.TemplateId);
            StringAssert.Contains(template.SystemPrompt ?? string.Empty, "terminal_start");
        }
    }

    [GeneratedRegex("^[a-zA-Z0-9_]+$")]
    private static partial Regex ValidToolIdRegex();
}
