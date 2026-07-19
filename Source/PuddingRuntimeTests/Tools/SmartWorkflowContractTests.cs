using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class SmartWorkflowContractTests
{
    private const string ValidDetailedReport = """
        SUMMARY:
        The requested work completed with a concrete outcome, a bounded scope, and enough detail for the parent Agent to use without repeating the delegated task.
        CHANGES:
        none — this contract-test response does not mutate files, but it explicitly records the inspected scope and intended behavior.
        EVIDENCE:
        Verified the delegated behavior using an exact command and a concrete source reference at E:\workspace\Source\Component.cs:42; the observed result matched the expected contract and produced a reproducible record.
        RISKS:
        The response is synthetic test evidence; live provider formatting and runtime integration remain covered by their dedicated execution tests.
        BLOCKERS:
        none — all required inputs and execution services were available to the test.
        """;

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
            ArgumentsJson = JsonSerializer.Serialize(new
            {
                task = "Plan the runtime hardening work. " + new string('x', 520),
            }),
            Context = new ToolExecutionContext
            {
                WorkspaceId = "workspace",
                SessionId = "session",
                AgentInstanceId = "agent",
            },
        });

        Assert.IsTrue(result.Success, result.Error);
        using var document = JsonDocument.Parse(recorder.ArgumentsJson!);
        Assert.AreEqual(150, document.RootElement.GetProperty("max_rounds").GetInt32());
        Assert.AreEqual(1800, document.RootElement.GetProperty("timeout_seconds").GetInt32());
        Assert.IsTrue(document.RootElement.GetProperty("allow_sub_delegation").GetBoolean());
        Assert.AreEqual(0, document.RootElement.GetProperty("depth").GetInt32());
        Assert.AreEqual(2, document.RootElement.GetProperty("max_depth").GetInt32());
        StringAssert.Contains(document.RootElement.GetProperty("tools").GetString(), "smart_explore");
        Assert.AreEqual(SubAgentExposure.MainAgentOnly, tool.Descriptor.SubAgentExposure);
    }

    [TestMethod]
    public async Task SmartExploreRequiresSelfContainedVerifiedEvidencePackage()
    {
        var recorder = new RecordingToolExecutionService();
        var services = new ServiceCollection()
            .AddSingleton<IPuddingToolExecutionService>(recorder)
            .BuildServiceProvider();
        var tool = new SmartExploreTool(
            services,
            NullLogger<SmartExploreTool>.Instance);

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "smart-explore",
            ArgumentsJson = """{"task":"Locate and explain the heartbeat implementation","max_results":8}""",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "workspace",
                SessionId = "session",
                AgentInstanceId = "agent",
            },
        });

        Assert.IsTrue(result.Success, result.Error);
        using var document = JsonDocument.Parse(recorder.ArgumentsJson!);
        Assert.AreEqual(1800, document.RootElement.GetProperty("timeout_seconds").GetInt32());
        Assert.IsFalse(document.RootElement.GetProperty("allow_sub_delegation").GetBoolean());
        Assert.AreEqual(SubAgentExposure.DelegatedSubAgent, tool.Descriptor.SubAgentExposure);
        var task = document.RootElement.GetProperty("task").GetString();
        Assert.IsNotNull(task);
        StringAssert.Contains(task, "without repeating file_search");
        StringAssert.Contains(task, "normalized absolute path");
        StringAssert.Contains(task, "RESPONSIBILITY:");
        StringAssert.Contains(task, "RELATIONSHIPS:");
        StringAssert.Contains(task, "DIRECT_ANSWER:");
        StringAssert.Contains(task, "Maximum verified artifacts: 8");
    }

    [TestMethod]
    public async Task OtherSmartWorkflowPromptsRequireRoleSpecificDetailedReports()
    {
        var recorder = new RecordingToolExecutionService();
        var services = new ServiceCollection()
            .AddSingleton<IPuddingToolExecutionService>(recorder)
            .BuildServiceProvider();
        var context = new ToolExecutionContext
        {
            WorkspaceId = "workspace",
            SessionId = "session",
            AgentInstanceId = "agent",
        };
        var longPlanTask = "Produce an implementation-ready architecture plan. " + new string('p', 520);
        (IPuddingTool Tool, string ArgumentsJson, string RoleMarker)[] cases =
        [
            (new SmartResearchTool(services, NullLogger<SmartResearchTool>.Instance),
                """{"task":"Research the runtime execution contract"}""", "CLAIMS_SUPPORTED:"),
            (new SmartPlanTool(services, NullLogger<SmartPlanTool>.Instance),
                JsonSerializer.Serialize(new { task = longPlanTask }), "DETAILED_TASKS:"),
            (new SmartReviewTool(services, NullLogger<SmartReviewTool>.Instance),
                """{"task":"Review the runtime execution contract"}""", "POSITIVE_OBSERVATIONS:"),
            (new SmartDevelopTool(services, NullLogger<SmartDevelopTool>.Instance),
                """{"task":"Implement the runtime execution contract"}""", "MANUAL_VERIFICATION:"),
            (new SmartTestTool(services, NullLogger<SmartTestTool>.Instance),
                """{"task":"Test the runtime execution contract"}""", "COVERAGE_GAPS:"),
            (new SmartDeployTool(services, NullLogger<SmartDeployTool>.Instance),
                """{"task":"Deploy the runtime execution contract"}""", "HEALTH_CHECKS:"),
        ];

        foreach (var testCase in cases)
        {
            var result = await testCase.Tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "smart-report-contract",
                ArgumentsJson = testCase.ArgumentsJson,
                Context = context,
            });

            Assert.IsTrue(result.Success, result.Error);
            using var document = JsonDocument.Parse(recorder.ArgumentsJson!);
            var task = document.RootElement.GetProperty("task").GetString();
            Assert.IsNotNull(task);
            StringAssert.Contains(task, "Return a complete, self-contained work report");
            StringAssert.Contains(task, "SUMMARY:");
            StringAssert.Contains(task, "CHANGES:");
            StringAssert.Contains(task, "EVIDENCE:");
            StringAssert.Contains(task, "RISKS:");
            StringAssert.Contains(task, "BLOCKERS:");
            StringAssert.Contains(task, testCase.RoleMarker);
            Assert.IsLessThanOrEqualTo(
                200,
                document.RootElement.GetProperty("max_rounds").GetInt32(),
                $"{testCase.Tool.Descriptor.ToolId} exceeds spawn_sub_agent's max_rounds contract.");
            Assert.AreEqual(
                1800,
                document.RootElement.GetProperty("timeout_seconds").GetInt32(),
                $"{testCase.Tool.Descriptor.ToolId} must use the shared 30-minute default.");
        }
    }

    [TestMethod]
    public void DelegatedSmartExposureRequiresExplicitPermissionAndRemainingDepth()
    {
        var descriptor = new SmartExploreTool(
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<SmartExploreTool>.Instance).Descriptor;
        var subAgentIdentity = new RuntimeExecutionIdentity
        {
            Kind = RuntimeExecutionKind.SubAgent,
            ConversationId = "conversation",
            RunId = "run",
        };

        string? Denial(bool allow, int depth, int maxDepth) =>
            PuddingToolExecutionService.GetSubAgentExposureDenial(
                descriptor,
                new ToolExecutionContext
                {
                    WorkspaceId = "workspace",
                    SessionId = "sub-session",
                    AgentInstanceId = "sub-session",
                    ExecutionIdentity = subAgentIdentity,
                    AllowSubDelegation = allow,
                    DelegationDepth = depth,
                    MaxDelegationDepth = maxDepth,
                });

        Assert.IsNull(Denial(allow: true, depth: 1, maxDepth: 2));
        StringAssert.Contains(Denial(allow: false, depth: 1, maxDepth: 2), "explicit sub-delegation");
        StringAssert.Contains(Denial(allow: true, depth: 2, maxDepth: 2), "depth=2");
    }

    [TestMethod]
    public async Task SmartWorkflowRejectsTerseCompletionWithoutDetailedReport()
    {
        var recorder = new RecordingToolExecutionService
        {
            ResponseOutput = "completed",
        };
        var services = new ServiceCollection()
            .AddSingleton<IPuddingToolExecutionService>(recorder)
            .BuildServiceProvider();
        var tool = new SmartResearchTool(
            services,
            NullLogger<SmartResearchTool>.Instance);

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "smart-terse-report",
            ArgumentsJson = """{"task":"Research the runtime execution contract"}""",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "workspace",
                SessionId = "session",
                AgentInstanceId = "agent",
            },
        });

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "incomplete work report");
        StringAssert.Contains(result.Error, "report is too short");
    }

    private sealed class RecordingToolExecutionService : IPuddingToolExecutionService
    {
        public string? ArgumentsJson { get; private set; }
        public string ResponseOutput { get; set; } = JsonSerializer.Serialize(new
        {
            schema = "pudding-subagent-result",
            version = 1,
            status = "completed",
            rawOutput = ValidDetailedReport,
        });

        public Task<ToolExecutionResult> ExecuteAsync(
            string toolId,
            string argumentsJson,
            ToolExecutionContext context,
            CapabilityPolicy? policy,
            CancellationToken ct = default)
        {
            Assert.AreEqual("spawn_sub_agent", toolId);
            ArgumentsJson = argumentsJson;
            return Task.FromResult(ToolExecutionResult.Ok(ResponseOutput));
        }
    }
}
