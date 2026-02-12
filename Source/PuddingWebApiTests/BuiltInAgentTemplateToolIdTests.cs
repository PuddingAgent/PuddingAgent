using System.Reflection;
using PuddingCode.Platform;

namespace PuddingWebApiTests;

[TestClass]
public sealed class BuiltInAgentTemplateToolIdTests
{
    [TestMethod]
    public void PuddingAgentBuiltInTemplates_Use_Registered_Runtime_Tool_Ids()
    {
        var templateType = typeof(Program).Assembly.GetType("PuddingCode.Platform.BuiltInAgentTemplates", throwOnError: true)!;
        var getAll = templateType.GetMethod("GetAll", BindingFlags.Public | BindingFlags.Static)!;
        var templates = ((IEnumerable<AgentTemplateDefinition>)getAll.Invoke(null, null)!).ToArray();

        var toolNames = templates
            .SelectMany(t => (t.Capability?.AllowedToolNames ?? [])
                .Concat(t.Capability?.DefaultToolNames ?? [])
                .Concat(t.Capability?.RequiresGrantToolNames ?? []))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        CollectionAssert.Contains(toolNames.ToArray(), "manage_tasks");
        CollectionAssert.DoesNotContain(toolNames.ToArray(), "task_manager");
    }
}
