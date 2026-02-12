using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Diagnostics;

/// <summary>
/// Builds a read-only benchmark diagnostics report for one chat session from
/// local runtime evidence: session JSONL, tool approval files, timeline JSONL,
/// and raw session logs.
/// </summary>
public sealed partial class SessionBenchmarkDiagnosticsService
{
    private const int MaxTextLength = 260;

    private readonly PuddingDataPaths _paths;
    private readonly PlatformDbContext? _db;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public SessionBenchmarkDiagnosticsService(PuddingDataPaths paths, PlatformDbContext? db = null)
    {
        _paths = paths;
        _db = db;
    }

    public async Task<SessionBenchmarkReportDto> BuildAsync(
        string sessionId,
        int maxFindings = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id cannot be empty.", nameof(sessionId));

        maxFindings = Math.Clamp(maxFindings, 1, 100);
        var state = new SessionBenchmarkState(sessionId, maxFindings);

        LoadSessionJsonl(state);
        if (!state.HasJsonl)
            await LoadSessionEventLogAsync(state, ct);

        LoadApprovalAudit(state);
        LoadTickets(state);
        LoadTimeline(state);
        LoadSessionLogFindings(state);

        return BuildReport(state);
    }

    private void LoadSessionJsonl(SessionBenchmarkState state)
    {
        var path = Path.Combine(_paths.DataRoot, "jsonl", $"{state.SessionId}.jsonl");
        state.JsonlPath = path;
        if (!File.Exists(path))
            return;

        state.HasJsonl = true;
        var pendingCalls = new Dictionary<string, Queue<SessionBenchmarkToolCallDto>>(StringComparer.Ordinal);

        foreach (var record in ReadJsonlObjects(path))
        {
            var recordType = GetString(record, "type");
            var eventType = GetString(record, "eventType");
            Increment(state.MessageCounts, recordType);
            Increment(state.EventCounts, eventType);

            if (string.Equals(recordType, "assistant", StringComparison.OrdinalIgnoreCase))
                TryUpdateUsage(state, ParseObjectFromString(GetString(record, "usageJson")));

            if (string.Equals(eventType, "done", StringComparison.OrdinalIgnoreCase))
            {
                var data = ParseObjectFromString(GetString(record, "data"));
                if (TryGetProperty(data, "usage", out var usage))
                    TryUpdateUsage(state, usage);
            }

            if (!string.Equals(eventType, "tool_call", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(eventType, "tool_result", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var eventData = ParseObjectFromString(GetString(record, "data"));
            if (eventData.ValueKind != JsonValueKind.Object)
                continue;

            if (string.Equals(eventType, "tool_call", StringComparison.OrdinalIgnoreCase))
            {
                var toolName = GetString(eventData, "name") ?? "";
                var arguments = ParseObjectFromString(GetString(eventData, "arguments"));
                var call = new SessionBenchmarkToolCallDto
                {
                    Seq = GetInt(record, "sequenceNum") ?? 0,
                    Name = toolName,
                    Command = ExtractCommand(arguments),
                    RecordedAt = GetString(record, "recordedAt"),
                };
                state.ToolCalls.Add(call);
                if (!pendingCalls.TryGetValue(toolName, out var queue))
                {
                    queue = new Queue<SessionBenchmarkToolCallDto>();
                    pendingCalls[toolName] = queue;
                }

                queue.Enqueue(call);
                continue;
            }

            var result = new SessionBenchmarkToolResultDto
            {
                Seq = GetInt(record, "sequenceNum") ?? 0,
                Name = GetString(eventData, "name") ?? "",
                ExitCode = GetInt(eventData, "exitCode"),
                RecordedAt = GetString(record, "recordedAt"),
            };
            var rawError = GetString(eventData, "error") ?? "";
            var rawOutput = GetString(eventData, "output") ?? "";
            result.Error = Truncate(rawError);
            result.Output = Truncate(rawOutput);
            result.OutputLineCount = CountLines(rawOutput);
            result.OutputCharCount = rawOutput.Length;
            result.ErrorLineCount = CountLines(rawError);
            result.ErrorCharCount = rawError.Length;
            result.TotalTextLineCount = CountLines(JoinResultText(rawOutput, rawError));
            result.TotalTextCharCount = rawOutput.Length + rawError.Length;
            if (pendingCalls.TryGetValue(result.Name, out var calls) && calls.Count > 0)
            {
                var call = calls.Dequeue();
                result.PairedCommand = call.Command;
                result.DurationMs = BuildDurationMs(call.RecordedAt, result.RecordedAt);
            }

            result.Category = ClassifyFailure(result);
            state.ToolResults.Add(result);
        }
    }

    private void LoadApprovalAudit(SessionBenchmarkState state)
    {
        var path = Path.Combine(_paths.RuntimeRoot, "tool-approval", "audit-events.jsonl");
        if (!File.Exists(path))
            return;

        foreach (var item in ReadJsonlObjects(path))
        {
            if (!string.Equals(GetString(item, "sessionId"), state.SessionId, StringComparison.Ordinal))
                continue;

            state.ApprovalEvents.Add(new SessionBenchmarkApprovalEventDto
            {
                EventType = GetString(item, "eventType") ?? "",
                ToolId = GetString(item, "toolId"),
                Command = Truncate(GetString(item, "command")),
                TicketId = GetString(item, "ticketId"),
                AllowlistRuleId = GetString(item, "allowlistRuleId"),
                Decision = GetString(item, "decision"),
                Reason = Truncate(GetString(item, "reason")),
                CreatedAtUtc = GetString(item, "createdAtUtc"),
            });
        }
    }

    private async Task LoadSessionEventLogAsync(SessionBenchmarkState state, CancellationToken ct)
    {
        if (_db is null)
            return;

        var events = await _db.SessionEventLogs
            .AsNoTracking()
            .Where(evt => evt.SessionId == state.SessionId)
            .OrderBy(evt => evt.SequenceNum)
            .ToListAsync(ct);
        if (events.Count == 0)
            return;

        state.HasSessionEventLog = true;
        var pendingCalls = new Dictionary<string, Queue<SessionBenchmarkToolCallDto>>(StringComparer.Ordinal);

        foreach (var evt in events)
        {
            Increment(state.MessageCounts, "event");
            Increment(state.EventCounts, evt.EventType);

            var eventData = TryParseJsonObject(evt.Data);
            if (eventData is null)
                continue;

            if (string.Equals(evt.EventType, SessionEventTypes.Done, StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetProperty(eventData.Value, "usage", out var usage))
                    TryUpdateUsage(state, usage);
                continue;
            }

            if (string.Equals(evt.EventType, SessionEventTypes.Usage, StringComparison.OrdinalIgnoreCase))
            {
                TryUpdateUsage(state, eventData.Value);
                if (TryGetProperty(eventData.Value, "usage", out var usage))
                    TryUpdateUsage(state, usage);
                continue;
            }

            if (string.Equals(evt.EventType, SessionEventTypes.ToolCall, StringComparison.OrdinalIgnoreCase))
            {
                var toolName = GetString(eventData.Value, "name") ?? "";
                var arguments = ParseObjectFromString(GetString(eventData.Value, "arguments"));
                var call = new SessionBenchmarkToolCallDto
                {
                    Seq = ToIntSequence(evt.SequenceNum),
                    Name = toolName,
                    Command = ExtractCommand(arguments),
                    RecordedAt = evt.RecordedAt,
                };
                state.ToolCalls.Add(call);
                if (!pendingCalls.TryGetValue(toolName, out var queue))
                {
                    queue = new Queue<SessionBenchmarkToolCallDto>();
                    pendingCalls[toolName] = queue;
                }

                queue.Enqueue(call);
                continue;
            }

            if (!string.Equals(evt.EventType, SessionEventTypes.ToolResult, StringComparison.OrdinalIgnoreCase))
                continue;

            var result = new SessionBenchmarkToolResultDto
            {
                Seq = ToIntSequence(evt.SequenceNum),
                Name = GetString(eventData.Value, "name") ?? "",
                ExitCode = GetInt(eventData.Value, "exitCode"),
                RecordedAt = evt.RecordedAt,
            };
            var rawError = GetString(eventData.Value, "error") ?? "";
            var rawOutput = GetString(eventData.Value, "output") ?? "";
            result.Error = Truncate(rawError);
            result.Output = Truncate(rawOutput);
            result.OutputLineCount = CountLines(rawOutput);
            result.OutputCharCount = rawOutput.Length;
            result.ErrorLineCount = CountLines(rawError);
            result.ErrorCharCount = rawError.Length;
            result.TotalTextLineCount = CountLines(JoinResultText(rawOutput, rawError));
            result.TotalTextCharCount = rawOutput.Length + rawError.Length;
            if (pendingCalls.TryGetValue(result.Name, out var calls) && calls.Count > 0)
            {
                var call = calls.Dequeue();
                result.PairedCommand = call.Command;
                result.DurationMs = BuildDurationMs(call.RecordedAt, result.RecordedAt);
            }

            result.Category = ClassifyFailure(result);
            state.ToolResults.Add(result);
        }
    }

    private void LoadTickets(SessionBenchmarkState state)
    {
        var path = Path.Combine(_paths.RuntimeRoot, "tool-approval", "tickets.json");
        if (!File.Exists(path))
            return;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        foreach (var ticket in EnumerateTicketObjects(root))
        {
            if (!TryGetProperty(ticket, "identity", out var identity)
                || !string.Equals(GetString(identity, "sessionId"), state.SessionId, StringComparison.Ordinal))
            {
                continue;
            }

            var request = TryGetProperty(ticket, "request", out var requestElement)
                ? requestElement
                : default;
            state.Tickets.Add(new SessionBenchmarkTicketDto
            {
                TicketId = GetString(ticket, "ticketId") ?? "",
                ToolId = GetString(ticket, "toolId") ?? "",
                Status = GetString(ticket, "status") ?? "",
                Scope = GetString(ticket, "scope") ?? "",
                RemainingUses = GetInt(ticket, "remainingUses"),
                Command = Truncate(DescribeTicketRequest(request)),
            });
        }
    }

    private void LoadTimeline(SessionBenchmarkState state)
    {
        var root = Path.Combine(_paths.DiagnosticsLogsRoot, "session-timeline");
        if (!Directory.Exists(root))
            return;

        var path = Directory
            .EnumerateFiles(root, $"{state.SessionId}.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (path is null)
            return;

        state.TimelinePath = path;
        foreach (var item in ReadJsonlObjects(path))
        {
            Increment(state.TimelineCounts, GetString(item, "status"));
            var status = GetString(item, "status") ?? "";
            var error = GetString(item, "errorMessage");
            if ((status.Equals("failed", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("error", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(error))
                && state.TimelineErrors.Count < state.MaxFindings)
            {
                state.TimelineErrors.Add(new SessionBenchmarkTimelineErrorDto
                {
                    RecordedAtUtc = GetString(item, "recordedAtUtc"),
                    Component = GetString(item, "component"),
                    Operation = GetString(item, "operation"),
                    Status = status,
                    ErrorMessage = Truncate(error),
                });
            }
        }
    }

    private void LoadSessionLogFindings(SessionBenchmarkState state)
    {
        var root = Path.Combine(_paths.SessionLogsRoot, state.SessionId);
        if (!Directory.Exists(root))
            return;

        var path = Directory
            .EnumerateFiles(root, "session-*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (path is null)
            return;

        state.SessionLogPath = path;
        if (!TryReadSharedLines(path, out var lines, out var error))
        {
            state.SessionLogFindings.Add(Truncate($"[WRN] session log unavailable: {error}"));
            return;
        }

        foreach (var line in lines)
        {
            if (!ImportantLogLineRegex().IsMatch(line))
                continue;

            state.SessionLogFindings.Add(Truncate(line));
            if (state.SessionLogFindings.Count >= state.MaxFindings)
                break;
        }
    }

    private SessionBenchmarkReportDto BuildReport(SessionBenchmarkState state)
    {
        var failures = state.ToolResults
            .Where(result => result.ExitCode is not null and not 0)
            .ToList();

        var counts = new SessionBenchmarkCountsDto
        {
            Messages = state.MessageCounts,
            Events = state.EventCounts,
            ToolCalls = CountBy(state.ToolCalls.Select(call => call.Name)),
            ToolResults = CountBy(state.ToolResults.Select(result => result.Name)),
            FailedToolResults = failures.Count,
            ApprovalEvents = CountBy(state.ApprovalEvents.Select(evt => evt.EventType)),
            Tickets = state.Tickets.Count,
            Timeline = state.TimelineCounts,
        };

        var approvalStats = BuildApprovalStats(state);
        var blockingFailureCount = failures.Count(result => result.Category != "expected_failure");
        var friction = BuildFrictionPoints(failures, state.ApprovalEvents, approvalStats, state.SessionLogFindings);
        var scores = BuildScores(counts, friction, blockingFailureCount);

        return new SessionBenchmarkReportDto
        {
            SessionId = state.SessionId,
            HasJsonl = state.HasJsonl,
            HasSessionEventLog = state.HasSessionEventLog,
            HasEvidence = state.HasJsonl
                || state.HasSessionEventLog
                || state.TimelinePath is not null
                || state.SessionLogPath is not null,
            Paths = new SessionBenchmarkPathsDto
            {
                Jsonl = state.HasJsonl ? state.JsonlPath : null,
                Timeline = state.TimelinePath,
                SessionLog = state.SessionLogPath,
            },
            Usage = state.Usage,
            Counts = counts,
            ApprovalStats = approvalStats,
            ToolOutputStats = BuildToolOutputStats(state.ToolResults),
            Failures = failures.Take(state.MaxFindings).ToList(),
            ApprovalTimeline = state.ApprovalEvents.Take(state.MaxFindings * 2).ToList(),
            Tickets = state.Tickets,
            TimelineErrors = state.TimelineErrors,
            SessionLogFindings = state.SessionLogFindings,
            FrictionPoints = friction,
            Scores = scores,
        };
    }

    private static List<SessionBenchmarkFrictionPointDto> BuildFrictionPoints(
        IReadOnlyList<SessionBenchmarkToolResultDto> failures,
        IReadOnlyList<SessionBenchmarkApprovalEventDto> approvalEvents,
        SessionBenchmarkApprovalStatsDto approvalStats,
        IReadOnlyList<string> logFindings)
    {
        var points = new List<SessionBenchmarkFrictionPointDto>();
        var mismatchCount = approvalEvents.Count(evt => evt.EventType == "TicketMismatch");
        if (mismatchCount > 0)
        {
            points.Add(new SessionBenchmarkFrictionPointDto
            {
                Severity = "high",
                Category = "approval_mismatch",
                Evidence = $"{mismatchCount} approval mismatch event(s)",
                Impact = "Agent had to recover by requesting a new explicit approval ticket.",
                Recommendation = "Return structured mismatch details and a suggested approval request payload.",
            });
        }

        if (approvalStats.ExplicitTickets > 0 && approvalStats.ImplicitCoveragePercent < 80)
        {
            points.Add(new SessionBenchmarkFrictionPointDto
            {
                Severity = approvalStats.ImplicitCoveragePercent < 50 ? "high" : "medium",
                Category = "implicit_approval_coverage",
                Evidence = $"implicit coverage {approvalStats.ImplicitCoveragePercent}% ({approvalStats.ImplicitApprovals}/{approvalStats.ApprovalDecisionAttempts})",
                Impact = "The agent still needed explicit approval tickets for work that should often be handled by implicit audit.",
                Recommendation = "Expand implicit approval rules for safe workspace-scoped and read-only tool requests.",
            });
        }

        var runtimeFailures = failures.Count(result => result.Category == "runtime_failure");
        if (runtimeFailures > 0)
        {
            points.Add(new SessionBenchmarkFrictionPointDto
            {
                Severity = "medium",
                Category = "runtime_failure",
                Evidence = $"{runtimeFailures} tool runtime failure result(s)",
                Impact = "The task needed extra verification or fallback commands.",
                Recommendation = "Surface runtime failures separately from approval denials.",
            });
        }

        var environmentFailures = failures.Count(result => result.Category == "environment_failure");
        if (environmentFailures > 0)
        {
            points.Add(new SessionBenchmarkFrictionPointDto
            {
                Severity = "medium",
                Category = "environment_failure",
                Evidence = $"{environmentFailures} environment-related failure result(s)",
                Impact = "The agent encountered host-specific shell or encoding behavior.",
                Recommendation = "Normalize shell process encoding and environment defaults.",
            });
        }

        if (logFindings.Count > 0)
        {
            points.Add(new SessionBenchmarkFrictionPointDto
            {
                Severity = "low",
                Category = "platform_log_noise",
                Evidence = $"{logFindings.Count} warning/error log finding(s)",
                Impact = "Background platform warnings make benchmark diagnosis harder.",
                Recommendation = "Separate task-critical errors from platform background noise.",
            });
        }

        return points;
    }

    private static SessionBenchmarkApprovalStatsDto BuildApprovalStats(SessionBenchmarkState state)
    {
        var implicitApproved = CountApprovalEvents(state.ApprovalEvents, "ImplicitApproved");
        var implicitDenied = CountApprovalEvents(state.ApprovalEvents, "ImplicitDenied");
        var implicitDecisions = implicitApproved + implicitDenied;
        var allowlistHits = CountApprovalEvents(state.ApprovalEvents, "AllowlistHit");
        var explicitTickets = CountApprovalEvents(state.ApprovalEvents, "TicketSubmitted");
        var implicitApprovals = implicitApproved + allowlistHits;
        var decisionAttempts = implicitApprovals + explicitTickets;
        var latencies = BuildImplicitApprovalLatencies(state.ToolCalls, state.ApprovalEvents);

        return new SessionBenchmarkApprovalStatsDto
        {
            ImplicitApproved = implicitApproved,
            ImplicitDenied = implicitDenied,
            ImplicitDecisions = implicitDecisions,
            ImplicitApprovals = implicitApprovals,
            ExplicitTickets = explicitTickets,
            TicketApprovals = CountApprovalEvents(state.ApprovalEvents, "TicketMatched")
                + CountApprovalEvents(state.ApprovalEvents, "TicketConsumed"),
            AllowlistHits = allowlistHits,
            ApprovalMismatches = CountApprovalEvents(state.ApprovalEvents, "TicketMismatch"),
            ApprovalDecisionAttempts = decisionAttempts,
            ImplicitCoveragePercent = decisionAttempts == 0
                ? 0
                : (int)Math.Round(implicitApprovals * 100.0 / decisionAttempts),
            ImplicitLatencySamples = latencies.Count,
            ImplicitLatencyAvgMs = latencies.Count == 0 ? null : (int)Math.Round(latencies.Average()),
            ImplicitLatencyP50Ms = Percentile(latencies, 0.50),
            ImplicitLatencyP95Ms = Percentile(latencies, 0.95),
            ImplicitLatencyMaxMs = latencies.Count == 0 ? null : latencies.Max(),
        };
    }

    private static int CountApprovalEvents(
        IReadOnlyList<SessionBenchmarkApprovalEventDto> approvalEvents,
        string eventType)
        => approvalEvents.Count(evt => string.Equals(evt.EventType, eventType, StringComparison.OrdinalIgnoreCase));

    private static List<int> BuildImplicitApprovalLatencies(
        IReadOnlyList<SessionBenchmarkToolCallDto> toolCalls,
        IReadOnlyList<SessionBenchmarkApprovalEventDto> approvalEvents)
    {
        var parsedCalls = toolCalls
            .Select(call => new ParsedToolCall(call, TryParseDateTimeOffset(call.RecordedAt)))
            .Where(call => call.RecordedAt is not null)
            .OrderBy(call => call.RecordedAt)
            .ToList();
        if (parsedCalls.Count == 0)
            return [];

        var latencies = new List<int>();
        foreach (var approvalEvent in approvalEvents)
        {
            if (!string.Equals(approvalEvent.EventType, "ImplicitApproved", StringComparison.OrdinalIgnoreCase))
                continue;

            var approvalTime = TryParseDateTimeOffset(approvalEvent.CreatedAtUtc);
            if (approvalTime is null)
                continue;

            var call = FindMatchingToolCall(parsedCalls, approvalEvent, approvalTime.Value);
            if (call is null)
                continue;

            var latency = approvalTime.Value - call.RecordedAt!.Value;
            if (latency < TimeSpan.Zero || latency > TimeSpan.FromMinutes(5))
                continue;

            latencies.Add((int)Math.Round(latency.TotalMilliseconds));
        }

        latencies.Sort();
        return latencies;
    }

    private static ParsedToolCall? FindMatchingToolCall(
        IReadOnlyList<ParsedToolCall> parsedCalls,
        SessionBenchmarkApprovalEventDto approvalEvent,
        DateTimeOffset approvalTime)
    {
        var exactMatch = parsedCalls
            .Where(call => IsSameTool(call.Value, approvalEvent)
                && call.RecordedAt <= approvalTime
                && !string.IsNullOrWhiteSpace(approvalEvent.Command)
                && string.Equals(call.Value.Command, approvalEvent.Command, StringComparison.Ordinal))
            .OrderByDescending(call => call.RecordedAt)
            .FirstOrDefault();
        if (exactMatch is not null)
            return exactMatch;

        return parsedCalls
            .Where(call => IsSameTool(call.Value, approvalEvent) && call.RecordedAt <= approvalTime)
            .OrderByDescending(call => call.RecordedAt)
            .FirstOrDefault();
    }

    private static bool IsSameTool(SessionBenchmarkToolCallDto call, SessionBenchmarkApprovalEventDto approvalEvent)
        => string.IsNullOrWhiteSpace(approvalEvent.ToolId)
            || string.Equals(call.Name, approvalEvent.ToolId, StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? TryParseDateTimeOffset(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static int? Percentile(IReadOnlyList<int> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
            return null;

        var index = (int)Math.Ceiling(percentile * sortedValues.Count) - 1;
        return sortedValues[Math.Clamp(index, 0, sortedValues.Count - 1)];
    }

    private static IReadOnlyList<SessionBenchmarkToolOutputStatsDto> BuildToolOutputStats(
        IReadOnlyList<SessionBenchmarkToolResultDto> results)
        => results
            .GroupBy(result => result.Name, StringComparer.Ordinal)
            .Select(group =>
            {
                var durations = group
                    .Select(result => result.DurationMs)
                    .Where(duration => duration is not null)
                    .Select(duration => duration!.Value)
                    .Order()
                    .ToList();
                var totalTextChars = group.Select(result => result.TotalTextCharCount).ToList();
                var resultCount = group.Count();

                return new SessionBenchmarkToolOutputStatsDto
                {
                    ToolName = group.Key,
                    ResultCount = resultCount,
                    OutputLineTotal = group.Sum(result => result.OutputLineCount),
                    OutputCharTotal = group.Sum(result => result.OutputCharCount),
                    ErrorLineTotal = group.Sum(result => result.ErrorLineCount),
                    ErrorCharTotal = group.Sum(result => result.ErrorCharCount),
                    TotalTextLineTotal = group.Sum(result => result.TotalTextLineCount),
                    TotalTextCharTotal = group.Sum(result => result.TotalTextCharCount),
                    MaxTotalTextCharCount = totalTextChars.Count == 0 ? 0 : totalTextChars.Max(),
                    AvgTotalTextCharCount = resultCount == 0
                        ? 0
                        : (int)Math.Round(totalTextChars.Sum() * 1.0 / resultCount),
                    DurationSamples = durations.Count,
                    DurationAvgMs = durations.Count == 0 ? null : (int)Math.Round(durations.Average()),
                    DurationP50Ms = Percentile(durations, 0.50),
                    DurationP95Ms = Percentile(durations, 0.95),
                    DurationMaxMs = durations.Count == 0 ? null : durations.Max(),
                };
            })
            .OrderByDescending(item => item.TotalTextCharTotal)
            .ThenByDescending(item => item.DurationMaxMs ?? 0)
            .ThenBy(item => item.ToolName, StringComparer.Ordinal)
            .ToList();

    private static SessionBenchmarkScoresDto BuildScores(
        SessionBenchmarkCountsDto counts,
        IReadOnlyList<SessionBenchmarkFrictionPointDto> friction,
        int blockingFailureCount)
    {
        var mismatchCount = counts.ApprovalEvents.GetValueOrDefault("TicketMismatch");
        var completion = Math.Clamp(100 - blockingFailureCount * 8, 0, 100);
        var toolExecution = Math.Clamp(100 - blockingFailureCount * 12, 0, 100);
        var approvalFlow = Math.Clamp(100 - mismatchCount * 16 - counts.ApprovalEvents.GetValueOrDefault("TicketSubmitted") * 2, 0, 100);
        var diagnosability = counts.ToolCalls.Count > 0 || counts.ApprovalEvents.Count > 0 ? 86 : 50;
        if (friction.Any(point => point.Category == "platform_log_noise"))
            diagnosability -= 4;
        var governance = counts.ApprovalEvents.Count > 0 ? 84 : 70;

        var overall = (int)Math.Round(
            completion * 0.25
            + toolExecution * 0.20
            + approvalFlow * 0.25
            + diagnosability * 0.20
            + governance * 0.10);

        return new SessionBenchmarkScoresDto
        {
            Completion = completion,
            ToolExecution = toolExecution,
            ApprovalFlow = approvalFlow,
            Diagnosability = Math.Clamp(diagnosability, 0, 100),
            Governance = governance,
            Overall = Math.Clamp(overall, 0, 100),
            Grade = Grade(overall),
        };
    }

    private static string Grade(int overall)
        => overall >= 90 ? "A"
            : overall >= 80 ? "B"
            : overall >= 70 ? "B-"
            : overall >= 60 ? "C"
            : "D";

    private static string ClassifyFailure(SessionBenchmarkToolResultDto result)
    {
        if (result.ExitCode is null or 0)
            return "success";

        var text = $"{result.Error}\n{result.Output}";
        if (text.Contains("TicketMismatch", StringComparison.OrdinalIgnoreCase)
            || text.Contains("does not match the actual arguments", StringComparison.OrdinalIgnoreCase))
        {
            return "approval_mismatch";
        }

        if (text.Contains("approval required", StringComparison.OrdinalIgnoreCase))
            return "approval_denied";

        if (text.Contains("UnicodeEncodeError", StringComparison.OrdinalIgnoreCase)
            || text.Contains("gbk", StringComparison.OrdinalIgnoreCase))
        {
            return "environment_failure";
        }

        if (IsExpectedNegativeCheck(result, text))
            return "expected_failure";

        return "runtime_failure";
    }

    private static bool IsExpectedNegativeCheck(SessionBenchmarkToolResultDto result, string text)
    {
        var command = result.PairedCommand ?? "";
        var commandLooksIntentional = command.Contains("nonexistent", StringComparison.OrdinalIgnoreCase)
            || command.Contains("missing", StringComparison.OrdinalIgnoreCase)
            || command.Contains("EXIT_CODE", StringComparison.OrdinalIgnoreCase);
        if (!commandLooksIntentional)
            return false;

        return text.Contains("路径不存在", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || text.Contains("file not found", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractCommand(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var key in new[] { "command", "cmd", "input", "command_name" })
        {
            var value = GetString(arguments, key);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        var nested = ParseObjectFromString(GetString(arguments, "requested_arguments_json"));
        return nested.ValueKind == JsonValueKind.Object ? ExtractCommand(nested) : null;
    }

    private static string DescribeTicketRequest(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object)
            return "";

        var requestedArguments = ParseObjectFromString(GetString(request, "requestedArgumentsJson"));
        var command = ExtractCommand(requestedArguments);
        if (!string.IsNullOrWhiteSpace(command))
            return command;

        if (requestedArguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "path", "file", "target", "url" })
            {
                var value = GetString(requestedArguments, key);
                if (!string.IsNullOrWhiteSpace(value))
                    return $"{key}={value}";
            }
        }

        var commandName = GetString(request, "commandName");
        if (!string.IsNullOrWhiteSpace(commandName))
            return commandName;

        return "";
    }

    private static IReadOnlyDictionary<string, int> CountBy(IEnumerable<string?> values)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in values)
            Increment(result, value);
        return result;
    }

    private static void Increment(Dictionary<string, int> counts, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        counts[key] = counts.GetValueOrDefault(key) + 1;
    }

    private static IEnumerable<JsonElement> EnumerateTicketObjects(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    yield return item;
            }

            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        if (TryGetProperty(root, "items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    yield return item;
            }

            yield break;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
                yield return property.Value;
        }
    }

    private static IEnumerable<JsonElement> ReadJsonlObjects(string path)
    {
        if (!TryReadSharedLines(path, out var lines, out _))
            yield break;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var item = TryParseJsonObject(line);
            if (item is not null)
                yield return item.Value;
        }
    }

    private static bool TryReadSharedLines(string path, out IReadOnlyList<string> lines, out string? error)
    {
        lines = [];
        error = null;

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var result = new List<string>();
            while (reader.ReadLine() is { } line)
                result.Add(line);

            lines = result;
            return true;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static JsonElement? TryParseJsonObject(string value)
    {
        try
        {
            using var doc = JsonDocument.Parse(value);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.Clone()
                : null;
        }
        catch (JsonException)
        {
            // Diagnostics must be best-effort; malformed lines should not hide later evidence.
            return null;
        }
    }

    private static JsonElement ParseObjectFromString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return default;

        try
        {
            using var doc = JsonDocument.Parse(value);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static void TryUpdateUsage(SessionBenchmarkState state, JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object)
            return;

        state.Usage = new SessionBenchmarkUsageDto
        {
            PromptTokens = GetLong(usage, "promptTokens") ?? GetLong(usage, "PromptTokens"),
            CompletionTokens = GetLong(usage, "completionTokens") ?? GetLong(usage, "CompletionTokens"),
            TotalTokens = GetLong(usage, "totalTokens") ?? GetLong(usage, "TotalTokens"),
            ContextWindowTokens = GetLong(usage, "contextWindowTokens") ?? GetLong(usage, "ContextWindowTokens"),
            PromptCacheHitTokens = GetLong(usage, "promptCacheHitTokens") ?? GetLong(usage, "PromptCacheHitTokens"),
            PromptCacheMissTokens = GetLong(usage, "promptCacheMissTokens") ?? GetLong(usage, "PromptCacheMissTokens"),
        };
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (element.TryGetProperty(name, out value))
            return true;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static long? GetLong(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        return long.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var text = value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Trim();
        return text.Length <= MaxTextLength ? text : text[..MaxTextLength] + "...";
    }

    private static int CountLines(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        var count = 1;
        foreach (var ch in value)
        {
            if (ch == '\n')
                count++;
        }

        return count;
    }

    private static string JoinResultText(string output, string error)
    {
        if (string.IsNullOrEmpty(output))
            return error;
        if (string.IsNullOrEmpty(error))
            return output;
        return $"{output}\n{error}";
    }

    private static int? BuildDurationMs(string? callRecordedAt, string? resultRecordedAt)
    {
        var callTime = TryParseDateTimeOffset(callRecordedAt);
        var resultTime = TryParseDateTimeOffset(resultRecordedAt);
        if (callTime is null || resultTime is null)
            return null;

        var duration = resultTime.Value - callTime.Value;
        if (duration < TimeSpan.Zero || duration > TimeSpan.FromHours(1))
            return null;

        return (int)Math.Round(duration.TotalMilliseconds);
    }

    private static int ToIntSequence(long sequenceNum)
    {
        if (sequenceNum > int.MaxValue)
            return int.MaxValue;
        if (sequenceNum < int.MinValue)
            return int.MinValue;
        return (int)sequenceNum;
    }

    [GeneratedRegex(@"\[(ERR|WRN)\]|approval required|TicketMismatch|UnicodeEncodeError", RegexOptions.IgnoreCase)]
    private static partial Regex ImportantLogLineRegex();

    private sealed record ParsedToolCall(SessionBenchmarkToolCallDto Value, DateTimeOffset? RecordedAt);

    private sealed class SessionBenchmarkState(string sessionId, int maxFindings)
    {
        public string SessionId { get; } = sessionId;
        public int MaxFindings { get; } = maxFindings;
        public bool HasJsonl { get; set; }
        public bool HasSessionEventLog { get; set; }
        public string? JsonlPath { get; set; }
        public string? TimelinePath { get; set; }
        public string? SessionLogPath { get; set; }
        public Dictionary<string, int> MessageCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> EventCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> TimelineCounts { get; } = new(StringComparer.Ordinal);
        public List<SessionBenchmarkToolCallDto> ToolCalls { get; } = new();
        public List<SessionBenchmarkToolResultDto> ToolResults { get; } = new();
        public List<SessionBenchmarkApprovalEventDto> ApprovalEvents { get; } = new();
        public List<SessionBenchmarkTicketDto> Tickets { get; } = new();
        public List<SessionBenchmarkTimelineErrorDto> TimelineErrors { get; } = new();
        public List<string> SessionLogFindings { get; } = new();
        public SessionBenchmarkUsageDto Usage { get; set; } = new();
    }
}

public sealed record SessionBenchmarkReportDto
{
    public required string SessionId { get; init; }
    public bool HasJsonl { get; init; }
    public bool HasSessionEventLog { get; init; }
    public bool HasEvidence { get; init; }
    public required SessionBenchmarkPathsDto Paths { get; init; }
    public required SessionBenchmarkUsageDto Usage { get; init; }
    public required SessionBenchmarkCountsDto Counts { get; init; }
    public required SessionBenchmarkApprovalStatsDto ApprovalStats { get; init; }
    public IReadOnlyList<SessionBenchmarkToolOutputStatsDto> ToolOutputStats { get; init; } = [];
    public IReadOnlyList<SessionBenchmarkToolResultDto> Failures { get; init; } = [];
    public IReadOnlyList<SessionBenchmarkApprovalEventDto> ApprovalTimeline { get; init; } = [];
    public IReadOnlyList<SessionBenchmarkTicketDto> Tickets { get; init; } = [];
    public IReadOnlyList<SessionBenchmarkTimelineErrorDto> TimelineErrors { get; init; } = [];
    public IReadOnlyList<string> SessionLogFindings { get; init; } = [];
    public IReadOnlyList<SessionBenchmarkFrictionPointDto> FrictionPoints { get; init; } = [];
    public required SessionBenchmarkScoresDto Scores { get; init; }
}

public sealed record SessionBenchmarkPathsDto
{
    public string? Jsonl { get; init; }
    public string? Timeline { get; init; }
    public string? SessionLog { get; init; }
}

public sealed record SessionBenchmarkUsageDto
{
    public long? PromptTokens { get; init; }
    public long? CompletionTokens { get; init; }
    public long? TotalTokens { get; init; }
    public long? ContextWindowTokens { get; init; }
    public long? PromptCacheHitTokens { get; init; }
    public long? PromptCacheMissTokens { get; init; }
}

public sealed record SessionBenchmarkCountsDto
{
    public IReadOnlyDictionary<string, int> Messages { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> Events { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> ToolCalls { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> ToolResults { get; init; } = new Dictionary<string, int>();
    public int FailedToolResults { get; init; }
    public IReadOnlyDictionary<string, int> ApprovalEvents { get; init; } = new Dictionary<string, int>();
    public int Tickets { get; init; }
    public IReadOnlyDictionary<string, int> Timeline { get; init; } = new Dictionary<string, int>();
}

public sealed record SessionBenchmarkApprovalStatsDto
{
    public int ImplicitApproved { get; init; }
    public int ImplicitDenied { get; init; }
    public int ImplicitDecisions { get; init; }
    public int ImplicitApprovals { get; init; }
    public int ExplicitTickets { get; init; }
    public int TicketApprovals { get; init; }
    public int AllowlistHits { get; init; }
    public int ApprovalMismatches { get; init; }
    public int ApprovalDecisionAttempts { get; init; }
    public int ImplicitCoveragePercent { get; init; }
    public int ImplicitLatencySamples { get; init; }
    public int? ImplicitLatencyAvgMs { get; init; }
    public int? ImplicitLatencyP50Ms { get; init; }
    public int? ImplicitLatencyP95Ms { get; init; }
    public int? ImplicitLatencyMaxMs { get; init; }
}

public sealed record SessionBenchmarkToolCallDto
{
    public int Seq { get; init; }
    public string Name { get; init; } = "";
    public string? Command { get; init; }
    public string? RecordedAt { get; init; }
}

public sealed record SessionBenchmarkToolResultDto
{
    public int Seq { get; init; }
    public string Name { get; init; } = "";
    public int? ExitCode { get; init; }
    public string Error { get; set; } = "";
    public string Output { get; set; } = "";
    public string? RecordedAt { get; init; }
    public string? PairedCommand { get; set; }
    public string Category { get; set; } = "success";
    public int? DurationMs { get; set; }
    public int OutputLineCount { get; set; }
    public int OutputCharCount { get; set; }
    public int ErrorLineCount { get; set; }
    public int ErrorCharCount { get; set; }
    public int TotalTextLineCount { get; set; }
    public int TotalTextCharCount { get; set; }
}

public sealed record SessionBenchmarkToolOutputStatsDto
{
    public string ToolName { get; init; } = "";
    public int ResultCount { get; init; }
    public int OutputLineTotal { get; init; }
    public int OutputCharTotal { get; init; }
    public int ErrorLineTotal { get; init; }
    public int ErrorCharTotal { get; init; }
    public int TotalTextLineTotal { get; init; }
    public int TotalTextCharTotal { get; init; }
    public int MaxTotalTextCharCount { get; init; }
    public int AvgTotalTextCharCount { get; init; }
    public int DurationSamples { get; init; }
    public int? DurationAvgMs { get; init; }
    public int? DurationP50Ms { get; init; }
    public int? DurationP95Ms { get; init; }
    public int? DurationMaxMs { get; init; }
}

public sealed record SessionBenchmarkApprovalEventDto
{
    public string EventType { get; init; } = "";
    public string? ToolId { get; init; }
    public string Command { get; init; } = "";
    public string? TicketId { get; init; }
    public string? AllowlistRuleId { get; init; }
    public string? Decision { get; init; }
    public string Reason { get; init; } = "";
    public string? CreatedAtUtc { get; init; }
}

public sealed record SessionBenchmarkTicketDto
{
    public string TicketId { get; init; } = "";
    public string ToolId { get; init; } = "";
    public string Status { get; init; } = "";
    public string Scope { get; init; } = "";
    public int? RemainingUses { get; init; }
    public string Command { get; init; } = "";
}

public sealed record SessionBenchmarkTimelineErrorDto
{
    public string? RecordedAtUtc { get; init; }
    public string? Component { get; init; }
    public string? Operation { get; init; }
    public string? Status { get; init; }
    public string ErrorMessage { get; init; } = "";
}

public sealed record SessionBenchmarkFrictionPointDto
{
    public string Severity { get; init; } = "";
    public string Category { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string Impact { get; init; } = "";
    public string Recommendation { get; init; } = "";
}

public sealed record SessionBenchmarkScoresDto
{
    public int Completion { get; init; }
    public int ToolExecution { get; init; }
    public int ApprovalFlow { get; init; }
    public int Diagnosability { get; init; }
    public int Governance { get; init; }
    public int Overall { get; init; }
    public string Grade { get; init; } = "";
}
