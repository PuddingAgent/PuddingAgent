using System.Net.Http;
using System.Text.Json;
using PuddingCode.Diagnostics;
using PuddingCode.Observability;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Diagnostics;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// Stage 4 门禁：终态失败的**投影与导出**（可诊断基础设施设计 §12 / §13）。
/// <para>
/// 判据来自用户诉求：界面看到的是「标题 + 大概原因」，而复制出去的东西必须够准、够全、
/// 能直接定位日志现场（时间 / errorId / traceId / 因果码 / 阶段 / 证据 / 日志定位提示）。
/// </para>
/// </summary>
[TestClass]
public sealed class TerminalDiagnosticExportTests
{
    private static readonly DateTimeOffset Captured = new(2026, 10, 7, 13, 11, 52, 128, TimeSpan.FromHours(8));

    [TestMethod]
    public void Export_UploadReset_GivesFriendlyPresentationAndCopyableReport()
    {
        var (error, scope) = BuildUploadResetError();

        var export = TerminalDiagnosticExport.Build(error, scope, Input());

        Assert.AreEqual(DiagnosticCauseCode.RequestUploadReset, export.CauseCode);
        Assert.AreEqual("request_upload", export.Phase);
        Assert.AreEqual("模型服务连接中断", export.Presentation.Title);
        Assert.AreEqual("transport", export.Presentation.Category);
        Assert.IsTrue(export.Presentation.Retryable);

        // 界面文案不得出现异常类型名或英文原文（技术细节只在复制载荷里）。
        foreach (var token in new[] { "Exception", "Error while copying" })
        {
            Assert.IsFalse(export.Presentation.Title.Contains(token, StringComparison.Ordinal));
            Assert.IsFalse(export.Presentation.ShortCause.Contains(token, StringComparison.Ordinal));
        }

        // 复制载荷必须自带定位四件套 + 阶段 + 体积 + 日志提示。
        StringAssert.Contains(export.ReportText, TerminalDiagnosticExport.ReportVersionValue);
        StringAssert.Contains(export.ReportText, "2026-10-07T13:11:52.1280000+08:00");
        StringAssert.Contains(export.ReportText, "e09ae04599944b8b82166321298225f8");
        StringAssert.Contains(export.ReportText, "llm-test-error-id");
        StringAssert.Contains(export.ReportText, DiagnosticCauseCode.RequestUploadReset);
        StringAssert.Contains(export.ReportText, "上传请求体");
        StringAssert.Contains(export.ReportText, "1048576");
        StringAssert.Contains(export.ReportText, "10054");
        StringAssert.Contains(export.ReportText, "logs/error/pudding-error");
        StringAssert.Contains(export.ReportText, "session-timeline");
        StringAssert.Contains(export.ReportText, "模型服务连接中断");

        using var json = JsonDocument.Parse(export.ReportJson);
        Assert.AreEqual(DiagnosticReportDocument.SchemaVersion, json.RootElement.GetProperty("Schema").GetString());
        Assert.AreEqual(DiagnosticCauseCode.RequestUploadReset, json.RootElement.GetProperty("CauseCode").GetString());
        Assert.AreEqual("request_upload", json.RootElement.GetProperty("Evidence").GetProperty("phase").GetString());
        Assert.IsGreaterThan(0, json.RootElement.GetProperty("Attempts").GetArrayLength());

        using var evidence = JsonDocument.Parse(export.EvidenceJson);
        Assert.AreEqual("request_upload", evidence.RootElement.GetProperty("phase").GetString());
        Assert.AreEqual("10054", evidence.RootElement.GetProperty("socket_error_code").GetString());
    }

    [TestMethod]
    public void Export_WithoutAttachedCause_StillYieldsAStableCode()
    {
        var scope = LlmCallDiagnosticsScope.Begin();
        var error = FaultScenarios.CreateUploadResetChain();

        // 故意不 Attach：上层仍必须得到稳定码（用现场重新分类），而不是 CLR 类型名。
        var export = TerminalDiagnosticExport.Build(error, scope, Input());
        scope.Dispose();

        Assert.AreEqual(DiagnosticCauseCode.UnclassifiedTransport, export.CauseCode);
        Assert.IsFalse(string.IsNullOrWhiteSpace(export.Presentation.Title));
        Assert.IsFalse(string.IsNullOrWhiteSpace(export.ReportText));
    }

    [TestMethod]
    public void AttachedCause_SurvivesRethrow()
    {
        var cause = LlmFailureClassifier.Default.Classify(
            FaultScenarios.CreateUploadResetChain(),
            FaultScenarios.IncidentReplica.Context with { PhaseHint = DiagnosticPhaseKind.RequestUpload });

        try
        {
            try
            {
                throw FaultScenarios.CreateUploadResetChain();
            }
            catch (HttpRequestException inner)
            {
                throw LlmFailureDiagnostics.Attach(inner, cause);
            }
        }
        catch (Exception outer)
        {
            Assert.IsTrue(LlmFailureDiagnostics.TryGet(outer, out var roundTripped),
                "因果结论必须随异常穿过边界（Exception.Data）");
            Assert.AreEqual(DiagnosticCauseCode.RequestUploadReset, roundTripped.Code);
            Assert.AreEqual("request_upload", DiagnosticPhases.ToWire(roundTripped.Phase));
        }
    }

    private static (Exception Error, LlmCallDiagnosticsScope Scope) BuildUploadResetError()
    {
        var scope = LlmCallDiagnosticsScope.Begin();
        scope.ProviderId = "deepseek";
        scope.ModelId = "deepseek-flash";
        scope.EndpointHost = "api.deepseek.com";
        scope.Attempt = 2;
        scope.MaxRetries = 2;
        scope.MarkDispatch(1_048_576);

        var error = FaultScenarios.CreateUploadResetChain();
        LlmFailureDiagnostics.Attach(error, LlmFailureDiagnostics.Classify(error, scope));
        return (error, scope);
    }

    private static TerminalDiagnosticExport.Input Input() => new()
    {
        TimestampUtc = Captured,
        SessionId = "206a9b48ec904ebb93e7541131fbb835",
        TurnId = "msg-1791349647230-e7f5o83q",
        MessageId = "msg-1791349647230-e7f5o83q",
        TraceId = "e09ae04599944b8b82166321298225f8",
        ErrorId = "llm-test-error-id",
        WorkspaceId = "default",
        AgentTemplateId = "global:general-assistant",
        ProviderId = "deepseek",
        ModelId = "deepseek-flash",
        EndpointHost = "api.deepseek.com",
        TerminalStatus = "failed",
        Round = 2,
        MaxRounds = 10,
        ConsecutiveFailures = 1,
    };
}
