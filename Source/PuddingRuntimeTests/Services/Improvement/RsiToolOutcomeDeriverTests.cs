using PuddingRuntime.Services.Improvement.Rsi;

namespace PuddingRuntimeTests.Services.Improvement;

/// <summary>
/// RSI S3 §1.2 / §2.3 纯函数增量契约测试：钉住三态结局判定规则。
/// <para>
/// 每条用例都对应一个真实的实现错误（规格 §4「每条都必须能红」）：
/// 最关键的是 U 系——若有人把「exitCode 缺失」实现成「默认成功」，
/// Unknown 用例会立即变红，而不是让污染悄悄溜进轨迹统计。
/// </para>
/// </summary>
[TestClass]
public sealed class RsiToolOutcomeDeriverTests
{
    // ---------------------------------------------------------------- C 系：Completed / Failed 的正向判定

    /// <summary>C1：exitCode == 0 且无 error ⇒ Completed（成功的唯一形态）。</summary>
    [TestMethod]
    public void C1_ExitCodeZeroWithoutError_DerivesCompleted()
    {
        var result = RsiToolOutcomeDeriver.Derive(
            """{"toolCallId":"c1","name":"fs.read","output":"ok","exitCode":0}""");

        Assert.AreEqual(RsiToolOutcome.Completed, result.Outcome);
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsNull(result.Error);
    }

    /// <summary>C2：exitCode 非 0（含负数）⇒ Failed。</summary>
    [TestMethod]
    public void C2_NonZeroExitCode_DerivesFailed()
    {
        var failed = RsiToolOutcomeDeriver.Derive("""{"exitCode":1}""");
        var negative = RsiToolOutcomeDeriver.Derive("""{"exitCode":-1}""");

        Assert.AreEqual(RsiToolOutcome.Failed, failed.Outcome);
        Assert.AreEqual(1, failed.ExitCode);
        Assert.AreEqual(RsiToolOutcome.Failed, negative.Outcome, "exitCode 非零包含负数。");
    }

    /// <summary>C3：exitCode == 0 但 error 非空 ⇒ Failed（error 优先，不得因 0 而判成功）。</summary>
    [TestMethod]
    public void C3_ZeroExitCodeWithNonEmptyError_DerivesFailed_ErrorTakesPrecedence()
    {
        var result = RsiToolOutcomeDeriver.Derive(
            """{"toolCallId":"c3","name":"fs.write","exitCode":0,"error":"disk full"}""");

        Assert.AreEqual(RsiToolOutcome.Failed, result.Outcome, "exitCode 为 0 不能抵消非空 error。");
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("disk full", result.Error);
    }

    /// <summary>C4：只有 error 非空、无 exitCode ⇒ Failed，且 ExitCode 保持 null（不得填 0 冒充）。</summary>
    [TestMethod]
    public void C4_ErrorOnlyWithoutExitCode_DerivesFailed_AndKeepsExitCodeNull()
    {
        var result = RsiToolOutcomeDeriver.Derive(
            """{"toolCallId":"c4","name":"web.search","error":"timeout"}""");

        Assert.AreEqual(RsiToolOutcome.Failed, result.Outcome);
        Assert.IsNull(result.ExitCode, "exitCode 缺失必须保持 null，不得用 0 冒充。");
        Assert.AreEqual("timeout", result.Error);
    }

    /// <summary>C5：exitCode == 0 且 error 为空串 ⇒ 仍 Completed（「为空」与 ADR-064 IsSuccessful 的 IsNullOrWhiteSpace 语义一致）。</summary>
    [TestMethod]
    public void C5_ZeroExitCodeWithEmptyStringError_IsStillCompleted()
    {
        var result = RsiToolOutcomeDeriver.Derive("""{"exitCode":0,"error":""}""");

        Assert.AreEqual(RsiToolOutcome.Completed, result.Outcome, "空 error 不是「非空 error」。");
    }

    // ---------------------------------------------------------------- U 系：Unknown 一等公民（本增量的灵魂）

    /// <summary>U1：exitCode 与 error 都缺失 ⇒ Unknown，不得默认成功——把「缺 exitCode」实现成 Completed 的话本用例必红。</summary>
    [TestMethod]
    public void U1_MissingExitCodeAndMissingError_DerivesUnknown_NeverDefaultsToSuccess()
    {
        var result = RsiToolOutcomeDeriver.Derive(
            """{"toolCallId":"u1","name":"no.exitcode.tool","output":"done"}""");

        Assert.AreEqual(RsiToolOutcome.Unknown, result.Outcome, "两者都缺失时不得默认成成功。");
        Assert.IsNull(result.ExitCode, "Unknown 不得用 0 冒充 exitCode。");
        Assert.IsNull(result.Error);
    }

    /// <summary>U2：同一缺失形态既不是 Failed 也不是 Completed（Unknown 不得退化成任一确定态）。</summary>
    [TestMethod]
    public void U2_UnknownOutcome_IsNeitherFailedNorCompleted()
    {
        var result = RsiToolOutcomeDeriver.Derive("""{"name":"unknown-tool"}""");

        Assert.AreNotEqual(RsiToolOutcome.Failed, result.Outcome, "缺失不是失败。");
        Assert.AreNotEqual(RsiToolOutcome.Completed, result.Outcome, "缺失不是成功。");
    }

    /// <summary>U3：payload 为 null / 空串 / 空白 / 非法 JSON / 非 JSON 对象 ⇒ Unknown 且不抛异常。</summary>
    [TestMethod]
    public void U3_NullEmptyOrInvalidPayload_ReturnsUnknownWithoutThrowing()
    {
        string?[] payloads =
        [
            null,
            string.Empty,
            "   ",
            "not-json{",
            "[1,2,3]",
            "\"just-a-string\"",
            "42",
            """{"exitCode":"not-a-number"}""", // 合法 JSON 但 exitCode 不可解析 ⇒ 视同缺失
        ];

        foreach (var payload in payloads)
        {
            var result = RsiToolOutcomeDeriver.Derive(payload);

            Assert.AreEqual(RsiToolOutcome.Unknown, result.Outcome, $"payload={payload ?? "<null>"} 应判 Unknown。");
            Assert.IsNull(result.ExitCode, $"payload={payload ?? "<null>"} 不得伪造退出码。");
        }
    }

    // ---------------------------------------------------------------- F 系：字段契约（与折叠层一致）

    /// <summary>F1：exit_code（下划线）回退也必须被识别——删掉回退分支的话本用例必红。</summary>
    [TestMethod]
    public void F1_SnakeCaseExitCodeFallback_IsHonored()
    {
        var failed = RsiToolOutcomeDeriver.Derive("""{"exit_code":2}""");
        var completed = RsiToolOutcomeDeriver.Derive("""{"exit_code":0}""");

        Assert.AreEqual(RsiToolOutcome.Failed, failed.Outcome);
        Assert.AreEqual(2, failed.ExitCode, "下划线形态的值必须被解出，而不只是用于判定。");
        Assert.AreEqual(RsiToolOutcome.Completed, completed.Outcome, "exit_code=0 且无 error ⇒ Completed。");
    }

    /// <summary>F2：exitCode 为数字字符串时与折叠层 GetInt 契约一致（Number 与 String 双形态）。</summary>
    [TestMethod]
    public void F2_NumericStringExitCode_IsParsedLikeTheFoldLayer()
    {
        var result = RsiToolOutcomeDeriver.Derive("""{"exitCode":"3"}""");

        Assert.AreEqual(RsiToolOutcome.Failed, result.Outcome);
        Assert.AreEqual(3, result.ExitCode);
    }

    /// <summary>F3：name / output / toolCallId 被正确解析出来，不得丢失。</summary>
    [TestMethod]
    public void F3_ToolNameAndOutputAndCallId_AreExtractedFromPayload()
    {
        var result = RsiToolOutcomeDeriver.Derive(
            """{"toolCallId":"call-42","name":"web.search","output":"hello world","exitCode":0}""");

        Assert.AreEqual("web.search", result.ToolName);
        Assert.AreEqual("hello world", result.Output);
        Assert.AreEqual("call-42", result.ToolCallId);
    }
}
