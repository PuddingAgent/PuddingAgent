using PuddingCode.Classification;
using PuddingRuntime.Operators.Adapters;

namespace PuddingRuntimeTests.Operators;

/// <summary>
/// 可编程的既有工具审批分类器 stub（零网络、零模型）：用于把「经适配器」与「直接调用既有实现」逐条对照。
/// </summary>
internal sealed class StubToolCallClassifier : IToolCallClassifier
{
    private readonly Func<ToolCallClassificationContext, ClassificationVerdict?> _factory;

    public StubToolCallClassifier(ClassificationVerdict verdict)
        : this(_ => verdict)
    {
    }

    public StubToolCallClassifier(Func<ToolCallClassificationContext, ClassificationVerdict?> factory)
        => _factory = factory;

    /// <summary>返回 null 的退化实现（契约上不该发生，但适配器必须不冒泡异常）。</summary>
    public static StubToolCallClassifier ReturningNull() => new(_ => null);

    /// <summary>总是抛指定异常的退化实现。</summary>
    public static StubToolCallClassifier Throwing(Exception exception) => new(_ => throw exception);

    public string ClassifierId { get; init; } = "stub-tool-call-classifier";

    /// <summary>被调用次数（用于证明适配器只转发一次、不重复调用）。</summary>
    public int Calls { get; private set; }

    /// <summary>最后一次收到的上下文（用于证明适配器<b>原样</b>转发既有上下文）。</summary>
    public ToolCallClassificationContext? LastContext { get; private set; }

    public Task<ClassificationVerdict> ClassifyAsync(
        ToolCallClassificationContext context,
        CancellationToken ct = default)
    {
        Calls++;
        LastContext = context;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_factory(context)!);
    }
}

/// <summary>一个代表性等价性用例：既有输入 + 既有裁决 + <b>期望</b>的规范化标签与置信度。</summary>
/// <param name="Name">用例名（打印进断言消息）。</param>
/// <param name="Context">既有裁决输入。</param>
/// <param name="Verdict">既有裁决结论（适配器的输入）。</param>
/// <param name="ExpectedLabel">期望的规范化主标签（<b>字面量</b>，不引用生产常量以形成独立见证）。</param>
/// <param name="ExpectedConfidence">期望置信度（主标签键缺失时为 null）。</param>
/// <param name="ExpectedRuleReference">期望的证据引用（<c>AppliedRuleId</c> 为 null 时为 null）。</param>
internal sealed record AdapterCase(
    string Name,
    ToolCallClassificationContext Context,
    ClassificationVerdict Verdict,
    string ExpectedLabel,
    double? ExpectedConfidence,
    string? ExpectedRuleReference);

/// <summary>适配器等价性测试的数据工厂。</summary>
internal static class ToolApprovalAdapterTestData
{
    /// <summary>代表性用例集合（覆盖规格 §4 要求的五类）。</summary>
    public static IReadOnlyList<AdapterCase> Cases { get; } =
    [
        // ① allow_once：分布齐全、规则命中、模型耗时齐全
        new AdapterCase(
            "allow_once",
            Context(command: "dotnet test", arguments: """{"command":"dotnet test"}"""),
            new ClassificationVerdict
            {
                Outcome = ClassificationOutcome.AllowOnce,
                Reason = "规则命中：允许本次 dotnet test",
                PerOutcomeConfidence = new Dictionary<string, double>
                {
                    ["allow_once"] = 0.97d,
                    ["allow_permanent"] = 0.61d,
                    ["deny_once"] = 0.02d,
                    ["deny_permanent"] = 0.01d,
                },
                ClassifierId = "stub-tool-call-classifier",
                ClassifierModel = "stub-model-1",
                LatencyMs = 42.5d,
                ReasonCode = "rule_allow_fast_path",
                AppliedRuleId = "rule-allow-1",
            },
            ExpectedLabel: "allow_once",
            ExpectedConfidence: 0.97d,
            ExpectedRuleReference: "rule-allow-1"),

        // ② deny_permanent：AppliedRuleId 为 null（不得产出证据）+ ReasonCode 为 null
        new AdapterCase(
            "deny_permanent",
            Context(command: "rm -rf data", arguments: """{"command":"rm -rf data"}"""),
            new ClassificationVerdict
            {
                Outcome = ClassificationOutcome.DenyPermanent,
                Reason = "模型判定：不可逆的数据删除操作",
                PerOutcomeConfidence = new Dictionary<string, double>
                {
                    ["allow_once"] = 0.01d,
                    ["allow_permanent"] = 0.0d,
                    ["deny_once"] = 0.83d,
                    ["deny_permanent"] = 0.91d,
                },
                ClassifierId = "stub-tool-call-classifier",
                ClassifierModel = "stub-model-1",
                LatencyMs = 88d,
                ReasonCode = null,
                AppliedRuleId = null,
            },
            ExpectedLabel: "deny_permanent",
            ExpectedConfidence: 0.91d,
            ExpectedRuleReference: null),

        // ③ unknown / 降级：PerOutcomeConfidence 为 null ⇒ 空字典、置信度为 null，但标签仍是规范化键 unknown
        new AdapterCase(
            "unknown_degraded",
            Context(command: "curl example.invalid", arguments: """{"command":"curl example.invalid"}"""),
            new ClassificationVerdict
            {
                Outcome = ClassificationOutcome.Unknown,
                Reason = "仲裁不可用：按降级契约返回未知，等待人工处置",
                PerOutcomeConfidence = null,
                ClassifierId = "stub-tool-call-classifier",
                ClassifierModel = null,
                LatencyMs = null,
                ReasonCode = "classifier_unavailable",
                AppliedRuleId = null,
            },
            ExpectedLabel: "unknown",
            ExpectedConfidence: null,
            ExpectedRuleReference: null),

        // ④ 主标签键缺失：分布非空但<b>不含</b>主标签 ⇒ 置信度必须为 null（不得回退到别的键、不得填 0）
        new AdapterCase(
            "allow_permanent_without_primary_key",
            Context(command: "git push origin master", arguments: """{"command":"git push origin master"}"""),
            new ClassificationVerdict
            {
                Outcome = ClassificationOutcome.AllowPermanent,
                Reason = "模型判定：放行并沉淀长期规则",
                PerOutcomeConfidence = new Dictionary<string, double>
                {
                    ["allow_once"] = 0.4d,
                    ["deny_once"] = 0.6d,
                },
                ClassifierId = "stub-tool-call-classifier",
                ClassifierModel = "stub-model-1",
                LatencyMs = 12d,
                ReasonCode = null,
                AppliedRuleId = "rule-allow-2",
            },
            ExpectedLabel: "allow_permanent",
            ExpectedConfidence: null,
            ExpectedRuleReference: "rule-allow-2"),

        // ⑤ deny_once：分布与证据齐全（补足最后一个规范键）
        new AdapterCase(
            "deny_once",
            Context(toolId: "file_write", command: "write", arguments: """{"path":"Source/a.cs"}"""),
            new ClassificationVerdict
            {
                Outcome = ClassificationOutcome.DenyOnce,
                Reason = "规则命中：本次写法被拒绝，仅限本次",
                PerOutcomeConfidence = new Dictionary<string, double>
                {
                    ["allow_once"] = 0.05d,
                    ["deny_once"] = 0.95d,
                },
                ClassifierId = "stub-tool-call-classifier",
                ClassifierModel = null,
                LatencyMs = 3d,
                ReasonCode = "rule_deny_once",
                AppliedRuleId = "rule-deny-9",
            },
            ExpectedLabel: "deny_once",
            ExpectedConfidence: 0.95d,
            ExpectedRuleReference: "rule-deny-9"),
    ];

    /// <summary>构造一个代表性的既有裁决输入。</summary>
    public static ToolCallClassificationContext Context(
        string toolId = "shell",
        string? command = "dotnet test",
        string? arguments = """{"command":"dotnet test"}""") => new()
        {
            ToolId = toolId,
            CommandName = command,
            ArgumentsJson = arguments,
            WorkingDirectory = @"E:\github\AgentNetworkPlan\PuddingAgent",
            Shell = "pwsh",
            OperationContext = "运行单元测试",
            Purpose = "验证改动",
            Necessity = "提交前必须验证",
            FactBasis = ["改动已完成"],
            TargetResources = ["Source/PuddingRuntimeTests"],
            IsIrreversibleOperation = false,
            MayDamageOrDeleteData = false,
            WorkspaceId = "ws-1",
            SessionId = "sess-1",
            AgentInstanceId = "agent-1",
            UserId = "user-1",
            RecentTrajectory = "agent 最近轨迹摘要",
            MatchedRuleSummaries = ["既有规则摘要"],
        };
}
