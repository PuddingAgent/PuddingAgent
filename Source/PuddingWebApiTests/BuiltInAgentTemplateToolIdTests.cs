using PuddingCode.Platform;

namespace PuddingWebApiTests;

[TestClass]
public sealed class BuiltInAgentTemplateToolIdTests
{
    [TestMethod]
    public void PuddingAgentBuiltInTemplates_Use_Registered_Runtime_Tool_Ids()
    {
        Assert.AreSame(
            typeof(AgentTemplateDefinition).Assembly,
            typeof(BuiltInAgentTemplates).Assembly,
            "Built-in template policy must have one authoritative definition in PuddingCore.");
        var templates = BuiltInAgentTemplates.GetAll();

        var toolNames = templates
            .SelectMany(t => (t.Capability?.AllowedToolNames ?? [])
                .Concat(t.Capability?.DefaultToolNames ?? [])
                .Concat(t.Capability?.RequiresGrantToolNames ?? []))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        CollectionAssert.DoesNotContain(toolNames.ToArray(), "task_manager");
    }
}
