using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingCode.Runtime;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Tools;
using Moq;

namespace PuddingRuntimeTests.Tools;

public sealed partial class PuddingToolInfrastructureTests
{
    private sealed class DecisionFirewall(ToolApprovalDecision disposition) : IAgentFirewall
    {
        public Task<FirewallDecision> EvaluateAsync(FirewallContext context, CancellationToken ct) =>
            Task.FromResult(FirewallDecision.Deny("unchanged denial reason", FirewallGate.Authorization, disposition, "test_reason"));
    }
    [TestMethod]
    [DataRow(ToolApprovalDecision.NeedHuman, ToolResultStatuses.HumanDecisionRequired, 403)]
    [DataRow(ToolApprovalDecision.DeferredDependency, ToolResultStatuses.DependencyWait, 428)]
    [DataRow(ToolApprovalDecision.Denied, null, 403)]
    public async Task HumanDecision_ExecutorPreservesTypedDenialWithoutExecuting(ToolApprovalDecision disposition, string? status, int exitCode)
    {
        var executor = new PuddingToolExecutionService(new PuddingToolRegistry([new SampleHighTool()]),
            new SandboxExecutor(NullLogger<SandboxExecutor>.Instance), NullLogger<PuddingToolExecutionService>.Instance,
            firewall: new DecisionFirewall(disposition));
        var result = await executor.ExecuteAsync("sample_high", "{}", SampleContext(), new CapabilityPolicy());
        Assert.IsFalse(result.Success);
        Assert.AreEqual(status, result.Status); Assert.AreEqual(exitCode, result.ExitCode);
        Assert.AreEqual("unchanged denial reason", result.Error); Assert.AreEqual("", result.Output);

        var control = new Mock<IRuntimeControlService>();
        var facade = await new ToolInvocationService(executor, runtimeControl: control.Object).InvokeAsync(new ToolInvocationRequest
        {
            WorkspaceId = "workspace", SessionId = "session", AgentInstanceId = "agent",
            ToolCallId = "call", ToolName = "sample_high", ArgumentsJson = "{}",
        });
        Assert.IsFalse(facade.Success);
        Assert.AreEqual(status, facade.Status);
        Assert.AreEqual(exitCode, facade.ExitCode);
        Assert.AreEqual(result.Error, facade.Error);
        control.Verify(c => c.RecordError("session", RuntimeErrorKind.Tool, "sample_high", "unchanged denial reason"),
            disposition == ToolApprovalDecision.Denied ? Times.Once() : Times.Never());
    }
}
