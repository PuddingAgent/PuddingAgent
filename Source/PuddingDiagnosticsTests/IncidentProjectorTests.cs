using PuddingCode.Diagnostics;

namespace PuddingDiagnosticsTests;

/// <summary>事故投影门禁：一轮 turn 的事实必须能压成「一次事故的答案」（Q1–Q5）。</summary>
[TestClass]
public sealed class IncidentProjectorTests
{
    private const string TurnId = "msg-1791349647230-e7f5o83q";
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 5, 10, 15, TimeSpan.Zero);

    [TestMethod]
    public void Incident_20261007_IsReducedToAttemptsPhaseCauseAndSize()
    {
        var facts = BuildIncidentFacts();

        var view = IncidentProjector.Project(TurnId, facts);

        Assert.AreEqual(TurnId, view.TurnId);
        Assert.AreEqual("failed", view.TerminalStatus);
        Assert.AreEqual(DiagnosticCauseCode.RequestUploadReset, view.RootCauseCode);
        Assert.AreEqual(DiagnosticPhaseKind.RequestUpload, view.Phase);
        Assert.HasCount(4, view.Attempts);

        // 结论句必须自带数字，人不需要再去 grep。
        StringAssert.Contains(view.Conclusion, "4 次尝试");
        StringAssert.Contains(view.Conclusion, DiagnosticCauseCode.RequestUploadReset);
        StringAssert.Contains(view.Conclusion, "上传请求体");
        StringAssert.Contains(view.Conclusion, "1 MiB");
        StringAssert.Contains(view.Conclusion, "10054");

        Assert.IsTrue(view.EvidenceLinks.Any(link => link.StartsWith("errorId=", StringComparison.Ordinal)));
        Assert.IsTrue(view.EvidenceLinks.Any(link => link.Contains(TurnId, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Projector_IsScopedToTheRequestedTurn()
    {
        var facts = BuildIncidentFacts()
            .Concat([Fact(DiagnosticFactKinds.Attempt, "failed", Start.AddHours(1), attempt: 1, turnId: "other-turn")]);

        var view = IncidentProjector.Project(TurnId, facts);

        Assert.HasCount(4, view.Attempts);
    }

    [TestMethod]
    public void Projector_WithoutFacts_ReportsUnknownInsteadOfHealthy()
    {
        var view = IncidentProjector.Project("missing-turn", []);

        Assert.AreEqual("unknown", view.TerminalStatus);
        Assert.IsNull(view.RootCauseCode);
        StringAssert.Contains(view.Conclusion, "未记录到尝试明细");
    }

    [TestMethod]
    public void FactQuery_ClampsLimit_AndReportsTruncationExplicitly()
    {
        var query = new DiagnosticFactQuery { TurnId = TurnId, Limit = 99_999 }.Normalized();
        Assert.AreEqual(DiagnosticFactQuery.MaxLimit, query.Limit);

        var items = Enumerable.Range(0, query.Limit).Select(_ => Fact(DiagnosticFactKinds.Attempt, "failed", Start, 1)).ToArray();
        var page = DiagnosticFactPage.Create(items, query);

        Assert.IsTrue(page.Truncated, "命中 Limit 时必须显式标记截断，而不是静默丢弃");

        var small = DiagnosticFactPage.Create(items.Take(3).ToArray(), new DiagnosticFactQuery { Limit = 10 });
        Assert.IsFalse(small.Truncated);
    }

    private static IReadOnlyList<DiagnosticFact> BuildIncidentFacts()
    {
        var evidence = new DiagnosticEvidenceBuilder()
            .Add(DiagnosticEvidenceKeys.SocketErrorCode, 10054)
            .Add(DiagnosticEvidenceKeys.RequestBytes, 1_048_576)
            .Build();

        return
        [
            Fact(DiagnosticFactKinds.Attempt, "retried", Start, attempt: 1, durationMs: 20_310, bytes: 1_048_576),
            Fact(DiagnosticFactKinds.Attempt, "retried", Start.AddSeconds(1), attempt: 2, durationMs: 54_290, bytes: 1_048_576),
            Fact(DiagnosticFactKinds.Attempt, "retried", Start.AddSeconds(2), attempt: 3, durationMs: 20_093, bytes: 1_048_576),
            Fact(DiagnosticFactKinds.Attempt, "failed", Start.AddSeconds(3), attempt: 4, durationMs: 96_746, bytes: 1_048_576),
            new DiagnosticFact
            {
                Kind = DiagnosticFactKinds.Terminal,
                Status = "failed",
                OccurredAtUtc = Start.AddSeconds(4),
                TurnId = TurnId,
                TraceId = "e09ae04599944b8b82166321298225f8",
                ErrorId = "llm-c198a96c19ec4914b24d1b0fe11f4528",
                CauseCode = DiagnosticCauseCode.RequestUploadReset,
                Phase = DiagnosticPhases.RequestUploadWire,
                MaxRetries = 2,
                RequestBytes = 1_048_576,
                Evidence = evidence,
            },
        ];
    }

    private static DiagnosticFact Fact(
        string kind,
        string status,
        DateTimeOffset occurredAt,
        int attempt,
        long? durationMs = 1000,
        long? bytes = 1_048_576,
        string turnId = TurnId)
        => new()
        {
            Kind = kind,
            Status = status,
            OccurredAtUtc = occurredAt,
            TurnId = turnId,
            Attempt = attempt,
            CauseCode = DiagnosticCauseCode.RequestUploadReset,
            Phase = DiagnosticPhases.RequestUploadWire,
            DurationMs = durationMs,
            RequestBytes = bytes,
        };
}
