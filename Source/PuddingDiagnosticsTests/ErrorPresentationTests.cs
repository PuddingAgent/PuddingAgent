using System.Text.Json;
using PuddingCode.Diagnostics;

namespace PuddingDiagnosticsTests;

/// <summary>
/// 界面呈现与「一键复制现场」门禁（可诊断基础设施设计 §12）。
/// <para>
/// 判据来自用户诉求：**界面要一眼能懂，复制出去的东西必须够准、够全，能直接定位到日志现场**。
/// 因此这里断言标题不含异常类型名/英文原文、未知码保留原码、复制文本含时间/traceId/errorId/因果码/阶段/证据，
/// 且不泄露任何密钥形态的值。
/// </para>
/// </summary>
[TestClass]
public sealed class ErrorPresentationTests
{
    private static readonly string[] ForbiddenTitleTokens =
        ["Exception", "HttpRequestException", "SocketException", "Error while copying", "IOException"];

    [TestMethod]
    public void Presentation_IsFriendly_AndNeverEchoesTheTransportMessage()
    {
        var cause = LlmFailureClassifier.Default.Classify(
            FaultScenarios.CreateUploadResetChain(),
            FaultScenarios.IncidentReplica.Context with { PhaseHint = DiagnosticPhaseKind.RequestUpload });

        var presentation = ErrorPresentationCatalog.Present(cause);
        var classifier = new LlmFailureClassifier([nameof(FaultScenarios.CreateUploadResetChain)]);
        var tagged = classifier.Classify(
            FaultScenarios.CreateUploadResetChain(),
            FaultScenarios.IncidentReplica.Context with { PhaseHint = DiagnosticPhaseKind.RequestUpload });
        var taggedPresentation = ErrorPresentationCatalog.Present(tagged);

        Assert.AreEqual("模型服务连接中断", taggedPresentation.Title);
        Assert.AreEqual(DiagnosticCauseCode.RequestUploadReset, taggedPresentation.CauseCode);
        Assert.AreEqual(ErrorSeverities.Error, taggedPresentation.Severity);
        Assert.IsTrue(taggedPresentation.Retryable);
        Assert.IsFalse(string.IsNullOrWhiteSpace(taggedPresentation.PrimaryAction));

        foreach (var token in ForbiddenTitleTokens)
        {
            Assert.IsFalse(presentation.Title.Contains(token, StringComparison.Ordinal), $"标题不得出现 {token}");
            Assert.IsFalse(presentation.ShortCause.Contains(token, StringComparison.Ordinal), $"简略原因不得出现 {token}");
        }
    }

    [TestMethod]
    public void Presentation_ShowsTheRawCodeForUnknownCauses()
    {
        var presentation = ErrorPresentationCatalog.Describe("vision_provider_file_expired", retryable: false);

        Assert.AreEqual("图片处理失败", presentation.Title);
        Assert.AreEqual("vision_provider_file_expired", presentation.CauseCode);
        Assert.AreEqual(DiagnosticCauseCategory.Vision, presentation.Category);

        var totallyUnknown = ErrorPresentationCatalog.Describe("something.brand_new", retryable: false);
        Assert.AreEqual("something.brand_new", totallyUnknown.CauseCode, "未知码必须原样显示，不得伪装成已知");
    }

    [TestMethod]
    public void CopiedReport_CarriesEveryFieldNeededToLocateTheIncident()
    {
        var report = BuildReport();
        var text = report.RenderText();

        // 定位要素（用户明确要求：日期 / traceId / errorId / 因果 / 现场）。
        // 本地时间对得上日志时间戳，UTC 对得上跨时区汇报：两者都必须给。
        StringAssert.Contains(text, "2026-10-07T13:11:52.1280000+08:00");
        StringAssert.Contains(text, "2026-10-07T05:11:52.1280000+00:00");
        StringAssert.Contains(text, "e09ae04599944b8b82166321298225f8");
        StringAssert.Contains(text, "llm-c198a96c19ec4914b24d1b0fe11f4528");
        StringAssert.Contains(text, DiagnosticCauseCode.RequestUploadReset);
        StringAssert.Contains(text, "上传请求体");
        StringAssert.Contains(text, "attempt=3");
        StringAssert.Contains(text, "request_bytes=1048576");
        StringAssert.Contains(text, "socket_error_code=10054");
        StringAssert.Contains(text, "SocketException");
        StringAssert.Contains(text, "logs/error/pudding-error");
        StringAssert.Contains(text, "session-timeline");
        StringAssert.Contains(text, "模型服务连接中断");
    }

    [TestMethod]
    public void CopiedReport_JsonIsCompleteAndStable()
    {
        var report = BuildReport();
        var first = report.RenderJson();
        var second = report.RenderJson();

        Assert.AreEqual(first, second, "同一份报告两次渲染必须逐字符相同");

        using var document = JsonDocument.Parse(first);
        var root = document.RootElement;

        Assert.AreEqual(DiagnosticReportDocument.SchemaVersion, root.GetProperty("Schema").GetString());
        Assert.AreEqual("transport.request_upload_reset", root.GetProperty("CauseCode").GetString());
        Assert.AreEqual("request_upload", root.GetProperty("Evidence").GetProperty("phase").GetString());
        Assert.AreEqual(3, root.GetProperty("Attempts").GetArrayLength());
        Assert.AreEqual("模型服务连接中断", root.GetProperty("Presentation").GetProperty("Title").GetString());
    }

    [TestMethod]
    public void CopiedReport_NeverContainsSecretShapedValues()
    {
        var cause = LlmFailureClassifier.Default.Classify(
            FaultScenarios.CreateUploadResetChain(),
            FaultScenarios.IncidentReplica.Context with
            {
                PhaseHint = DiagnosticPhaseKind.RequestUpload,
                ProviderId = "deepseek",
            });
        var classifier = new LlmFailureClassifier([nameof(FaultScenarios.CreateUploadResetChain)]);
        var tagged = classifier.Classify(
            FaultScenarios.CreateUploadResetChain(),
            FaultScenarios.IncidentReplica.Context with { PhaseHint = DiagnosticPhaseKind.RequestUpload });

        var report = BuildReport() with
        {
            Evidence = new DiagnosticEvidenceBuilder()
                .Add("authorization", "Bearer sk-FIXTURE-not-a-real-key")
                .Add("endpoint", "https://api.example.com/v1?q=FIXTUREVALUE")
                .Build(),
        };

        var rendered = report.RenderText() + report.RenderJson();

        Assert.IsFalse(rendered.Contains("sk-FIXTURE", StringComparison.Ordinal), "复制载荷不得带出密钥形态的值");
        Assert.IsFalse(rendered.Contains("FIXTUREVALUE", StringComparison.Ordinal), "URL query 必须被剥离");
        Assert.IsFalse(string.IsNullOrWhiteSpace(cause.Rule));
        Assert.IsFalse(string.IsNullOrWhiteSpace(tagged.Rule));
    }

    [TestMethod]
    public void CopiedReport_IsClampedWithAnExplicitNotice()
    {
        // 直接用原始字典绕过证据构建器的预算，模拟「消费方塞进超长证据」的情形，
        // 验证渲染层的兜底截断是显式的（不是悄悄变短）。
        var bulky = new string('x', DiagnosticReportDocument.MaxRenderedChars + 1_000);
        var report = BuildReport() with
        {
            Evidence = new Dictionary<string, string> { ["note"] = bulky },
        };

        var text = report.RenderText();

        StringAssert.Contains(text, "报告已截断");
        Assert.IsLessThan(DiagnosticReportDocument.MaxRenderedChars + 256, text.Length);
    }

    private static DiagnosticReportDocument BuildReport()
    {
        var evidence = new DiagnosticEvidenceBuilder()
            .Add(DiagnosticEvidenceKeys.SocketErrorCode, 10054)
            .Add(DiagnosticEvidenceKeys.RequestBytes, 1_048_576)
            .Add(DiagnosticEvidenceKeys.Phase, DiagnosticPhases.RequestUploadWire)
            .Add(DiagnosticEvidenceKeys.ExceptionChain, "HttpRequestException: Error while copying content to a stream. <- IOException: Unable to write data <- SocketException(10054)")
            .Build();

        var incident = new IncidentView
        {
            TurnId = "msg-1791349647230-e7f5o83q",
            TerminalStatus = "failed",
            TraceId = "e09ae04599944b8b82166321298225f8",
            ErrorId = "llm-c198a96c19ec4914b24d1b0fe11f4528",
            RootCauseCode = DiagnosticCauseCode.RequestUploadReset,
            RootCauseMessage = "请求体上传阶段连接被对端重置（网络或代理不稳定）。",
            RemediationHint = "先确认网络/代理路径，再重试。",
            Phase = DiagnosticPhaseKind.RequestUpload,
            MaxRetries = 2,
            Attempts =
            [
                new IncidentAttempt(1, "retried", DiagnosticCauseCode.RequestUploadReset, DiagnosticPhases.RequestUploadWire,
                    20_310, 1_048_576, new DateTimeOffset(2026, 10, 7, 5, 10, 15, TimeSpan.Zero)),
                new IncidentAttempt(2, "retried", DiagnosticCauseCode.RequestUploadReset, DiagnosticPhases.RequestUploadWire,
                    54_290, 1_048_576, new DateTimeOffset(2026, 10, 7, 5, 10, 36, TimeSpan.Zero)),
                new IncidentAttempt(3, "failed", DiagnosticCauseCode.RequestUploadReset, DiagnosticPhases.RequestUploadWire,
                    20_093, 1_048_576, new DateTimeOffset(2026, 10, 7, 5, 11, 32, TimeSpan.Zero)),
            ],
            Conclusion = "共 3 次尝试；根因 transport.request_upload_reset。",
            EvidenceLinks = ["errorId=llm-c198a96c19ec4914b24d1b0fe11f4528"],
        };

        return DiagnosticReportDocument.FromIncident(
            incident,
            capturedAt: new DateTimeOffset(2026, 10, 7, 13, 11, 52, 128, TimeSpan.FromHours(8)),
            evidence: evidence,
            providerId: "deepseek",
            modelId: "deepseek-flash",
            endpointHost: "api.deepseek.com",
            sessionId: "206a9b48ec904ebb93e7541131fbb835",
            workspaceId: "default",
            agentTemplateId: "global:general-assistant",
            platformVersion: "test");
    }
}
