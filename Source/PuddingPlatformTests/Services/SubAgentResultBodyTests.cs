using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 子代理结果消息正文（A 期）的格式契约。
/// <para>
/// 背景事故：子代理交付 9 个文件、22/22 测试通过，气泡里却只有一行
/// <c>shell: exit code 1</c> —— 失败分支把 reply 整条丢了，正文位置被退出码占据。
/// 这组用例锁死三件事：<b>标题在前、reply 永不丢、错误只作附注且缺失项显式写出</b>。
/// </para>
/// </summary>
[TestClass]
public sealed class SubAgentResultBodyTests
{
    private const string TemplateId = "workspace-task-agent";
    private const string SubSessionId = "parent-session-sub-e0b08674";
    private const string DeliveryReply = "SUMMARY: 9 files changed +123/-32；新测试 22/22 通过。";
    private const string ShellError = "shell: exit code 1";

    private static string BuildBody(
        string status,
        string? reply,
        string? error,
        int toolFailureCount = 0,
        string? toolFailureSummary = null,
        int toolCallCount = 37,
        long? elapsedMs = 83_400,
        string? displayName = "delivery-agent")
        => SubAgentManager.BuildSubAgentResultBody(
            displayName,
            TemplateId,
            SubSessionId,
            status,
            reply,
            error,
            toolFailureCount,
            toolFailureSummary,
            toolCallCount,
            elapsedMs);

    // ── 判据 1：失败态不得吞掉 reply，且必须附 tool_failure 诊断 ──────────────
    [TestMethod]
    public void BuildSubAgentResultBody_Failed_KeepsReplyAndAppendsToolFailureDiagnostic()
    {
        var body = BuildBody(
            "failed",
            DeliveryReply,
            ShellError,
            toolFailureCount: 1,
            toolFailureSummary: ShellError);

        StringAssert.Contains(body, DeliveryReply);
        StringAssert.Contains(body, "错误诊断（tool_failure）");
    }

    // ── 判据 2：没有 reply 时给固定句，不留空白 ──────────────────────────────
    [TestMethod]
    public void BuildSubAgentResultBody_FailedWithoutReply_WritesExplicitNoDeliverableSentence()
    {
        var body = BuildBody("failed", reply: "   ", error: ShellError, toolFailureCount: 1);

        StringAssert.Contains(body, "（没有产生可读的交付文本）");
    }

    // ── 判据 3：成功且无错误 → 不出现无意义的错误段 ──────────────────────────
    [TestMethod]
    public void BuildSubAgentResultBody_CompletedWithoutError_OmitsErrorSection()
    {
        var body = BuildBody("completed", DeliveryReply, error: null, toolFailureCount: 0);

        Assert.IsFalse(body.Contains("错误诊断", StringComparison.Ordinal), body);
        Assert.IsFalse(body.Contains("---", StringComparison.Ordinal), body);
        StringAssert.Contains(body, DeliveryReply);
    }

    // ── 判据 4：可续跑态保留正文，状态词区分「等裁决」 ──────────────────────
    [TestMethod]
    public void BuildSubAgentResultBody_Paused_KeepsReplyAndUsesAwaitingDecisionWord()
    {
        var body = BuildBody("paused", DeliveryReply, error: null, toolFailureCount: 0);

        StringAssert.Contains(body, "已暂停待裁决");
        StringAssert.Contains(body, DeliveryReply);
    }

    // ── 判据 5：名字解析（人读名优先，否则确定性回退） ──────────────────────
    [TestMethod]
    public void ResolveDisplayNameForResult_PrefersDisplayName_AndFallsBackToTemplateAndShortId()
    {
        Assert.AreEqual(
            "paused-tests",
            SubAgentManager.ResolveDisplayNameForResult("paused-tests", TemplateId, SubSessionId));

        var fallback = SubAgentManager.ResolveDisplayNameForResult(
            null, TemplateId, "parent-session-sub-e0b08674");

        Assert.AreEqual("workspace-task-agent-e0b08674", fallback);
        StringAssert.Contains(BuildBody("failed", DeliveryReply, null, displayName: null), fallback);
    }

    // ── 判据 6：正文永不等于错误串（第一个非空行必须是标题） ────────────────
    [TestMethod]
    public void BuildSubAgentResultBody_Failed_FirstNonEmptyLineIsHeaderAndReplyPrecedesError()
    {
        var body = BuildBody(
            "failed",
            reply: "X",
            error: ShellError,
            toolFailureCount: 1,
            toolFailureSummary: ShellError);

        var firstNonEmptyLine = body
            .Split('\n')
            .First(line => !string.IsNullOrWhiteSpace(line));

        StringAssert.Contains(firstNonEmptyLine, "子代理");
        var replyIndex = body.IndexOf("X", StringComparison.Ordinal);
        var errorIndex = body.IndexOf(ShellError, StringComparison.Ordinal);
        Assert.IsTrue(replyIndex >= 0, body);
        Assert.IsTrue(errorIndex > replyIndex, $"reply@{replyIndex} error@{errorIndex}\n{body}");
    }

    // ── 判据 7：缺失项显式写出，堆栈口径不得留空或伪造 ──────────────────────
    [TestMethod]
    public void BuildSubAgentResultBody_MissingDiagnostics_WritesExplicitMarkers()
    {
        var toolFailureBody = BuildBody(
            "failed",
            reply: DeliveryReply,
            error: null,
            toolFailureCount: 2,
            toolFailureSummary: null);

        StringAssert.Contains(toolFailureBody, "未记录失败摘要");
        StringAssert.Contains(toolFailureBody, "未记录错误文本");
        StringAssert.Contains(toolFailureBody, "无堆栈");
        StringAssert.Contains(toolFailureBody, "错误诊断（tool_failure）");

        // 只有错误串、没有工具失败计数 → category 只能是 unknown，且仍然写明堆栈口径。
        var unknownBody = BuildBody("failed", reply: DeliveryReply, error: ShellError, toolFailureCount: 0);

        StringAssert.Contains(unknownBody, "错误诊断（unknown）");
        StringAssert.Contains(unknownBody, "无堆栈");
        StringAssert.Contains(unknownBody, ShellError);
        Assert.IsFalse(
            unknownBody.Contains("runtime_exception", StringComparison.Ordinal),
            "没有异常对象时不得凭猜写 runtime_exception");
    }

    // ── 判据 8：标题带工具计数（总调用数来自真实计数，不是编造值） ──────────
    [TestMethod]
    public void BuildSubAgentResultBody_Header_CarriesToolCounts()
    {
        var body = BuildBody(
            "failed",
            DeliveryReply,
            ShellError,
            toolFailureCount: 2,
            toolFailureSummary: ShellError,
            toolCallCount: 37);

        StringAssert.Contains(body, "工具 37 次（失败 2 次）");
    }

    // ── 计数口径：总调用数 = TurnSteps 中带工具名的轮次数 ───────────────────
    [TestMethod]
    public void CountToolCalls_CountsStepsCarryingToolName()
    {
        var steps = new TurnStepDto[]
        {
            new() { Round = 1, Status = "CONTINUE", ToolName = "file_read" },
            new() { Round = 2, Status = "CONTINUE" },
            new() { Round = 3, Status = "CONTINUE", ToolName = "shell" },
        };

        Assert.AreEqual(2, SubAgentManager.CountToolCalls(steps));
        Assert.AreEqual(0, SubAgentManager.CountToolCalls(null));
    }

    // ── 用时口径：无任何耗时记录时返回 null（正文写「未记录」） ─────────────
    [TestMethod]
    public void SumStepDurationMs_SumsReportedDurations_AndReturnsNullWhenUnmeasured()
    {
        var steps = new TurnStepDto[]
        {
            new() { Round = 1, Status = "CONTINUE", DurationMs = 1_500 },
            new() { Round = 2, Status = "DONE", DurationMs = 32_000 },
        };

        Assert.AreEqual(33_500L, SubAgentManager.SumStepDurationMs(steps));
        Assert.IsNull(SubAgentManager.SumStepDurationMs([new TurnStepDto { Round = 1, Status = "CONTINUE" }]));

        StringAssert.Contains(BuildBody("completed", DeliveryReply, null, elapsedMs: null), "用时 未记录");
    }

    // ── 全文金样本：锁死三段式版式（改格式必须先改这里，不能静默漂移） ────
    [TestMethod]
    public void BuildSubAgentResultBody_FailedWithMissingDiagnostics_MatchesExactLayout()
    {
        var expected = string.Join(
            "\n",
            "**子代理 delivery-agent 失败** · 模板 workspace-task-agent · 工具 37 次（失败 2 次） · 用时 1m23s",
            string.Empty,
            DeliveryReply,
            string.Empty,
            "---",
            "⚠ 错误诊断（tool_failure）：工具调用失败 2 次，最近一次：未记录失败摘要；最后一次错误：未记录错误文本（无堆栈，工具退出码路径）");

        var body = BuildBody(
            "failed",
            reply: DeliveryReply,
            error: null,
            toolFailureCount: 2,
            toolFailureSummary: null,
            toolCallCount: 37,
            elapsedMs: 83_400);

        Assert.AreEqual(expected, body);
    }

    // ── wiring：信封装配必须真的用三段式正文（只测纯函数证明不了调用方用了它） ──
    [TestMethod]
    public void BuildSubAgentResultEnvelope_FailedRun_RendersHeaderThenReplyThenDiagnostic()
    {
        var baseEnvelope = new MessageEnvelope
        {
            MessageId = "result-1",
            From = new MessageAddress
            {
                Kind = MessageEndpointKinds.Agent,
                Id = SubSessionId,
                WorkspaceId = "default",
                DisplayName = "Sub Agent",
            },
            To =
            [
                new MessageAddress
                {
                    Kind = MessageEndpointKinds.Agent,
                    Id = "agent-parent",
                    WorkspaceId = "default",
                },
            ],
            RoomId = "default",
            ConversationId = "parent-session",
            Audience = MessageAudiences.Direct,
            Visibility = MessageVisibilities.System,
            ContentType = "application/vnd.pudding.agent-context-envelope+json",
            Content = string.Empty,
        };

        var envelope = SubAgentManager.BuildSubAgentResultEnvelope(
            baseEnvelope,
            SubSessionId,
            displayName: "delivery-agent",
            templateId: TemplateId,
            status: "failed",
            task: "deliver the slice",
            reply: DeliveryReply,
            error: ShellError,
            toolFailureCount: 1,
            toolOutputTruncatedCount: 0,
            toolOutputChars: 0,
            toolFailureSummary: ShellError,
            toolCallCount: 37,
            elapsedMs: 83_400);

        var text = envelope.Context.Text;
        StringAssert.Contains(text, "**子代理 delivery-agent 失败**");
        StringAssert.Contains(text, DeliveryReply);
        StringAssert.Contains(text, "错误诊断（tool_failure）");
        Assert.IsTrue(
            text.IndexOf(DeliveryReply, StringComparison.Ordinal)
                < text.IndexOf(ShellError, StringComparison.Ordinal),
            text);
        // 正文第一行必须是标题，错误串不得占据正文位置。
        Assert.IsTrue(
            text.TrimStart().StartsWith("**子代理 ", StringComparison.Ordinal),
            text);
    }
}
