using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class AgentRawLogMirrorServiceTests
{
    [TestMethod]
    public async Task MirrorAsync_WritesRawEventJsonlUnderAgentPrivateRawLogDirectory()
    {
        using var temp = new TempDataRoot();
        var service = new AgentRawLogMirrorService(
            temp.Paths,
            NullLogger<AgentRawLogMirrorService>.Instance);

        await service.MirrorAsync(new AgentRawLogMirrorRecord(
            WorkspaceId: "workspace-1",
            AgentInstanceId: "agent-1",
            AgentTemplateId: "template-1",
            SessionId: "session-1",
            SequenceNum: 7,
            EventType: "tool_result",
            Data: """{"ok":true}""",
            RecordedAt: "2026-06-16T02:03:04.0000000+00:00",
            TraceId: "trace-1",
            CorrelationId: "corr-1",
            ExecutionId: "exec-1",
            ParentExecutionId: null,
            SubAgentId: null,
            Component: "agent_execution",
            Operation: "tool.result"));

        var path = temp.Paths.AgentInstanceRawLogJsonlFile("agent-1", "2026-06-16", "session-1");
        Assert.IsTrue(File.Exists(path));

        var lines = await File.ReadAllLinesAsync(path);
        Assert.AreEqual(1, lines.Length);

        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        Assert.AreEqual("workspace-1", root.GetProperty("workspaceId").GetString());
        Assert.AreEqual("agent-1", root.GetProperty("agentInstanceId").GetString());
        Assert.AreEqual("template-1", root.GetProperty("agentTemplateId").GetString());
        Assert.AreEqual("session-1", root.GetProperty("sessionId").GetString());
        Assert.AreEqual(7, root.GetProperty("sequenceNum").GetInt64());
        Assert.AreEqual("tool_result", root.GetProperty("eventType").GetString());
        Assert.AreEqual("""{"ok":true}""", root.GetProperty("data").GetString());
        Assert.AreEqual("session-raw:2026-06-16:session-1:7", root.GetProperty("evidenceRef").GetString());
    }

    [TestMethod]
    public async Task MirrorAsync_SkipsFileWrite_WhenAgentInstanceIdMissing()
    {
        using var temp = new TempDataRoot();
        var service = new AgentRawLogMirrorService(
            temp.Paths,
            NullLogger<AgentRawLogMirrorService>.Instance);

        await service.MirrorAsync(new AgentRawLogMirrorRecord(
            WorkspaceId: "workspace-1",
            AgentInstanceId: "",
            AgentTemplateId: "template-1",
            SessionId: "session-1",
            SequenceNum: 1,
            EventType: "delta",
            Data: "{}",
            RecordedAt: "2026-06-16T02:03:04.0000000+00:00",
            TraceId: null,
            CorrelationId: null,
            ExecutionId: null,
            ParentExecutionId: null,
            SubAgentId: null,
            Component: null,
            Operation: null));

        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Paths.AgentInstancesRoot, "logs")));
    }

    private sealed class TempDataRoot : IDisposable
    {
        public TempDataRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-agent-raw-log-tests", Guid.NewGuid().ToString("N"));
            Paths = PuddingDataPaths.FromRoot(Root);
        }

        public string Root { get; }
        public PuddingDataPaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
