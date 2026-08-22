using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 任务 ce63f8c0：审批字符串防火墙回归测试。
/// 目标：危险命令确定性秒拒（引导 request_tool_approval）、安全命令确定性秒放
/// （不触发 LLM 隐式审计——用会记录调用数的 fake reviewer 验证）、灰区落回隐式审计。
/// </summary>
[TestClass]
public sealed class ToolApprovalCommandFirewallTests
{
    private sealed class CountingReviewer : IToolApprovalReviewer
    {
        public int Calls { get; private set; }

        public Task<ToolApprovalReviewResult> ReviewAsync(
            ToolApprovalTicketRequest request,
            ToolApprovalIdentity identity,
            ToolDescriptor descriptor,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ToolApprovalReviewResult
            {
                Decision = ToolApprovalDecision.Denied,
                DecisionReason = "counting reviewer denies",
            });
        }
    }

    private static async Task<ToolApprovalCheckResult> CheckAsync(
        CountingReviewer reviewer, string toolId, string command)
    {
        var service = new InMemoryToolApprovalService(reviewer);
        var descriptor = new TestShellTool().Descriptor with { ToolId = toolId };
        return await service.CheckAsync(new ToolApprovalExecutionRequest
        {
            WorkspaceId = "ws",
            SessionId = "s",
            AgentInstanceId = "a",
            UserId = "u",
            ToolId = toolId,
            ActualArgumentsJson = $$"""{"command": {{System.Text.Json.JsonSerializer.Serialize(command)}}}""",
        }, descriptor);
    }

    [TestMethod]
    public async Task Dangerous_Command_Is_Denied_Deterministically_Without_LlmReview()
    {
        var reviewer = new CountingReviewer();
        foreach (var command in new[]
                 {
                     "Remove-Item -Recurse -Force D:\\data",
                     "del /s /q temp",
                     "rm -rf /tmp/x",
                     "format D:",
                     "taskkill /PID 123 /F",
                     "git push --force origin master",
                     "git reset --hard HEAD~3",
                 })
        {
            var result = await CheckAsync(reviewer, "shell", command);
            Assert.IsFalse(result.IsApproved, command);
            Assert.AreEqual("CommandFirewall", result.ApprovalSource);
            StringAssert.Contains(result.Message, "request_tool_approval");
        }

        Assert.AreEqual(0, reviewer.Calls, "危险命令必须在 LLM 隐式审计之前确定性拒绝");
    }

    [TestMethod]
    public async Task Safe_Commands_Are_Allowed_Deterministically_Without_LlmReview()
    {
        var reviewer = new CountingReviewer();
        foreach (var command in new[]
                 {
                     "git -C \"E:\\repo\" status",
                     "git add -A",
                     "git commit -m \"feat: wiring\"",
                     "git push origin feature/x",
                     "dotnet build Source/PuddingRuntime",
                     "npm test",
                     "Get-ChildItem -Path . -Recurse",
                     "Select-String -Path a.ts -Pattern useChatState",
                     "cd \"E:\\repo\"; dotnet test",
                     "git diff --stat",
                 })
        {
            var result = await CheckAsync(reviewer, "shell", command);
            Assert.IsTrue(result.IsApproved, $"{command} → {result.Message}");
            // 既有 builtin allowlist（如 builtin_shell_get_childitem）与防火墙同为确定性秒放；
            // 本测试的核心断言是：不经 LLM 隐式审计（reviewer.Calls==0）。
            Assert.IsInstanceOfType(result.ApprovalSource, typeof(string));
            Assert.IsFalse(string.Equals(result.ApprovalSource, "ImplicitAudit", StringComparison.Ordinal),
                $"{command} → {result.ApprovalSource}");
        }

        Assert.AreEqual(0, reviewer.Calls, "安全命令必须零 LLM 审计直接放行");
    }

    [TestMethod]
    public async Task Commit_Message_Containing_Danger_Word_Does_Not_FalsePositive()
    {
        var reviewer = new CountingReviewer();
        var result = await CheckAsync(
            reviewer,
            "shell",
            "git commit -m \"remove del files and clean up\"");

        Assert.IsTrue(result.IsApproved, result.Message);
    }

    [TestMethod]
    public async Task Compound_Command_With_Any_Dangerous_Segment_Is_Denied()
    {
        var reviewer = new CountingReviewer();
        var result = await CheckAsync(
            reviewer,
            "shell",
            "git status && del important.txt");

        Assert.IsFalse(result.IsApproved);
        Assert.AreEqual(0, reviewer.Calls);
    }

    [TestMethod]
    public async Task Gray_Command_Falls_Back_To_Implicit_Review()
    {
        var reviewer = new CountingReviewer();
        var result = await CheckAsync(reviewer, "shell", "pwsh -NoProfile -File temp/apply.ps1");

        Assert.IsFalse(result.IsApproved);
        Assert.AreEqual(1, reviewer.Calls, "灰区命令应落回 LLM 隐式审计");
    }

    [TestMethod]
    public async Task NonCommand_Tools_Are_Not_Affected()
    {
        var reviewer = new CountingReviewer();
        var service = new InMemoryToolApprovalService(reviewer);
        var descriptor = new TestShellTool().Descriptor with
        {
            ToolId = "file_read",
            Safety = ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
        };
        var result = await service.CheckAsync(new ToolApprovalExecutionRequest
        {
            WorkspaceId = "ws",
            SessionId = "s",
            AgentInstanceId = "a",
            UserId = "u",
            ToolId = "file_read",
            ActualArgumentsJson = """{"path": "a.txt"}""",
        }, descriptor);

        // file_read 是只读工具：走 builtin 策略放行，不经防火墙也不经 LLM。
        Assert.IsTrue(result.IsApproved, result.Message);
        Assert.AreEqual(0, reviewer.Calls);
    }

    [Tool(
        id: "test_shell",
        name: "Test shell",
        description: "Test.",
        permission: ToolPermissionLevel.High,
        safety: ToolSafetyFlags.RequiresShell)]
    private sealed class TestShellTool : PuddingToolBase<object>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            object args, ToolExecutionContext context, CancellationToken ct)
            => Task.FromResult(ToolExecutionResult.Ok("ok"));
    }
}
