using System.Text.RegularExpressions;

namespace PuddingRuntimeTests.Diagnostics;

/// <summary>
/// Stage 5 治理门禁：把 `Docs/08_how_debuge/02-日志位置与埋点约束.md` §9 的**人读约束**升级为**机器门禁**
/// （可诊断基础设施设计 §7 Stage 5）。
/// <para>
/// 判据来源：文档规定了必填字段，但没有任何机制阻止它们被后来的重构删掉——本次事故之所以难查，
/// 正是因为「失败路径只记了一句话」。这些用例把「诊断契约」变成会红的断言：
/// 只要有人删掉网关的字节数记账、删掉失败日志的因果字段、或把 `ErrorCode` 换回 CLR 类型名，立刻取红。
/// </para>
/// <para>
/// 每个字段断言都配了**正控制**（故意喂一段缺少该字段的文本，断言探测器报出来），
/// 否则探测器通路坏了会静默变成永远绿。
/// </para>
/// </summary>
[TestClass]
public sealed class DiagnosticContractGateTests
{
    private const string DirectLlmRelativePath = @"Source/PuddingRuntime/Services/DirectLlmClient.cs";
    private const string AgentExecutionRelativePath = @"Source/PuddingRuntime/Services/AgentExecutionService.cs";

    private static readonly string[] GatewayRelativePaths =
    [
        @"Source/PuddingCore/Core/ResponsesLlmGateway.cs",
        @"Source/PuddingCore/Core/OpenAiLlmGateway.cs",
        @"Source/PuddingCore/Core/AnthropicMessagesLlmGateway.cs",
    ];

    /// <summary>失败终态日志必须自证「谁、哪一步、能不能重试、多大、第几次」。</summary>
    private static readonly string[] StreamErrorLogTokens =
    [
        "provider={Provider}",
        "model={Model}",
        "cause={Cause}",
        "phase={Phase}",
        "retryable={Retryable}",
        "requestBytes={RequestBytes}",
        "attempt={Attempt}/{MaxRetries}",
    ];

    private static readonly string[] StreamRetryLogTokens =
    [
        "cause={Cause}",
        "phase={Phase}",
        "requestBytes={RequestBytes}",
        "dispatchCount={DispatchCount}",
    ];

    /// <summary>终态 markdown 必须保留 §9 的可定位字段（成功/失败都要能按它搜日志）。</summary>
    private static readonly string[] TerminalMarkdownTokens =
    [
        "Session ID",
        "Message ID / Turn ID",
        "Trace ID",
        "Error ID",
        "Time",
        "Location",
        "Error Code",
        "因果码",
        "失败阶段",
        "处置建议",
    ];

    [TestMethod]
    public void FieldDetector_ReportsMissingTokens_AndStaysQuietWhenPresent()
    {
        // 正控制：缺字段必须被报出来。
        var missing = FindMissingTokens("## 请求失败\n- Session ID: x", ["Session ID", "Trace ID", "因果码"]);
        CollectionAssert.AreEquivalent(new[] { "Trace ID", "因果码" }, missing.ToArray());

        // 负控制：字段齐了不得乱报。
        Assert.IsEmpty(FindMissingTokens("- Trace ID: x\n- 因果码: y", ["Trace ID", "因果码"]));
    }

    [TestMethod]
    public void DirectLlm_FailureAndRetryLogs_CarryTheDiagnosticContractFields()
    {
        var source = ReadRepositoryFile(DirectLlmRelativePath);

        Assert.IsEmpty(
            FindMissingTokens(source, StreamErrorLogTokens),
            "`[DirectLlm] STREAM ERROR` 日志必须保留诊断契约字段（providers/model/cause/phase/retryable/requestBytes/attempt）；" +
            "若确实要改模板，请同时更新本门禁并说明理由：" + DirectLlmRelativePath);

        Assert.IsEmpty(
            FindMissingTokens(source, StreamRetryLogTokens),
            "`[DirectLlm] STREAM RETRY` 日志必须保留 cause/phase/requestBytes/dispatchCount：" + DirectLlmRelativePath);
    }

    [TestMethod]
    public void DirectLlm_FailedActivity_UsesStableCauseCodeInsteadOfClrTypeName()
    {
        var source = ReadRepositoryFile(DirectLlmRelativePath);

        StringAssert.Contains(source, "ErrorCode = cause?.Code",
            "失败活动的 ErrorCode 必须是稳定因果码（否则「按码聚合事故」不可能做到）");

        // 正控制：探测器能识别旧写法。
        Assert.IsTrue(
            Regex.IsMatch("ErrorCode = error?.GetType().Name", @"ErrorCode\s*=\s*error\?\.GetType\(\)\.Name"),
            "正控制：必须能匹配回退到 CLR 类型名的旧写法");
    }

    [TestMethod]
    public void EveryGateway_RecordsRequestBytesAndPhase()
    {
        var offenders = new List<string>();

        foreach (var relativePath in GatewayRelativePaths)
        {
            var source = ReadRepositoryFile(relativePath);
            if (!source.Contains("LlmCallDiagnosticsScope.Current?.MarkDispatch(", StringComparison.Ordinal))
                offenders.Add($"{Path.GetFileName(relativePath)}: 缺 MarkDispatch（请求体字节数/派发次数）");
            if (!source.Contains("LlmCallDiagnosticsScope.Current?.MarkHeaders()", StringComparison.Ordinal))
                offenders.Add($"{Path.GetFileName(relativePath)}: 缺 MarkHeaders（阶段翻转）");
        }

        Assert.IsEmpty(offenders,
            "每个协议网关都必须把「请求体字节数 / 派发次数 / 阶段」写入诊断作用域：" + string.Join(" | ", offenders));
    }

    [TestMethod]
    public void TerminalError_CarriesPresentationAndCopyableReport()
    {
        var source = ReadRepositoryFile(AgentExecutionRelativePath);

        Assert.IsEmpty(
            FindMissingTokens(source,
            [
                "CauseCode = export.CauseCode",
                "CauseTitle = export.Presentation.Title",
                "CauseShortCause = export.Presentation.ShortCause",
                "RemediationHint = export.Cause.RemediationHint",
                "ReportVersion = export.ReportVersion",
                "EvidenceJson = export.EvidenceJson",
                "ReportText = export.ReportText",
                "ReportJson = export.ReportJson",
            ]),
            "终态诊断必须携带界面呈现字段与可复制载荷（ReportText/ReportJson/EvidenceJson）");

        Assert.IsEmpty(
            FindMissingTokens(source, TerminalMarkdownTokens),
            "终态 markdown 必须保留 §9 的可定位字段与因果行，否则用户复制的文本无法定位日志：" + AgentExecutionRelativePath);
    }

    private static List<string> FindMissingTokens(string text, IReadOnlyList<string> tokens)
        => tokens.Where(token => !text.Contains(token, StringComparison.Ordinal)).ToList();

    /// <summary>从测试输出目录向上找到仓库根（不依赖固定的目录层级，避免 CI/artifacts 路径产生假红）。</summary>
    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PuddingAgentNetwork.slnx")))
            {
                var fullPath = Path.Combine(directory.FullName, relativePath);
                Assert.IsTrue(File.Exists(fullPath), $"期望文件存在：{fullPath}");
                return File.ReadAllText(fullPath);
            }

            directory = directory.Parent;
        }

        Assert.Fail("未能从 " + AppContext.BaseDirectory + " 向上找到仓库根（PuddingAgentNetwork.slnx）");
        return string.Empty;
    }
}
