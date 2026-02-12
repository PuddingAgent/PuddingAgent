using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Diagnostics;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class SessionBenchmarkDiagnosticsServiceTests
{
    [TestMethod]
    public async Task BuildAsync_AggregatesSessionLogsApprovalTicketsAndScores()
    {
        var root = CreateTempRoot();
        var sessionId = "session-1";
        WriteSessionJsonl(root, sessionId);
        WriteApprovalAudit(root, sessionId);
        WriteTickets(root, sessionId);
        WriteTimeline(root, sessionId);
        WriteSessionLog(root, sessionId);

        var service = new SessionBenchmarkDiagnosticsService(PuddingDataPaths.FromRoot(root));

        var report = await service.BuildAsync(sessionId);

        Assert.IsTrue(report.HasJsonl);
        Assert.IsTrue(report.HasEvidence);
        Assert.AreEqual(2, report.Counts.ToolCalls["shell"]);
        Assert.AreEqual(1, report.Counts.ToolResults["shell"]);
        Assert.AreEqual(1, report.Counts.FailedToolResults);
        Assert.AreEqual(1, report.Counts.ApprovalEvents["TicketMismatch"]);
        Assert.AreEqual(1, report.Counts.ApprovalEvents["TicketSubmitted"]);
        Assert.AreEqual(1, report.Counts.ApprovalEvents["ImplicitApproved"]);
        Assert.AreEqual(1, report.ApprovalStats.ImplicitApproved);
        Assert.AreEqual(0, report.ApprovalStats.ImplicitDenied);
        Assert.AreEqual(1, report.ApprovalStats.ExplicitTickets);
        Assert.AreEqual(1, report.ApprovalStats.ImplicitDecisions);
        Assert.AreEqual(1, report.ApprovalStats.ImplicitApprovals);
        Assert.AreEqual(50, report.ApprovalStats.ImplicitCoveragePercent);
        Assert.AreEqual(1, report.ApprovalStats.ImplicitLatencySamples);
        Assert.AreEqual(250, report.ApprovalStats.ImplicitLatencyAvgMs);
        Assert.AreEqual(250, report.ApprovalStats.ImplicitLatencyP95Ms);
        Assert.AreEqual(1, report.ToolOutputStats.Single(stat => stat.ToolName == "shell").ResultCount);
        Assert.AreEqual(1, report.ToolOutputStats.Single(stat => stat.ToolName == "shell").OutputLineTotal);
        Assert.AreEqual(9, report.ToolOutputStats.Single(stat => stat.ToolName == "shell").OutputCharTotal);
        Assert.AreEqual(1, report.ToolOutputStats.Single(stat => stat.ToolName == "shell").ErrorLineTotal);
        Assert.AreEqual(11, report.ToolOutputStats.Single(stat => stat.ToolName == "shell").ErrorCharTotal);
        Assert.AreEqual(1000, report.ToolOutputStats.Single(stat => stat.ToolName == "shell").DurationAvgMs);
        Assert.AreEqual(1, report.Counts.Tickets);
        Assert.AreEqual(12, report.Usage.TotalTokens);
        Assert.AreEqual("runtime_failure", report.Failures.Single().Category);
        Assert.AreEqual(1000, report.Failures.Single().DurationMs);
        Assert.AreEqual(20, report.Failures.Single().TotalTextCharCount);
        Assert.IsTrue(report.FrictionPoints.Any(point => point.Category == "approval_mismatch"));
        Assert.IsTrue(report.FrictionPoints.Any(point => point.Category == "implicit_approval_coverage"));
        Assert.IsTrue(report.FrictionPoints.Any(point => point.Category == "runtime_failure"));
        Assert.IsTrue(report.Scores.Overall < 100);
        Assert.AreEqual("B", report.Scores.Grade);
        Assert.IsTrue(report.SessionLogFindings.Any(line => line.Contains("approval required", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task BuildAsync_UsesSessionEventLogWhenLegacyJsonlMissing()
    {
        var root = CreateTempRoot();
        var sessionId = "session-event-log-only";
        WriteTimeline(root, sessionId);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.SessionEventLogs.AddRange(
            new SessionEventLogEntity
            {
                SessionId = sessionId,
                WorkspaceId = "default",
                SequenceNum = 1,
                EventType = SessionEventTypes.ToolCall,
                Data = JsonSerializer.Serialize(new
                {
                    name = "shell",
                    arguments = JsonSerializer.Serialize(new { command = "python app.py" }),
                }),
                RecordedAt = "2026-06-05T00:00:00+00:00",
            },
            new SessionEventLogEntity
            {
                SessionId = sessionId,
                WorkspaceId = "default",
                SequenceNum = 2,
                EventType = SessionEventTypes.ToolResult,
                Data = JsonSerializer.Serialize(new
                {
                    name = "shell",
                    exitCode = 1,
                    error = "exit code 1",
                    output = "Traceback",
                }),
                RecordedAt = "2026-06-05T00:00:01+00:00",
            },
            new SessionEventLogEntity
            {
                SessionId = sessionId,
                WorkspaceId = "default",
                SequenceNum = 3,
                EventType = SessionEventTypes.Usage,
                Data = JsonSerializer.Serialize(new
                {
                    PromptTokens = 10,
                    CompletionTokens = 2,
                    TotalTokens = 12,
                }),
                RecordedAt = "2026-06-05T00:00:02+00:00",
            });
        await db.SaveChangesAsync();

        var service = new SessionBenchmarkDiagnosticsService(PuddingDataPaths.FromRoot(root), db);

        var report = await service.BuildAsync(sessionId);

        Assert.IsFalse(report.HasJsonl);
        Assert.IsTrue(report.HasSessionEventLog);
        Assert.IsTrue(report.HasEvidence);
        Assert.AreEqual(1, report.Counts.ToolCalls["shell"]);
        Assert.AreEqual(1, report.Counts.ToolResults["shell"]);
        Assert.AreEqual(1, report.Counts.FailedToolResults);
        Assert.AreEqual(12, report.Usage.TotalTokens);
        Assert.AreEqual("runtime_failure", report.Failures.Single().Category);
        Assert.AreEqual(1000, report.Failures.Single().DurationMs);
        Assert.IsNotNull(report.Paths.Timeline);
    }

    [TestMethod]
    public async Task BuildAsync_ReadsSessionLogWhileWriterAllowsReaders()
    {
        var root = CreateTempRoot();
        var sessionId = "session-live-log";
        WriteSessionJsonl(root, sessionId);
        WriteSessionLog(root, sessionId);
        var logPath = Path.Combine(root, "logs", "sessions", sessionId, "session-20260605.log");
        await using var writer = new FileStream(logPath, FileMode.Open, FileAccess.Write, FileShare.Read);

        var service = new SessionBenchmarkDiagnosticsService(PuddingDataPaths.FromRoot(root));

        var report = await service.BuildAsync(sessionId);

        Assert.IsTrue(report.SessionLogFindings.Any(line => line.Contains("approval required", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task BuildAsync_Classifies_Negative_Error_Check_As_Expected_Failure()
    {
        var root = CreateTempRoot();
        var sessionId = "session-negative-check";
        var path = Path.Combine(root, "jsonl", $"{sessionId}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var records = new object[]
        {
            new
            {
                type = "event",
                eventType = "tool_call",
                data = JsonSerializer.Serialize(new
                {
                    name = "shell",
                    arguments = JsonSerializer.Serialize(new { command = "python png2jpg.py nonexistent.png 2>&1; echo \"EXIT_CODE=$?\"" }),
                }),
                sequenceNum = 1,
                recordedAt = "2026-06-05T00:00:00+00:00",
            },
            new
            {
                type = "event",
                eventType = "tool_result",
                data = JsonSerializer.Serialize(new
                {
                    name = "shell",
                    exitCode = 1,
                    error = "exit code 1",
                    output = "错误: 路径不存在: nonexistent.png",
                }),
                sequenceNum = 2,
                recordedAt = "2026-06-05T00:00:01+00:00",
            },
        };
        File.WriteAllLines(path, records.Select(record => JsonSerializer.Serialize(record)));

        var service = new SessionBenchmarkDiagnosticsService(PuddingDataPaths.FromRoot(root));

        var report = await service.BuildAsync(sessionId);

        Assert.AreEqual(1, report.Counts.FailedToolResults);
        Assert.AreEqual("expected_failure", report.Failures.Single().Category);
        Assert.IsFalse(report.FrictionPoints.Any(point => point.Category == "runtime_failure"));
        Assert.AreEqual(100, report.Scores.Completion);
        Assert.AreEqual(100, report.Scores.ToolExecution);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-session-benchmark-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteSessionJsonl(string root, string sessionId)
    {
        var path = Path.Combine(root, "jsonl", $"{sessionId}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var records = new object[]
        {
            new
            {
                type = "event",
                eventType = "tool_call",
                data = JsonSerializer.Serialize(new
                {
                    name = "shell",
                    arguments = JsonSerializer.Serialize(new { command = "python app.py" }),
                }),
                sequenceNum = 1,
                recordedAt = "2026-06-05T00:00:00+00:00",
            },
            new
            {
                type = "event",
                eventType = "tool_result",
                data = JsonSerializer.Serialize(new
                {
                    name = "shell",
                    exitCode = 1,
                    error = "exit code 1",
                    output = "Traceback",
                }),
                sequenceNum = 2,
                recordedAt = "2026-06-05T00:00:01+00:00",
            },
            new
            {
                type = "event",
                eventType = "tool_call",
                data = JsonSerializer.Serialize(new
                {
                    name = "shell",
                    arguments = JsonSerializer.Serialize(new { command = "python implicit.py" }),
                }),
                sequenceNum = 3,
                recordedAt = "2026-06-05T00:00:03+00:00",
            },
            new
            {
                type = "assistant",
                usageJson = JsonSerializer.Serialize(new
                {
                    PromptTokens = 10,
                    CompletionTokens = 2,
                    TotalTokens = 12,
                }),
            },
        };
        var lines = records.Select(record => JsonSerializer.Serialize(record));
        File.WriteAllText(path, string.Join(Environment.NewLine, lines), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void WriteApprovalAudit(string root, string sessionId)
    {
        var path = Path.Combine(root, "runtime", "tool-approval", "audit-events.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var records = new object[]
        {
            new
            {
                eventId = "audit-1",
                eventType = "TicketMismatch",
                sessionId,
                toolId = "shell",
                command = "python app.py",
                reason = "Approved ticket does not match the actual arguments.",
                createdAtUtc = "2026-06-05T00:00:00+00:00",
            },
            new
            {
                eventId = "audit-2",
                eventType = "TicketSubmitted",
                sessionId,
                toolId = "shell",
                command = "python app.py",
                ticketId = "tap_1",
                reason = "Explicit approval requested.",
                createdAtUtc = "2026-06-05T00:00:02+00:00",
            },
            new
            {
                eventId = "audit-3",
                eventType = "ImplicitApproved",
                sessionId,
                toolId = "shell",
                command = "python implicit.py",
                decision = "Approved",
                reason = "Workspace command approved by audit agent.",
                createdAtUtc = "2026-06-05T00:00:03.250+00:00",
            },
        };
        File.WriteAllLines(path, records.Select(record => JsonSerializer.Serialize(record)));
    }

    private static void WriteTickets(string root, string sessionId)
    {
        var path = Path.Combine(root, "runtime", "tool-approval", "tickets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var records = new object[]
        {
            new
            {
                ticketId = "tap_1",
                identity = new { sessionId, workspaceId = "default", agentInstanceId = "agent-1", userId = "admin" },
                toolId = "shell",
                status = "Consumed",
                scope = "Once",
                remainingUses = 0,
                request = new
                {
                    requestedArgumentsJson = JsonSerializer.Serialize(new { command = "python app.py" }),
                },
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(records, JsonOptions));
    }

    private static void WriteTimeline(string root, string sessionId)
    {
        var path = Path.Combine(root, "logs", "diagnostics", "session-timeline", "20260605", $"{sessionId}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var records = new object[]
        {
            new { status = "started", component = "tool", operation = "shell", recordedAtUtc = "2026-06-05T00:00:00+00:00" },
            new { status = "succeeded", component = "tool", operation = "shell", recordedAtUtc = "2026-06-05T00:00:01+00:00" },
        };
        File.WriteAllLines(path, records.Select(record => JsonSerializer.Serialize(record)));
    }

    private static void WriteSessionLog(string root, string sessionId)
    {
        var path = Path.Combine(root, "logs", "sessions", sessionId, "session-20260605.log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "[WRN] approval required\r\n[ERR] exit code 1\r\n");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
