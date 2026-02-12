using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class QuerySessionLogsToolTests
{
    [TestMethod]
    public void QuerySessionLogsTool_Uses_Strongly_Typed_Tool_Base()
    {
        Assert.IsTrue(
            typeof(PuddingToolBase<QuerySessionLogsArgs>).IsAssignableFrom(typeof(QuerySessionLogsTool)),
            "QuerySessionLogsTool should derive from PuddingToolBase<QuerySessionLogsArgs>.");
    }

    [TestMethod]
    public async Task ExecuteSkillAsync_Grep_InjectsWorkspaceAndReturnsEvidence()
    {
        var service = new CapturingRawSessionLogService();
        var tool = new QuerySessionLogsTool(service, NullLogger<QuerySessionLogsTool>.Instance);

        var result = await ExecuteAsync(tool, new Dictionary<string, string>
        {
            ["action"] = "grep",
            ["query"] = "needle",
            ["day"] = "2026-06-02",
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("ws-tool", service.LastSearchRequest?.WorkspaceId);
        Assert.AreEqual("agent-1", service.LastSearchRequest?.AgentInstanceId);
        Assert.AreEqual("needle", service.LastSearchRequest?.Query);
        Assert.AreEqual("2026-06-02", service.LastSearchRequest?.Day);

        using var doc = JsonDocument.Parse(result.Output!);
        Assert.AreEqual("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("grep", doc.RootElement.GetProperty("action").GetString());
        Assert.AreEqual("session-message:s1:7", doc.RootElement
            .GetProperty("matches")[0]
            .GetProperty("evidenceRef")
            .GetString());
    }

    [TestMethod]
    public async Task ExecuteSkillAsync_Messages_ReturnsTranscriptByDefault()
    {
        var service = new CapturingRawSessionLogService();
        var tool = new QuerySessionLogsTool(service, NullLogger<QuerySessionLogsTool>.Instance);

        var result = await ExecuteAsync(tool, new Dictionary<string, string>
        {
            ["action"] = "messages",
            ["session_id"] = "session-1",
            ["limit"] = "10",
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("ws-tool", service.LastReadMessagesWorkspaceId);
        Assert.AreEqual("agent-1", service.LastReadMessagesAgentInstanceId);
        Assert.AreEqual("session-1", service.LastReadMessagesSessionId);
        Assert.AreEqual(10, service.LastReadMessagesLimit);

        using var doc = JsonDocument.Parse(result.Output!);
        Assert.AreEqual("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("messages", doc.RootElement.GetProperty("action").GetString());
        var transcript = doc.RootElement.GetProperty("transcript");
        Assert.IsFalse(transcript.GetProperty("isPaged").GetBoolean());
        StringAssert.Contains(transcript.GetProperty("text").GetString(), "final reply");
    }

    [TestMethod]
    public async Task ExecuteSkillAsync_Messages_PaginatesTranscriptOverOneKb()
    {
        var service = new CapturingRawSessionLogService();
        var tool = new QuerySessionLogsTool(service, NullLogger<QuerySessionLogsTool>.Instance);

        var result = await ExecuteAsync(tool, new Dictionary<string, string>
        {
            ["action"] = "messages",
            ["session_id"] = "long-session",
            ["page"] = "1",
            ["window_size"] = "1024",
        });

        Assert.IsTrue(result.Success, result.Error);

        using var doc = JsonDocument.Parse(result.Output!);
        var transcript = doc.RootElement.GetProperty("transcript");
        Assert.IsTrue(transcript.GetProperty("isPaged").GetBoolean());
        Assert.AreEqual(1000, service.LastReadMessagesLimit);
        Assert.AreEqual(1, transcript.GetProperty("page").GetInt32());
        Assert.IsTrue(transcript.GetProperty("totalPages").GetInt32() > 1);
        Assert.IsTrue(transcript.GetProperty("text").GetString()!.Length <= 1024);
        StringAssert.Contains(transcript.GetProperty("note").GetString(), "当前分页 1");
        StringAssert.Contains(transcript.GetProperty("nextPageExample").GetString(), "\"page\":2");
    }

    [TestMethod]
    public async Task ExecuteSkillAsync_ReadRawEvents_AcceptsStringAfterSequence()
    {
        var service = new CapturingRawSessionLogService();
        var tool = new QuerySessionLogsTool(service, NullLogger<QuerySessionLogsTool>.Instance);

        var result = await ExecuteAsync(tool, new Dictionary<string, string>
        {
            ["action"] = "read_raw_events",
            ["session_id"] = "session-1",
            ["diagnostic"] = "true",
            ["after_sequence"] = "20",
            ["limit"] = "10",
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(20, service.LastReadRawAfterSequence);

        using var doc = JsonDocument.Parse(result.Output!);
        Assert.AreEqual("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("read_raw_events", doc.RootElement.GetProperty("action").GetString());
    }

    [TestMethod]
    public async Task ExecuteSkillAsync_ReadRawEvents_RequiresDiagnosticMode()
    {
        var service = new CapturingRawSessionLogService();
        var tool = new QuerySessionLogsTool(service, NullLogger<QuerySessionLogsTool>.Instance);

        var result = await ExecuteAsync(tool, new Dictionary<string, string>
        {
            ["action"] = "read_raw_events",
            ["session_id"] = "session-1",
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNull(service.LastReadRawAfterSequence);

        using var doc = JsonDocument.Parse(result.Output!);
        Assert.AreEqual("error", doc.RootElement.GetProperty("status").GetString());
        StringAssert.Contains(doc.RootElement.GetProperty("message").GetString(), "diagnostic=true");
    }

    [TestMethod]
    public void ToolMetadata_IsReadOnlyLowRisk()
    {
        var tool = new QuerySessionLogsTool(new CapturingRawSessionLogService(), NullLogger<QuerySessionLogsTool>.Instance);

        Assert.AreEqual("query_session_logs", tool.Descriptor.ToolId);
        Assert.AreEqual(ToolPermissionLevel.Low, tool.Descriptor.PermissionLevel);
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.ReadOnly));
        Assert.IsTrue(tool.Descriptor.Parameters.Properties.Any(p => p.Name == "workspace_id"));
        Assert.IsTrue(tool.Descriptor.Parameters.Properties.Any(p => p.Name == "query"));
    }

    private static Task<ToolExecutionResult> ExecuteAsync(
        QuerySessionLogsTool tool,
        IReadOnlyDictionary<string, string> parameters) =>
        tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = JsonSerializer.Serialize(parameters),
            Context = new ToolExecutionContext
            {
                AgentInstanceId = "agent-1",
                WorkspaceId = "ws-tool",
                SessionId = "session-1",
            },
        });

    private sealed class CapturingRawSessionLogService : IRawSessionLogService
    {
        public RawSessionLogSearchRequest? LastSearchRequest { get; private set; }
        public string? LastReadMessagesWorkspaceId { get; private set; }
        public string? LastReadMessagesAgentInstanceId { get; private set; }
        public string? LastReadMessagesSessionId { get; private set; }
        public int? LastReadMessagesLimit { get; private set; }
        public long? LastReadRawAfterSequence { get; private set; }

        public Task<RawSessionLogDayList> ListDaysAsync(
            string workspaceId,
            string? fromDay = null,
            string? toDay = null,
            int limit = 31,
            string? agentInstanceId = null,
            CancellationToken ct = default)
            => Task.FromResult(new RawSessionLogDayList(
                [new RawSessionLogDaySummary("2026-06-02", 1, 1)]));

        public Task<RawSessionLogSessionList> ListSessionsAsync(
            string workspaceId,
            string day,
            int limit = 100,
            string? agentInstanceId = null,
            CancellationToken ct = default)
            => Task.FromResult(new RawSessionLogSessionList(
                [new RawSessionLogSessionSummary("s1", workspaceId, day, 1, 7, 7, "2026-06-02T08:00:00.0000000Z", "2026-06-02T08:00:00.0000000Z")]));

        public Task<RawSessionLogSearchResult> GrepAsync(
            RawSessionLogSearchRequest request,
            CancellationToken ct = default)
        {
            LastSearchRequest = request;
            return Task.FromResult(new RawSessionLogSearchResult(
                [
                    new RawSessionLogMatch(
                        "s1",
                        request.WorkspaceId,
                        "2026-06-02",
                        7,
                        "message.delta",
                        "2026-06-02T08:00:00.0000000Z",
                        "needle snippet",
                        "session-log:2026-06-02:s1:7")
                ],
                HasMore: false));
        }

        public Task<RawSessionLogMessagePage> ReadMessagesAsync(
            string workspaceId,
            string sessionId,
            string? agentInstanceId = null,
            long? before = null,
            int limit = 20,
            CancellationToken ct = default)
        {
            LastReadMessagesWorkspaceId = workspaceId;
            LastReadMessagesAgentInstanceId = agentInstanceId;
            LastReadMessagesSessionId = sessionId;
            LastReadMessagesLimit = limit;

            if (sessionId == "long-session")
            {
                return Task.FromResult(new RawSessionLogMessagePage(
                    [
                        new RawSessionLogMessage(
                            "m-long",
                            sessionId,
                            workspaceId,
                            "agent",
                            new string('x', 1500),
                            "2026-06-02T08:00:00.0000000Z",
                            "session-message:long-session:m-long")
                    ],
                    HasMore: false,
                    NextCursor: null));
            }

            return Task.FromResult(new RawSessionLogMessagePage(
                [
                    new RawSessionLogMessage(
                        "m1",
                        sessionId,
                        workspaceId,
                        "agent",
                        "final reply",
                        "2026-06-02T08:00:00.0000000Z",
                        "session-message:session-1:m1")
                ],
                HasMore: false,
                NextCursor: null));
        }

        public Task<RawSessionLogSearchResult> GrepMessagesAsync(
            RawSessionLogSearchRequest request,
            CancellationToken ct = default)
        {
            LastSearchRequest = request;
            return Task.FromResult(new RawSessionLogSearchResult(
                [
                    new RawSessionLogMatch(
                        "s1",
                        request.WorkspaceId,
                        "2026-06-02",
                        7,
                        "message",
                        "2026-06-02T08:00:00.0000000Z",
                        "needle transcript snippet",
                        "session-message:s1:7")
                ],
                HasMore: false));
        }

        public Task<RawSessionLogReadResult> ReadSessionAsync(
            string workspaceId,
            string sessionId,
            long? afterSequence = null,
            int limit = 100,
            string? agentInstanceId = null,
            CancellationToken ct = default)
        {
            LastReadRawAfterSequence = afterSequence;
            return Task.FromResult(new RawSessionLogReadResult(
                [
                    new RawSessionLogEvent(
                        sessionId,
                        workspaceId,
                        "2026-06-02",
                        21,
                        "message.delta",
                        "{\"text\":\"needle\"}",
                        "2026-06-02T08:00:00.0000000Z",
                        "session-log:2026-06-02:s1:21")
                ],
                HasMore: false,
                NextSequence: null));
        }

        public Task<RawSessionLogMessage?> GetMessageByIdAsync(
            string workspaceId,
            long messageId,
            CancellationToken ct = default)
            => Task.FromResult<RawSessionLogMessage?>(new RawSessionLogMessage(
                messageId.ToString(),
                "session-1",
                workspaceId,
                "agent",
                "message by id",
                "2026-06-02T08:00:00.0000000Z",
                $"session-message:session-1:{messageId}"));
    }
}
