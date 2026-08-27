using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// P0-6：授权 canonical definition/version 定向测试。
/// 覆盖：哈希稳定性与敏感性、授权成功路径捕获哈希/版本（ticket + allowlist 继承 + 版本单调）、
/// 漂移检测写审计事件且不阻断、旧格式 JSON 缺字段向后兼容读取。
/// </summary>
[TestClass]
public sealed class ToolDefinitionCanonicalTests
{
    [TestMethod]
    public void Compute_IsStable_AndSensitiveToDefinitionChanges()
    {
        var descriptor = CreateDescriptor();
        var first = ToolDefinitionHash.Compute(descriptor);

        // SHA-256 小写 hex
        Assert.AreEqual(64, first.Length);
        Assert.AreEqual(first.ToLowerInvariant(), first);

        // 同一定义重复计算稳定
        Assert.AreEqual(first, ToolDefinitionHash.Compute(descriptor));

        // Description 变化 → 哈希变化
        Assert.AreNotEqual(
            first,
            ToolDefinitionHash.Compute(descriptor with { Description = "Changed description." }));

        // 参数描述变化 → 哈希变化
        var changedParams = descriptor.Parameters with
        {
            Properties =
            [
                descriptor.Parameters.Properties[0],
                descriptor.Parameters.Properties[1] with { Description = "Changed param description." },
            ],
        };
        Assert.AreNotEqual(
            first,
            ToolDefinitionHash.Compute(descriptor with { Parameters = changedParams }));
    }

    [TestMethod]
    public void Compute_IsInvariantToPropertyOrder()
    {
        var descriptor = CreateDescriptor();
        var reordered = descriptor with
        {
            Parameters = new ToolParameterSchema(
                descriptor.Parameters.Properties.Reverse().ToArray(),
                descriptor.Parameters.Required,
                descriptor.Parameters.RawJsonSchema),
        };

        // Properties 顺序差异不影响规范哈希（对齐 ComputeToolSpecHash 规范化语义）
        Assert.AreEqual(
            ToolDefinitionHash.Compute(descriptor),
            ToolDefinitionHash.Compute(reordered));
    }

    [TestMethod]
    public async Task SubmitApproved_RecordsDefinitionFields_OnTicket_AndAllowlistRule_Inherits()
    {
        var ticketStore = new InMemoryToolApprovalTicketStore();
        var allowlistStore = new InMemoryToolApprovalAllowlistStore();
        var auditStore = new InMemoryToolApprovalAuditStore();
        var service = new InMemoryToolApprovalService(
            new FakeToolApprovalReviewer(),
            ticketStore,
            allowlistStore,
            auditStore);
        var descriptor = CreateDescriptor();
        var identity = SampleIdentity();
        var argsJson = """{"command":"pwd","shell":"auto","timeout_seconds":10}""";

        var first = await service.SubmitAsync(CreateRequest(descriptor.ToolId, argsJson), identity, descriptor);

        Assert.AreEqual(ToolApprovalDecision.Approved, first.Decision);
        var ticket = (await ticketStore.ListAsync()).Single(t => t.TicketId == first.TicketId);
        Assert.AreEqual(ToolDefinitionHash.Compute(descriptor), ticket.DefinitionHash);
        Assert.IsTrue(string.Equals(ticket.DefinitionHash, ticket.DefinitionHash.ToLowerInvariant(), StringComparison.Ordinal));
        Assert.AreEqual(1, ticket.DefinitionVersion);

        // 同一授权事件创建的 allowlist 规则继承 ticket 的哈希/版本（版本不二次递增）
        var rule = (await allowlistStore.ListAsync()).Single(r => r.ApprovalTicketId == ticket.TicketId);
        Assert.AreEqual(ticket.DefinitionHash, rule.DefinitionHash);
        Assert.AreEqual(ticket.DefinitionVersion, rule.DefinitionVersion);

        // 同一工具再次授权 → 版本单调 +1
        var second = await service.SubmitAsync(CreateRequest(descriptor.ToolId, argsJson), identity, descriptor);
        Assert.AreEqual(ToolApprovalDecision.Approved, second.Decision);
        var ticket2 = (await ticketStore.ListAsync()).Single(t => t.TicketId == second.TicketId);
        Assert.AreEqual(ToolDefinitionHash.Compute(descriptor), ticket2.DefinitionHash);
        Assert.AreEqual(2, ticket2.DefinitionVersion);
    }

    [TestMethod]
    public async Task Check_DetectsDefinitionDrift_EmitsAuditEvent_AndStillApproves()
    {
        var allowlistStore = new InMemoryToolApprovalAllowlistStore();
        var auditStore = new InMemoryToolApprovalAuditStore();
        // 注意：不用 pwd 等内置只读命令（builtin_shell_* 规则会先命中且无哈希，按设计跳过漂移审计）
        var argsJson = """{"command":"echo p06-drift-probe"}""";
        await allowlistStore.SaveAsync(new ToolApprovalAllowlistRule
        {
            RuleId = "tap_allow_p06stale",
            ToolId = "shell",
            Command = "echo p06-drift-probe",
            ArgumentsJson = argsJson,
            Source = ToolApprovalAllowlistRuleSource.AuditAgent,
            Status = ToolApprovalAllowlistRuleStatus.Enabled,
            Reason = "stale definition",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            DefinitionHash = new string('a', 64),
            DefinitionVersion = 3,
        });
        var service = new InMemoryToolApprovalService(
            new FakeToolApprovalReviewer(),
            new InMemoryToolApprovalTicketStore(),
            allowlistStore,
            auditStore);
        var descriptor = CreateDescriptor();
        var identity = SampleIdentity();

        var check = await service.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = identity.SessionId,
                AgentInstanceId = identity.AgentInstanceId,
                UserId = identity.UserId,
                ToolId = "shell",
                ActualArgumentsJson = argsJson,
            },
            descriptor);

        // v1 不硬阻断：仍按 allowlist 放行
        Assert.IsTrue(check.IsApproved, check.Message);
        Assert.AreEqual("AuditAgent", check.ApprovalSource);

        // 漂移被记录为审计事件
        var events = await auditStore.ListAsync();
        var drift = events.FirstOrDefault(e => e.EventType == ToolApprovalAuditEventType.DefinitionDriftDetected);
        Assert.IsNotNull(drift, "expected DefinitionDriftDetected audit event");
        Assert.AreEqual("shell", drift.ToolId);
        Assert.IsTrue(drift.Reason?.Contains("definition drift", StringComparison.Ordinal) == true);
    }

    [TestMethod]
    public async Task AllowlistStore_OldJsonWithoutDefinitionFields_ReadsAsDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "p06-canonical-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            var file = Path.Combine(paths.RuntimeRoot, "tool-approval", "allowlist.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, """
            [
              {
                "ruleId": "tap_allow_oldformat",
                "toolId": "shell",
                "command": "git status",
                "source": "Human",
                "status": "Enabled",
                "createdAtUtc": "2026-01-01T00:00:00+00:00"
              }
            ]
            """);

            // 旧格式文件缺 definitionHash/definitionVersion 字段 → 容忍为 null/0，不迁移不炸档
            var store = new FileToolApprovalAllowlistStore(paths, NullLogger<FileToolApprovalAllowlistStore>.Instance);
            var rule = await store.GetAsync("tap_allow_oldformat");

            Assert.IsNotNull(rule);
            Assert.AreEqual("shell", rule.ToolId);
            Assert.IsNull(rule.DefinitionHash);
            Assert.AreEqual(0, rule.DefinitionVersion);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static ToolDescriptor CreateDescriptor() => new()
    {
        ToolId = "shell",
        Name = "shell",
        Description = "Execute a shell command.",
        Parameters = new ToolParameterSchema(
            [
                new ToolParameter("command", "string", "The command to execute."),
                new ToolParameter("timeout_seconds", "integer", "Command timeout."),
            ],
            ["command"]),
    };

    private static ToolApprovalIdentity SampleIdentity() => new()
    {
        WorkspaceId = "workspace-1",
        SessionId = "session-p06",
        AgentInstanceId = "agent-p06",
        UserId = "user-p06",
    };

    private static ToolApprovalTicketRequest CreateRequest(string toolId, string argsJson) => new()
    {
        ToolId = toolId,
        CommandName = "run",
        Purpose = "P0-6 canonical definition test",
        Necessity = "verification",
        FactBasis = ["test"],
        RequestedArgumentsJson = argsJson,
        TargetResources = ["workspace-1"],
        AuthorizedArea = ["workspace-1"],
        OperationSteps =
        [
            new ToolApprovalOperationStep
            {
                StepNumber = 1,
                ToolId = toolId,
                Command = "pwd",
                RequestedArgumentsJson = argsJson,
                TargetObject = "workspace-1",
                Purpose = "show working directory",
                ExpectedEffect = "prints cwd",
                Reasonableness = "read-only",
                StopCondition = "command exits",
            },
        ],
        RequestAllowlistRule = true,
        AllowlistReason = "P0-6 test allowlist",
    };
}
