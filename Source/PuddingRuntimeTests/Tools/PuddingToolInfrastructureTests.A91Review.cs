// A91-0 正式回归测试（源自独立审计夹具 ADR-091-A91-0-Review-RegressionTests.cs.txt，artifact 已转为项目契约）。
// 依赖以下已实现契约：ToolApprovalReviewResult.ReasonCode、生产注册不提供 fake、
// 隐式审查的 typed 分类（DeferredDependency 不写 ImplicitDenied）。
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

public sealed partial class PuddingToolInfrastructureTests
{
    [TestMethod]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("1")]
    [DataRow("\"text\"")]
    public void A91Review_NonObjectJson_IsDeferredWithoutException(string raw)
    {
        var result = ToolApprovalReviewParser.Parse(raw);
        Assert.AreEqual(ToolApprovalDecision.DeferredDependency, result.Decision);
        Assert.IsFalse(result.RequiresHumanAuthorization);
        Assert.AreEqual(ToolApprovalWire.CodeNonObjectRoot, result.ReasonCode);
    }

    [TestMethod]
    [DataRow("{\"decision\":\"approved\"}")]
    [DataRow("{\"decision\":\"approved\",\"reason\":\"ok\",\"requiresHumanAuthorization\":true}")]
    [DataRow("{\"decision\":\"deferred_dependency\",\"reason\":\"unavailable\",\"requiresHumanAuthorization\":true}")]
    public void A91Review_IncompleteOrContradictorySchema_IsDeferred(string raw)
    {
        var result = ToolApprovalReviewParser.Parse(raw);
        Assert.AreEqual(ToolApprovalDecision.DeferredDependency, result.Decision);
        Assert.IsFalse(result.RequiresHumanAuthorization);
        Assert.IsNotNull(result.ReasonCode);
    }

    [TestMethod]
    public void A91Review_ReasonCode_IsPreservedInTypedResult()
    {
        var result = ToolApprovalReviewParser.Parse(
            "{\"decision\":\"deferred_dependency\",\"reason\":\"unavailable\",\"reasonCode\":\"approval_review_profile_not_configured\"}");

        Assert.AreEqual(ToolApprovalWire.CodeProfileNotConfigured, result.ReasonCode);
    }

    [TestMethod]
    public void A91Review_ProductionRegistration_CannotEnableFakeThroughConfig()
    {
        var services = new ServiceCollection();
        services.Configure<ToolApprovalRuntimeOptions>(options =>
        {
            options.Reviewer = "fake";
        });
        services.AddPuddingToolRegistry();
        using var provider = services.BuildServiceProvider();

        // 生产注册没有 fake 放行路径：旧配置（Reviewer=fake）必须被拒绝，
        // 假实现只能在测试组合里通过显式 DI 注册。
        var rejected = false;
        try
        {
            provider.GetRequiredService<IToolApprovalReviewer>();
        }
        catch (InvalidOperationException ex)
        {
            rejected = true;
            StringAssert.Contains(ex.Message, "not supported");
        }

        Assert.IsTrue(rejected, "production registration must reject Reviewer=fake.");
    }

    [TestMethod]
    public async Task A91Review_ImplicitDependencyWait_DoesNotSuggestHumanOrRecordDenied()
    {
        var telemetry = new RecordingTelemetrySink();
        var auditStore = new InMemoryToolApprovalAuditStore();
        var approval = new InMemoryToolApprovalService(
            new DeferredDependencyToolApprovalReviewer(),
            new InMemoryToolApprovalTicketStore(),
            new InMemoryToolApprovalAllowlistStore(),
            auditStore,
            telemetryMetricSink: telemetry);
        var identity = SampleApprovalIdentity();

        var check = await approval.CheckAsync(new ToolApprovalExecutionRequest
        {
            WorkspaceId = identity.WorkspaceId,
            SessionId = identity.SessionId,
            AgentInstanceId = identity.AgentInstanceId,
            UserId = identity.UserId,
            ToolId = "shell",
            ActualArgumentsJson = "{\"command\":\"python png2jpg.py --help\",\"shell\":\"powershell\",\"timeout_seconds\":10}",
        }, new SampleHighTool().Descriptor with { ToolId = "shell" });

        Assert.IsFalse(check.IsApproved);
        Assert.AreEqual(ToolApprovalDecision.DeferredDependency, check.Disposition);
        Assert.IsFalse(
            check.Message.Contains("request_tool_approval", StringComparison.OrdinalIgnoreCase)
            || check.Message.Contains("/authorize", StringComparison.OrdinalIgnoreCase),
            check.Message);

        var events = await auditStore.ListAsync();
        Assert.IsFalse(
            events.Any(e => e.EventType == ToolApprovalAuditEventType.ImplicitDenied),
            "Infrastructure waiting must not be recorded as an implicit rejection.");
        Assert.IsTrue(
            events.Any(e => e.EventType == ToolApprovalAuditEventType.TicketDeferredDependency),
            "Infrastructure waiting must be recorded with its own audit event.");
    }

    [TestMethod]
    public void A91Review_ApprovedJson_KeepsScopeAndAllowlist()
    {
        var result = ToolApprovalReviewParser.Parse("""
        {
          "decision": "approved",
          "reason": "Scoped to one exact read-only command.",
          "allowedScope": "once",
          "requiresHumanAuthorization": false,
          "allowlistProposals": [
            { "toolId": "shell", "command": "pwd", "argumentsJson": "{}", "reason": "read-only" }
          ]
        }
        """);

        Assert.AreEqual(ToolApprovalDecision.Approved, result.Decision);
        Assert.AreEqual(ToolApprovalScope.Once, result.AllowedScope);
        Assert.AreEqual(1, result.AllowlistProposals.Count);
    }

    [TestMethod]
    public void A91Review_NonApprovedDecisions_DropScopeAndAllowlist()
    {
        var result = ToolApprovalReviewParser.Parse("""
        {
          "decision": "denied",
          "reason": "outside scope",
          "allowedScope": "session",
          "allowedDurationMinutes": 10,
          "allowlistProposals": [
            { "toolId": "shell", "command": "rm -rf /", "argumentsJson": "{}", "reason": "nope" }
          ]
        }
        """);

        Assert.AreEqual(ToolApprovalDecision.Denied, result.Decision);
        Assert.IsNull(result.AllowedScope);
        Assert.IsNull(result.AllowedDuration);
        Assert.AreEqual(0, result.AllowlistProposals.Count);
    }
}
