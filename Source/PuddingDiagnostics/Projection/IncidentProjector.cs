using System.Globalization;

namespace PuddingCode.Diagnostics;

/// <summary>
/// 事故投影：把一堆诊断事实压成**一次事故的答案**（可诊断基础设施设计 §5.5）。
/// <para>
/// 纯函数、确定性：同输入同输出。它回答的是
/// 「这一轮到底发生了什么、几次尝试、卡在哪一步、为什么、怎么避免」，
/// 而不是把原始事实再抛给下一个人去 grep。
/// </para>
/// </summary>
public static class IncidentProjector
{
    /// <summary>按 turn 投影；<paramref name="turnId"/> 为空时对全部输入事实投影。</summary>
    public static IncidentView Project(string? turnId, IEnumerable<DiagnosticFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var scoped = facts
            .Where(fact => string.IsNullOrWhiteSpace(turnId)
                           || string.Equals(fact.TurnId, turnId, StringComparison.Ordinal))
            .OrderBy(fact => fact.OccurredAtUtc)
            .ThenBy(fact => fact.Attempt ?? 0)
            .ToArray();

        var attempts = scoped
            .Where(fact => string.Equals(fact.Kind, DiagnosticFactKinds.Attempt, StringComparison.Ordinal))
            .Select(fact => new IncidentAttempt(
                fact.Attempt,
                fact.Status,
                fact.CauseCode,
                fact.Phase,
                fact.DurationMs,
                fact.RequestBytes,
                fact.OccurredAtUtc))
            .ToArray();

        var terminal = scoped.LastOrDefault(fact => string.Equals(fact.Kind, DiagnosticFactKinds.Terminal, StringComparison.Ordinal));
        var rootFact = terminal?.CauseCode is not null
            ? terminal
            : scoped.LastOrDefault(fact => fact.CauseCode is not null);

        var rootCode = rootFact?.CauseCode;
        var descriptor = rootCode is null ? null : DiagnosticCauseCatalog.Describe(rootCode);
        var phase = rootFact is not null ? DiagnosticPhases.FromWire(rootFact.Phase) : DiagnosticPhaseKind.Unknown;

        return new IncidentView
        {
            TurnId = turnId ?? terminal?.TurnId ?? string.Empty,
            TerminalStatus = terminal?.Status ?? attempts.LastOrDefault()?.Status ?? "unknown",
            TraceId = terminal?.TraceId ?? scoped.FirstOrDefault()?.TraceId,
            ErrorId = terminal?.ErrorId ?? scoped.LastOrDefault(fact => fact.ErrorId is not null)?.ErrorId,
            RootCauseCode = rootCode,
            RootCauseMessage = rootFact?.UserMessage ?? descriptor?.UserMessage,
            RemediationHint = rootFact?.RemediationHint ?? descriptor?.RemediationHint,
            Phase = phase,
            MaxRetries = rootFact?.MaxRetries ?? terminal?.MaxRetries,
            Attempts = attempts,
            Conclusion = BuildConclusion(attempts, rootCode, phase, rootFact),
            EvidenceLinks = BuildEvidenceLinks(turnId, terminal, attempts),
        };
    }

    private static string BuildConclusion(
        IReadOnlyList<IncidentAttempt> attempts,
        string? rootCode,
        DiagnosticPhaseKind phase,
        DiagnosticFact? rootFact)
    {
        var parts = new List<string>();

        var attemptCount = attempts.Count;
        if (attemptCount > 0)
        {
            var failed = attempts.Count(attempt => string.Equals(attempt.Status, "failed", StringComparison.Ordinal));
            var retried = attempts.Count(attempt => string.Equals(attempt.Status, "retried", StringComparison.Ordinal));
            parts.Add($"共 {attemptCount} 次尝试（失败 {failed}，触发重试 {retried}）");
        }
        else
        {
            parts.Add("未记录到尝试明细");
        }

        parts.Add(rootCode is null ? "根因未定性" : $"根因 {rootCode}");
        parts.Add($"阶段「{DiagnosticPhases.Label(phase)}」");

        var requestBytes = rootFact?.RequestBytes ?? attempts.LastOrDefault(attempt => attempt.RequestBytes is not null)?.RequestBytes;
        if (requestBytes is not null)
            parts.Add($"请求体 {DiagnosticFormatting.Bytes(requestBytes)}");

        var socketCode = rootFact?.Evidence is not null
                         && rootFact.Evidence.TryGetValue(DiagnosticEvidenceKeys.SocketErrorCode, out var socket)
            ? socket
            : null;
        if (!string.IsNullOrWhiteSpace(socketCode))
            parts.Add($"socket={socketCode}");

        var duration = attempts.LastOrDefault()?.DurationMs;
        if (duration is not null)
            parts.Add($"末次耗时 {DiagnosticFormatting.Duration(duration)}");

        var conclusion = string.Join("；", parts) + "。";

        var message = rootFact?.UserMessage;
        return string.IsNullOrWhiteSpace(message) ? conclusion : $"{conclusion} {message}";
    }

    private static IReadOnlyList<string> BuildEvidenceLinks(
        string? turnId,
        DiagnosticFact? terminal,
        IReadOnlyList<IncidentAttempt> attempts)
    {
        var links = new List<string>();

        if (!string.IsNullOrWhiteSpace(terminal?.ErrorId))
            links.Add($"errorId={terminal.ErrorId}");
        if (!string.IsNullOrWhiteSpace(terminal?.TraceId))
            links.Add($"traceId={terminal.TraceId}");
        if (!string.IsNullOrWhiteSpace(turnId))
            links.Add($"turnId={turnId}");

        links.Add($"attempts={attempts.Count.ToString(CultureInfo.InvariantCulture)}");
        return links;
    }
}
