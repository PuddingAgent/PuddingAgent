using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class ToolDiscoveryTests
{
    [TestMethod]
    public void SearchTools_Schema_Requires_Query()
    {
        var descriptor = ToolDescriptorFactory.Create(typeof(SearchToolsTool), typeof(SearchToolsArgs));

        CollectionAssert.Contains(descriptor.Parameters.Required.ToArray(), "query");
    }

    [TestMethod]
    public void ExposurePlanner_Defers_NonCore_Tools_And_Loads_Search_Matches()
    {
        var tools = new List<LlmToolDefinition>
        {
            Definition("search_tools"),
            Definition("goal_read"),
        };
        tools.AddRange(Enumerable.Range(0, 28).Select(index => Definition($"deferred_{index:00}")));

        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var initial = ToolExposurePlanner.CreatePlan(tools, loaded);

        Assert.IsTrue(initial.DeferredLoadingEnabled);
        CollectionAssert.AreEquivalent(
            new[] { "goal_read", "search_tools" },
            initial.VisibleTools.Select(tool => tool.Name).ToArray());
        Assert.AreEqual(28, initial.DeferredToolCount);

        var added = ToolExposurePlanner.RegisterSearchResult(
            "search_tools",
            success: true,
            """{"loaded_tool_ids":["deferred_17","unknown_tool"]}""",
            loaded,
            tools);
        var nextRound = ToolExposurePlanner.CreatePlan(tools, loaded);

        Assert.AreEqual(1, added);
        CollectionAssert.Contains(nextRound.VisibleTools.Select(tool => tool.Name).ToArray(), "deferred_17");
        CollectionAssert.DoesNotContain(nextRound.VisibleTools.Select(tool => tool.Name).ToArray(), "unknown_tool");
    }

    [TestMethod]
    public void ExposurePlanner_Keeps_Small_Tool_Sets_Unchanged()
    {
        var tools = Enumerable.Range(0, 8)
            .Select(index => Definition($"tool_{index}"))
            .ToList();

        var plan = ToolExposurePlanner.CreatePlan(tools);

        Assert.IsFalse(plan.DeferredLoadingEnabled);
        CollectionAssert.AreEquivalent(
            tools.Select(tool => tool.Name).ToArray(),
            plan.VisibleTools.Select(tool => tool.Name).ToArray());
    }

    [TestMethod]
    public async Task SearchTools_Returns_Only_Capability_Visible_Matches()
    {
        var services = new ServiceCollection();
        services.AddSingleton<SearchToolsTool>();
        services.AddSingleton<AllowedDatabaseTool>();
        services.AddSingleton<HiddenDatabaseTool>();
        services.AddSingleton<IPuddingToolRegistry>(sp => new PuddingToolRegistry(
            [
                sp.GetRequiredService<SearchToolsTool>(),
                sp.GetRequiredService<AllowedDatabaseTool>(),
                sp.GetRequiredService<HiddenDatabaseTool>(),
            ],
            new ToolPermissionPolicyService()));

        await using var provider = services.BuildServiceProvider();
        var tool = provider.GetRequiredService<SearchToolsTool>();
        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "search-call-1",
            ArgumentsJson = """{"query":"database","maxResults":10}""",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "workspace-1",
                SessionId = "session-1",
                AgentInstanceId = "agent-1",
                CapabilityPolicy = new CapabilityPolicy
                {
                    AllowedToolNames = ["search_tools", "allowed_database"],
                },
            },
        });

        Assert.IsTrue(result.Success, result.Error);
        using var document = JsonDocument.Parse(result.Output);
        var loadedIds = document.RootElement
            .GetProperty("loaded_tool_ids")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        CollectionAssert.AreEqual(new[] { "allowed_database" }, loadedIds);
        Assert.IsFalse(result.Output.Contains("hidden_database", StringComparison.Ordinal));
    }

    private static LlmToolDefinition Definition(string name) => new()
    {
        Name = name,
        Description = $"Description for {name}",
        Parameters = new ToolParameterSchema([], []),
    };

    [Tool(
        "allowed_database",
        "Allowed database query",
        "Read records from the project database.",
        ToolCategory.Query,
        ToolPermissionLevel.Low,
        ToolSafetyFlags.ReadOnly)]
    private sealed class AllowedDatabaseTool : PuddingToolBase<EmptyArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            EmptyArgs args,
            ToolExecutionContext context,
            CancellationToken ct) => Task.FromResult(ToolExecutionResult.Ok("ok"));
    }

    [Tool(
        "hidden_database",
        "Hidden database administration",
        "Administer hidden database records.",
        ToolCategory.Query,
        ToolPermissionLevel.Low,
        ToolSafetyFlags.ReadOnly)]
    private sealed class HiddenDatabaseTool : PuddingToolBase<EmptyArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            EmptyArgs args,
            ToolExecutionContext context,
            CancellationToken ct) => Task.FromResult(ToolExecutionResult.Ok("ok"));
    }

    private sealed record EmptyArgs;
}
