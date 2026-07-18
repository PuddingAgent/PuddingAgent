using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class SmartWorkflowContractTests
{
    [TestMethod]
    public void AllSmartWorkflowArgumentsUseTaskAsPrimaryInstruction()
    {
        Type[] argumentTypes =
        [
            typeof(SmartExploreArgs),
            typeof(SmartResearchArgs),
            typeof(SmartPlanArgs),
            typeof(SmartReviewArgs),
            typeof(SmartDevelopArgs),
            typeof(SmartDeployArgs),
            typeof(SmartTestArgs),
        ];

        foreach (var argumentType in argumentTypes)
        {
            Assert.IsNotNull(
                argumentType.GetProperty(nameof(SmartWorkflowArgs.Task)),
                $"{argumentType.Name} must expose task.");
            Assert.IsNull(argumentType.GetProperty("What"));
            Assert.IsNull(argumentType.GetProperty("Goal"));
            Assert.IsNull(argumentType.GetProperty("Question"));
            Assert.IsNull(argumentType.GetProperty("Target"));
        }
    }

    [TestMethod]
    public async Task SmartPlanUsesRoleExecutionBudget()
    {
        var recorder = new RecordingToolExecutionService();
        var services = new ServiceCollection()
            .AddSingleton<IPuddingToolExecutionService>(recorder)
            .BuildServiceProvider();
        var tool = new SmartPlanTool(services, NullLogger<SmartPlanTool>.Instance);

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "smart-plan",
            ArgumentsJson = """{"task":"Plan the runtime hardening work"}""",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "workspace",
                SessionId = "session",
                AgentInstanceId = "agent",
            },
        });

        Assert.IsTrue(result.Success, result.Error);
        using var document = JsonDocument.Parse(recorder.ArgumentsJson!);
        Assert.AreEqual(20, document.RootElement.GetProperty("max_rounds").GetInt32());
        Assert.AreEqual(240, document.RootElement.GetProperty("timeout_seconds").GetInt32());
    }

    private sealed class RecordingToolExecutionService : IPuddingToolExecutionService
    {
        public string? ArgumentsJson { get; private set; }

        public Task<ToolExecutionResult> ExecuteAsync(
            string toolId,
            string argumentsJson,
            ToolExecutionContext context,
            CapabilityPolicy? policy,
            CancellationToken ct = default)
        {
            Assert.AreEqual("spawn_sub_agent", toolId);
            ArgumentsJson = argumentsJson;
            return Task.FromResult(ToolExecutionResult.Ok("completed"));
        }
    }
}
