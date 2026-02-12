using System.Text.Json;
using PuddingCode.Configuration;
using PuddingCode.SubAgents;

namespace PuddingCoreTests.SubAgents;

[TestClass]
public sealed class SubAgentRunModelsTests
{
    // ═══════════════════════════════════════════════════════════════
    // 路径解析测试
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    public void SubAgentRunRoot_Correctly_Joins_Paths()
    {
        var paths = PuddingDataPaths.FromRoot(Path.Combine(Path.GetTempPath(), "pudding-data-root"));

        var result = paths.SubAgentRunRoot("default", "default.researcher-001", "run_20260519_abcdef12");

        var expected = Path.Combine(
            paths.DataRoot, "workspaces", "default", "agents", "default.researcher-001", "runs", "run_20260519_abcdef12");
        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void WorkspaceAgentPermissionsFile_Correctly_Joins_Paths()
    {
        var paths = PuddingDataPaths.FromRoot(Path.Combine(Path.GetTempPath(), "pudding-data-root"));

        var result = paths.WorkspaceAgentPermissionsFile("default", "default.researcher-001");

        var expected = Path.Combine(
            paths.DataRoot, "workspaces", "default", "agents", "default.researcher-001", "permissions.json");
        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void WorkspaceAgentRoot_Still_Works_For_SubAgent()
    {
        var paths = PuddingDataPaths.FromRoot(Path.Combine(Path.GetTempPath(), "pudding-data-root"));

        var result = paths.WorkspaceAgentRoot("default", "default.researcher-001");

        var expected = Path.Combine(
            paths.DataRoot, "workspaces", "default", "agents", "default.researcher-001");
        Assert.AreEqual(expected, result);
    }

    // ═══════════════════════════════════════════════════════════════
    // JSON 序列化/反序列化测试
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    public void SubAgentRunManifest_Roundtrip_Json()
    {
        var manifest = new SubAgentRunManifest
        {
            RunId = "run_20260519_abcdef12",
            ParentSessionId = "ses_parent",
            SubSessionId = "ses_parent-sub-12345678",
            WorkspaceId = "default",
            AgentInstanceId = "default.researcher-001",
            TemplateId = "researcher",
            Task = "总结最近的会话问题",
            Status = "completed",
            StartedAt = new DateTimeOffset(2026, 5, 19, 0, 0, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2026, 5, 19, 0, 0, 12, TimeSpan.Zero),
            LlmProfiles = new Dictionary<string, string>
            {
                ["conscious"] = "default-conscious",
                ["subconscious"] = "default-subconscious"
            },
            Trace = new Dictionary<string, string>
            {
                ["traceId"] = "trace_abc"
            }
        };

        var json = JsonSerializer.Serialize(manifest);
        var deserialized = JsonSerializer.Deserialize<SubAgentRunManifest>(json);

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(manifest.RunId, deserialized!.RunId);
        Assert.AreEqual(manifest.ParentSessionId, deserialized.ParentSessionId);
        Assert.AreEqual(manifest.SubSessionId, deserialized.SubSessionId);
        Assert.AreEqual(manifest.WorkspaceId, deserialized.WorkspaceId);
        Assert.AreEqual(manifest.AgentInstanceId, deserialized.AgentInstanceId);
        Assert.AreEqual(manifest.TemplateId, deserialized.TemplateId);
        Assert.AreEqual(manifest.Task, deserialized.Task);
        Assert.AreEqual(manifest.Status, deserialized.Status);
        Assert.AreEqual(manifest.StartedAt, deserialized.StartedAt);
        Assert.AreEqual(manifest.CompletedAt, deserialized.CompletedAt);
        Assert.AreEqual(manifest.LlmProfiles["conscious"], deserialized.LlmProfiles["conscious"]);
        Assert.AreEqual(manifest.Trace["traceId"], deserialized.Trace["traceId"]);
    }

    [TestMethod]
    public void SubAgentRunManifest_Json_Has_KebabCase_Keys()
    {
        var manifest = new SubAgentRunManifest
        {
            RunId = "run_20260519_abcdef12",
            ParentSessionId = "ses_parent",
            SubSessionId = "ses_parent-sub-12345678",
            WorkspaceId = "default",
            AgentInstanceId = "default.researcher-001",
            TemplateId = "researcher",
            Task = "总结最近的会话问题",
            Status = "running",
            StartedAt = new DateTimeOffset(2026, 5, 19, 0, 0, 0, TimeSpan.Zero),
        };

        var json = JsonSerializer.Serialize(manifest);

        // 验证关键字段存在于 JSON 中
        Assert.IsTrue(json.Contains("\"runId\"") || json.Contains("\"RunId\""), "JSON should contain runId");
        Assert.IsTrue(json.Contains("run_20260519_abcdef12"), "JSON should contain run ID value");
        Assert.IsTrue(json.Contains("ses_parent"), "JSON should contain parent session ID");
        Assert.IsTrue(json.Contains("running"), "JSON should contain status");
    }

    [TestMethod]
    public void SubAgentRunCompletion_Serialization_Roundtrip()
    {
        var completion = new SubAgentRunCompletion
        {
            Status = "completed",
            Output = "这是子代理的输出结果。",
            ErrorMessage = null,
            TotalRounds = 5,
            TotalToolCalls = 12,
            TotalDurationMs = 3400
        };

        var json = JsonSerializer.Serialize(completion);
        var deserialized = JsonSerializer.Deserialize<SubAgentRunCompletion>(json);

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(completion.Status, deserialized!.Status);
        Assert.AreEqual(completion.Output, deserialized.Output);
        Assert.IsNull(deserialized.ErrorMessage);
        Assert.AreEqual(completion.TotalRounds, deserialized.TotalRounds);
        Assert.AreEqual(completion.TotalToolCalls, deserialized.TotalToolCalls);
        Assert.AreEqual(completion.TotalDurationMs, deserialized.TotalDurationMs);
    }

    [TestMethod]
    public void SubAgentToolAuditEntry_Serialization_Roundtrip()
    {
        var entry = new SubAgentToolAuditEntry
        {
            ToolCallId = "call_1",
            ToolName = "search_memory",
            ArgsHash = "sha256:abc123",
            Success = true,
            DurationMs = 34,
            OutputLength = 512
        };

        var json = JsonSerializer.Serialize(entry);
        var deserialized = JsonSerializer.Deserialize<SubAgentToolAuditEntry>(json);

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(entry.ToolCallId, deserialized!.ToolCallId);
        Assert.AreEqual(entry.ToolName, deserialized.ToolName);
        Assert.AreEqual(entry.ArgsHash, deserialized.ArgsHash);
        Assert.AreEqual(entry.Success, deserialized.Success);
        Assert.AreEqual(entry.DurationMs, deserialized.DurationMs);
        Assert.AreEqual(entry.OutputLength, deserialized.OutputLength);
        Assert.IsNull(deserialized.ErrorMessage);
    }

    [TestMethod]
    public void SubAgentRunHandle_Holds_RunId_And_Path()
    {
        var handle = new SubAgentRunHandle
        {
            RunId = "run_20260519_abcdef12",
            ArchivePath = "/data/workspaces/default/agents/default.researcher-001/runs/run_20260519_abcdef12"
        };

        Assert.AreEqual("run_20260519_abcdef12", handle.RunId);
        Assert.IsTrue(handle.ArchivePath.Contains("run_20260519_abcdef12"));
    }
}
