using PuddingCode.Diagnostics;

namespace PuddingDiagnosticsTests;

/// <summary>
/// 场景门禁：**「注入的失败 → 期望诊断结论」对照表**（可诊断基础设施设计 §6）。
/// <para>
/// 每条场景都必须产出期望的稳定码、阶段与可重试性；场景表就是人读判读文档的可执行版本。
/// 码表新增取值而不加场景，会被 <see cref="CauseCatalog_EveryCodeHasAScenarioOrIsExplicitlyPassthrough"/>
/// 抓住。
/// </para>
/// </summary>
[TestClass]
public sealed class FaultScenarioTests
{
    [TestMethod]
    public void EveryScenario_ProducesItsExpectedCause()
    {
        var failures = new List<string>();

        foreach (var scenario in FaultScenarios.All)
        {
            var cause = LlmFailureClassifier.Default.Classify(scenario.CreateException(), scenario.Context);

            if (!string.Equals(cause.Code, scenario.ExpectedCauseCode, StringComparison.Ordinal))
                failures.Add($"{scenario.Name}: code={cause.Code} expected={scenario.ExpectedCauseCode}");

            if (cause.Phase != scenario.ExpectedPhase)
                failures.Add($"{scenario.Name}: phase={cause.Phase} expected={scenario.ExpectedPhase}");

            if (cause.Retryable != scenario.ExpectedRetryable)
                failures.Add($"{scenario.Name}: retryable={cause.Retryable} expected={scenario.ExpectedRetryable}");

            if (scenario.ExpectedHttpStatus is { } status
                && cause.Evidence.GetValueOrDefault(DiagnosticEvidenceKeys.HttpStatus) != status.ToString())
            {
                failures.Add($"{scenario.Name}: http_status evidence mismatch");
            }

            if (string.IsNullOrWhiteSpace(cause.UserMessage) || string.IsNullOrWhiteSpace(cause.RemediationHint))
                failures.Add($"{scenario.Name}: user message / remediation hint must never be empty");
        }

        Assert.IsEmpty(failures, "场景与诊断结论不一致：" + string.Join(" | ", failures));
    }

    /// <summary>
    /// 2026-10-07 事故复刻：<c>HttpRequestException("Error while copying content to a stream.")</c>
    /// → <c>IOException</c> → <c>SocketException(10054)</c>，且堆栈含「写请求体」帧。
    /// </summary>
    [TestMethod]
    public void IncidentReplica_ClassifiesAsRequestUploadReset_WithFullEvidence()
    {
        // 真实框架帧（System.Net.Http.HttpContent.&lt;CopyToAsync&gt; 等）无法在测试里伪造，
        // 因此注入本场景工厂方法自身的帧名，用来验证「帧指纹判定」这一机制。
        var classifier = new LlmFailureClassifier([nameof(FaultScenarios.CreateUploadResetChain)]);

        var cause = classifier.Classify(FaultScenarios.CreateUploadResetChain(), FaultScenarios.IncidentReplica.Context);

        Assert.AreEqual(DiagnosticCauseCode.RequestUploadReset, cause.Code);
        Assert.AreEqual(DiagnosticCauseCategory.Transport, cause.Category);
        Assert.AreEqual(DiagnosticPhaseKind.RequestUpload, cause.Phase);
        Assert.IsTrue(cause.Retryable, "未产出增量前的传输重置必须可重试");
        StringAssert.Contains(cause.Rule, "stack_frame:");

        Assert.AreEqual("10054", cause.Evidence[DiagnosticEvidenceKeys.SocketErrorCode]);
        Assert.AreEqual("request_upload", cause.Evidence[DiagnosticEvidenceKeys.Phase]);
        Assert.AreEqual("3", cause.Evidence[DiagnosticEvidenceKeys.Attempt]);
        Assert.AreEqual("1048576", cause.Evidence[DiagnosticEvidenceKeys.RequestBytes]);
        Assert.AreEqual("154", cause.Evidence[DiagnosticEvidenceKeys.MessageCount]);
        Assert.AreEqual("99", cause.Evidence[DiagnosticEvidenceKeys.ToolCount]);
        StringAssert.Contains(cause.Evidence[DiagnosticEvidenceKeys.ExceptionChain], "SocketException");
        StringAssert.Contains(cause.Evidence[DiagnosticEvidenceKeys.ExceptionChain], "Error while copying content to a stream.");
    }

    /// <summary>
    /// 生产指纹锚定：默认判据必须包含 2026-10-07 实测到的框架帧名。
    /// 删掉指纹会让「上传阶段重置」退化成「未定性传输失败」，因此这一条必须有断言守着。
    /// </summary>
    [TestMethod]
    public void DefaultUploadFrameFingerprints_MatchTheObservedProductionStack()
    {
        CollectionAssert.Contains(
            ExceptionChainInspector.DefaultUploadFrames.ToArray(),
            "SendRequestContentAsync",
            "实测帧：System.Net.Http.HttpConnection.SendRequestContentAsync");
        CollectionAssert.Contains(
            ExceptionChainInspector.DefaultUploadFrames.ToArray(),
            "HttpContent.<CopyToAsync>",
            "实测帧：System.Net.Http.HttpContent.<CopyToAsync>g__WaitAsync");
    }

    /// <summary>
    /// 没有阶段指纹、也没收到响应头时的 RST：必须如实标为 transport.unclassified_failure，
    /// 而不是猜成「上传重置」或「读流重置」。
    /// </summary>
    [TestMethod]
    public void ResetWithoutPhaseSignal_StaysUnclassified()
    {
        var cause = LlmFailureClassifier.Default.Classify(
            FaultScenarios.CreateUploadResetChain(),
            FaultScenarios.IncidentReplica.Context with { PhaseHint = DiagnosticPhaseKind.Unknown });

        Assert.AreEqual(DiagnosticCauseCode.UnclassifiedTransport, cause.Code);
        Assert.AreEqual("transport:reset_without_phase_signal", cause.Rule);
        Assert.AreEqual(DiagnosticPhaseKind.AwaitResponseHeaders, cause.Phase);
    }

    /// <summary>
    /// 重试安全边界（Docs/08_how_debuge/06-延迟问题定位.md §7.9）：已产出增量后，
    /// 传输类瞬态失败一律不可重试。
    /// </summary>
    [TestMethod]
    public void Retryable_IsForcedFalse_AfterAnyDeltaHasBeenYielded()
    {
        var classifier = new LlmFailureClassifier([nameof(FaultScenarios.CreateUploadResetChain)]);
        var context = FaultScenarios.IncidentReplica.Context with { HasYieldedDelta = true };

        var cause = classifier.Classify(FaultScenarios.CreateUploadResetChain(), context);

        Assert.AreEqual(DiagnosticCauseCode.RequestUploadReset, cause.Code);
        Assert.IsFalse(cause.Retryable, "产出增量后不得再按传输瞬态重试");
        Assert.AreEqual("false", cause.Evidence[DiagnosticEvidenceKeys.Retryable]);
    }

    /// <summary>码表不得漂移：每个已登记码都能被目录描述，且本测试工程覆盖了非透传码。</summary>
    [TestMethod]
    public void CauseCatalog_EveryCodeHasAScenarioOrIsExplicitlyPassthrough()
    {
        var scenarioCodes = FaultScenarios.All
            .Select(scenario => scenario.ExpectedCauseCode)
            .Append(FaultScenarios.IncidentReplica.ExpectedCauseCode)
            .ToHashSet(StringComparer.Ordinal);

        var missing = DiagnosticCauseCatalog.Codes
            .Where(code => !scenarioCodes.Contains(code))
            .ToArray();

        Assert.IsEmpty(missing, "新增稳定码必须同时补「场景 → 期望码」用例：" + string.Join(", ", missing));

        foreach (var code in DiagnosticCauseCatalog.Codes)
        {
            var descriptor = DiagnosticCauseCatalog.Describe(code);
            Assert.AreEqual(code, descriptor.Code);
            Assert.IsFalse(string.IsNullOrWhiteSpace(descriptor.UserMessage), $"{code} 缺用户消息");
            Assert.IsFalse(string.IsNullOrWhiteSpace(descriptor.RemediationHint), $"{code} 缺处置建议");
            Assert.IsTrue(DiagnosticCauseCode.IsRegistered(code), $"{code} 未登记");
        }
    }
}
