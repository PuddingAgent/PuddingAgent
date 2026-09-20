using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PuddingCode.Classification;
using PuddingCode.Tools;
using PuddingRuntime.Classification;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <see cref="ClassifierStatusTool"/> 的离线单元测试（零网络，切片 S6a，方案 v2 §8.2 / §14.10 D5）。
/// <para>
/// 逐条锁定：只读 Descriptor 与 TryAddEnumerable 注册、正常态输出字段齐全且无厂商名（§14.11-1），
/// 未注册仲裁位 ⇒ fail-closed 占位可见、输出脱敏（对齐 list_llm_providers 纪律）、
/// Reviewer 生效取值输出（守护既有默认值未被翻转）与 per-key deferred 计数/退避档端到端可见。
/// 测试组合故意不注册 IJevDecisionService ⇒ 仲裁位为 fail-closed 占位（生产同形态）。
/// </para>
/// </summary>
[TestClass]
public sealed class ClassifierStatusToolTests
{
    private static readonly string[] ForbiddenVendorNames = ["jev", "noul", "choice"];

    // ---------- Descriptor：只读 + 注册（照 list_tool_approvals 范式） ----------

    [TestMethod]
    public void T10_Descriptor_IsReadOnly_And_RegisteredViaTryAddEnumerable()
    {
        using var provider = BuildHost();
        var tool = ResolveTool(provider);

        var descriptor = tool.Descriptor;
        Assert.AreEqual("classifier_status", descriptor.ToolId);
        Assert.AreEqual(ToolCategory.Security, descriptor.Category);
        Assert.IsTrue(descriptor.Safety.HasFlag(ToolSafetyFlags.ReadOnly), "只读探查。");
        Assert.IsTrue(descriptor.Safety.HasFlag(ToolSafetyFlags.ConcurrencySafe));
        Assert.IsFalse(descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresNetwork), "探查健康 ≠ 触发分类器网络调用。");
        Assert.IsFalse(descriptor.Safety.HasFlag(ToolSafetyFlags.Destructive));

        Assert.IsFalse(
            ForbiddenVendorNames.Any(name => descriptor.Description.Contains(name, StringComparison.OrdinalIgnoreCase)),
            "Descriptor 描述不得硬编码厂商名。");
    }

    // ---------- ⑦ 正常态输出字段齐全且无厂商名 ----------

    [TestMethod]
    public async Task T11_NormalOutput_FieldsComplete_NoVendorNames()
    {
        using var provider = BuildHost();
        var tool = ResolveTool(provider);

        var result = await ExecuteAsync(tool);

        Assert.IsTrue(result.Success, $"执行应成功：{result.Error}");
        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;

        AssertTrueProperty(root, "reviewer");
        AssertTrueProperty(root, "arbiterRegistered");
        AssertTrueProperty(root, "arbiterClassifierId");

        var thresholds = root.GetProperty("thresholds");
        AssertTrueProperty(thresholds, "permanentConfidenceThreshold");
        AssertTrueProperty(thresholds, "arbiterTimeoutMs");
        AssertTrueProperty(thresholds, "unavailableBackoffBaseMs");
        AssertTrueProperty(thresholds, "degradedAfterConsecutiveDeferred");
        AssertTrueProperty(thresholds, "unavailableAfterConsecutiveDeferred");
        AssertTrueProperty(thresholds, "maxRetryAfterMs");

        Assert.AreEqual(0.90, thresholds.GetProperty("permanentConfidenceThreshold").GetDouble(), 0.0001, "生效永久类门槛（§14.13.3）。");
        Assert.AreEqual(3000, thresholds.GetProperty("arbiterTimeoutMs").GetInt32(), "生效仲裁超时（§14.13.6）。");
        Assert.AreEqual(2000, thresholds.GetProperty("unavailableBackoffBaseMs").GetInt32(), "生效退避基数（§14.9）。");
        Assert.AreEqual(3, thresholds.GetProperty("degradedAfterConsecutiveDeferred").GetInt32());
        Assert.AreEqual(5, thresholds.GetProperty("unavailableAfterConsecutiveDeferred").GetInt32());
        Assert.AreEqual(60_000, thresholds.GetProperty("maxRetryAfterMs").GetInt32());

        var classifiers = root.GetProperty("classifiers");
        Assert.IsTrue(classifiers.GetArrayLength() >= 2, "至少含管线与规则类分类器条目。");
        foreach (var entry in classifiers.EnumerateArray())
        {
            AssertTrueProperty(entry, "classifierId");
            AssertTrueProperty(entry, "health");
            AssertTrueProperty(entry, "consecutiveFailures");
        }

        AssertTrueProperty(root, "deferredCounters");
        AssertHasNoVendorName(root, "正常态输出");
    }

    // ---------- ⑧ 未注册仲裁位 ⇒ fail-closed 占位可见 ----------

    [TestMethod]
    public async Task T12_UnregisteredArbiter_FailClosedPlaceholderVisible()
    {
        using var provider = BuildHost();
        var tool = ResolveTool(provider);

        var result = await ExecuteAsync(tool);

        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;
        Assert.IsFalse(root.GetProperty("arbiterRegistered").GetBoolean(), "未注册 IJevDecisionService ⇒ 仲裁位未注册。");
        Assert.AreEqual(
            "arbiter.not-registered",
            root.GetProperty("arbiterClassifierId").GetString(),
            "fail-closed 占位的稳定标识必须能被看出来。");
        Assert.IsTrue(
            root.GetProperty("classifiers").EnumerateArray()
                .Any(c => c.GetProperty("classifierId").GetString() == "arbiter.not-registered"),
            "健康条目也应包含占位分类器（默认 Unknown）。");
    }

    // ---------- ⑨ 输出脱敏：不含 apiKey/token 字样与参数原文 ----------

    [TestMethod]
    public async Task T13_Output_Redacted_NoSecretMaterial()
    {
        using var provider = BuildHost();
        var reporter = provider.GetRequiredService<ClassifierHealthReporter>();
        var secretArguments = """{"command":"echo","payload":"apiKey=sk-abcdef0123456789ABCDEFghijk token=BEGIN PRIVATE KEY"}""";
        reporter.RecordDeferred("stub-classifier", "shell", secretArguments, "approval_review_classifier_unknown");

        var result = await ExecuteAsync(ResolveTool(provider));

        var output = result.Output;
        StringAssert.DoesNotMatch(
            output,
            new Regex("apikey|api_key|secret|password", RegexOptions.IgnoreCase),
            "不得含密钥字段样词汇。");
        StringAssert.DoesNotMatch(output, new Regex("sk-abcdef", RegexOptions.IgnoreCase), "不得泄露参数原文片段。");
        StringAssert.DoesNotMatch(output, new Regex("BEGIN PRIVATE", RegexOptions.IgnoreCase), "不得泄露令牌原文。");

        using var doc = JsonDocument.Parse(output);
        var counter = doc.RootElement.GetProperty("deferredCounters").EnumerateArray().Single();
        Assert.IsTrue(
            Regex.IsMatch(counter.GetProperty("argumentsHash").GetString()!, "^[0-9a-f]{64}$"),
            "参数只以 64 位十六进制 SHA-256 哈希出现。");
    }

    // ---------- Reviewer 当前生效取值输出（守护既有默认 classifier 未被翻转） ----------

    [TestMethod]
    public async Task T14_ReviewerEffectiveValue_IsEmitted_AndExistingDefaultUnchanged()
    {
        using var provider = BuildHost();
        var tool = ResolveTool(provider);

        var result = await ExecuteAsync(tool);

        using var doc = JsonDocument.Parse(result.Output);
        Assert.AreEqual(
            "classifier",
            doc.RootElement.GetProperty("reviewer").GetString(),
            "ToolApproval:Reviewer 现行默认 classifier（本切片不得翻转）；工具输出当前生效取值。");

        // 组合未传显式配置 ⇒ 输出的取值必须直接来自既有默认（Options 默认值）。
        Assert.AreEqual(
            ToolApprovalRuntimeOptions.ClassifierReviewer,
            provider.GetRequiredService<IOptions<ToolApprovalRuntimeOptions>>().Value.Reviewer);
    }

    // ---------- 计数与退避档端到端可见（3 次 deferred ⇒ degraded + 8000ms） ----------

    [TestMethod]
    public async Task T15_DeferredCounters_And_BackoffTier_VisibleInOutput()
    {
        using var provider = BuildHost();
        var reporter = provider.GetRequiredService<ClassifierHealthReporter>();
        const string argsJson = """{"command":"dotnet test"}""";
        reporter.RecordDeferred("stub-classifier", "shell", argsJson, "code");
        reporter.RecordDeferred("stub-classifier", "shell", argsJson, "code");
        reporter.RecordDeferred("stub-classifier", "shell", argsJson, "code");

        var result = await ExecuteAsync(ResolveTool(provider));

        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;

        var status = root.GetProperty("classifiers").EnumerateArray()
            .Single(c => c.GetProperty("classifierId").GetString() == "stub-classifier");
        Assert.AreEqual("degraded", status.GetProperty("health").GetString(), "3 次连续 deferred ⇒ Degraded（§14.9.2）。");
        Assert.AreEqual(3, status.GetProperty("consecutiveFailures").GetInt32());
        Assert.AreEqual("code", status.GetProperty("detail").GetString(), "最近失败原因码可见。");

        var counter = root.GetProperty("deferredCounters").EnumerateArray().Single();
        Assert.AreEqual("shell", counter.GetProperty("toolId").GetString());
        Assert.AreEqual(3, counter.GetProperty("consecutiveDeferred").GetInt32());
        Assert.AreEqual(8000, counter.GetProperty("retryAfterMs").GetInt32(), "当前退避档：2000 × 2^2。");
    }

    // ---------- helpers ----------

    private static ServiceProvider BuildHost()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IToolApprovalAllowlistStore>(new InMemoryToolApprovalAllowlistStore());
        services.AddSingleton<IToolApprovalAuditStore>(new InMemoryToolApprovalAuditStore());

        // 故意不注册 IJevDecisionService ⇒ 仲裁位为 fail-closed 占位（与生产未配置形态一致）。
        services.AddPuddingToolRegistry();
        return services.BuildServiceProvider();
    }

    private static ClassifierStatusTool ResolveTool(ServiceProvider provider)
        => provider.GetServices<IPuddingTool>().OfType<ClassifierStatusTool>().Single();

    private static Task<ToolExecutionResult> ExecuteAsync(ClassifierStatusTool tool)
        => tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-classifier-status",
            ArgumentsJson = string.Empty,
            Context = new ToolExecutionContext
            {
                SessionId = "s-1",
                WorkspaceId = "ws-test",
                AgentInstanceId = "agent-1",
            },
        });

    private static void AssertTrueProperty(JsonElement element, string propertyName)
        => Assert.IsTrue(
            element.TryGetProperty(propertyName, out _),
            $"输出缺少字段 {propertyName}。");

    private static void AssertHasNoVendorName(JsonElement root, string scenario)
    {
        var serialized = root.GetRawText().ToLowerInvariant();
        foreach (var vendor in ForbiddenVendorNames)
        {
            Assert.IsFalse(serialized.Contains(vendor, StringComparison.Ordinal), $"{scenario} 不得出现厂商名 {vendor}（§14.11-1 零厂商依赖）。");
        }
    }
}
