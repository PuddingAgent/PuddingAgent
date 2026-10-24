using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class AgentSkillToolsTests
{
    /// <summary>
    /// create 不得再回吐整份索引：实测旧实现单次返回 313,549 字符，其中索引占 99%，
    /// 而调用方只需知道“建好了、现在几个”。这条锁住“不回吐”，防止以后为了“方便”又塞回来。
    /// </summary>
    [TestMethod]
    public async Task AgentSkillTool_Create_Does_Not_Echo_Full_Index()
    {
        using var temp = new TempDataRoot();
        var service = new AgentSkillFileService(temp.Paths);
        var tool = CreateTool(service);

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-create-index",
            ArgumentsJson = """
            {
              "action": "create",
              "skill_id": "no_index_echo",
              "name": "No Index Echo",
              "summary": "Guard against echoing the whole index.",
              "skill_markdown": "# No Index Echo"
            }
            """,
            Context = TestContext("agent-a"),
        });

        Assert.IsTrue(result.Success, result.Error);
        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;

        Assert.AreEqual(1, root.GetProperty("count").GetInt32());
        Assert.IsFalse(root.TryGetProperty("index", out _), "不得再回吐完整索引");
        var ids = root.GetProperty("indexSkillIds").EnumerateArray()
            .Select(e => e.GetString()).ToArray();
        CollectionAssert.Contains(ids, "no_index_echo");
    }

    [TestMethod]
    public async Task AgentSkillTool_Create_Initializes_Agent_Private_Skill_And_Returns_Physical_Path()
    {
        using var temp = new TempDataRoot();
                var service = new AgentSkillFileService(temp.Paths);
        var tool = CreateTool(service);

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-create",
            ArgumentsJson = """
            {
              "action": "create",
              "skill_id": "daily_notes",
              "name": "Daily Notes",
              "summary": "Use this when writing daily notes.",
              "skill_markdown": "# Daily Notes\n\nKeep notes concise."
            }
            """,
            Context = TestContext("agent-a"),
        });

        Assert.IsTrue(result.Success, result.Error);
        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;
        Assert.AreEqual("ok", root.GetProperty("status").GetString());
        Assert.AreEqual("create", root.GetProperty("action").GetString());
        Assert.AreEqual("agent-a", root.GetProperty("agentInstanceId").GetString());
        Assert.AreEqual("daily_notes", root.GetProperty("skill").GetProperty("skillId").GetString());

        var physicalPath = root.GetProperty("physicalPath").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(physicalPath));
        Assert.IsTrue(Directory.Exists(physicalPath));
        Assert.AreEqual(Path.Combine(temp.Paths.AgentInstanceRoot("agent-a"), "skills", "daily_notes"), physicalPath);
        Assert.IsTrue(File.Exists(Path.Combine(physicalPath!, "manifest.json")));
        Assert.IsTrue(File.Exists(Path.Combine(physicalPath!, "SKILL.md")));
    }

    [TestMethod]
    public async Task AgentSkillTool_List_Uses_Current_Agent_Instance_And_Does_Not_Leak_Other_Agents()
    {
        using var temp = new TempDataRoot();
        var service = new AgentSkillFileService(temp.Paths);
        await service.CreateAsync("agent-a", new AgentSkillCreateRequest
        {
            SkillId = "private_a",
            Name = "Private A",
            Summary = "Agent A only.",
            SkillMarkdown = "agent-a-content",
        });
        await service.CreateAsync("agent-b", new AgentSkillCreateRequest
        {
            SkillId = "private_b",
            Name = "Private B",
            Summary = "Agent B only.",
            SkillMarkdown = "agent-b-content",
        });
                var tool = CreateTool(service);

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-list",
            ArgumentsJson = """{"action":"list"}""",
            Context = TestContext("agent-a"),
        });

        Assert.IsTrue(result.Success, result.Error);
        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;
        Assert.AreEqual("ok", root.GetProperty("status").GetString());
        Assert.AreEqual("agent-a", root.GetProperty("agentInstanceId").GetString());
        Assert.AreEqual(1, root.GetProperty("count").GetInt32());
        Assert.AreEqual("private_a", root.GetProperty("skills")[0].GetProperty("skillId").GetString());
        Assert.IsFalse(result.Output.Contains("private_b", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Output.Contains("agent-b-content", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task AgentSkillTool_ReadFile_Returns_Content_Physical_Path_And_Truncation_Metadata()
    {
        using var temp = new TempDataRoot();
        var service = new AgentSkillFileService(temp.Paths);
        await service.CreateAsync("agent-a", new AgentSkillCreateRequest
        {
            SkillId = "long_skill",
            Name = "Long Skill",
            Summary = "Long summary.",
            SkillMarkdown = "0123456789abcdefghijklmnopqrstuvwxyz",
        });
                var tool = CreateTool(service);

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-read-file",
            ArgumentsJson = """{"action":"read_file","skill_id":"long_skill","max_chars":10}""",
            Context = TestContext("agent-a"),
        });

        Assert.IsTrue(result.Success, result.Error);
        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;
        Assert.AreEqual("ok", root.GetProperty("status").GetString());
        Assert.AreEqual("read_file", root.GetProperty("action").GetString());
        Assert.AreEqual("long_skill", root.GetProperty("skillId").GetString());
        Assert.AreEqual("SKILL.md", root.GetProperty("relativePath").GetString());
        Assert.AreEqual("0123456789", root.GetProperty("content").GetString());
        Assert.IsTrue(root.GetProperty("truncated").GetBoolean());
        Assert.AreEqual(36, root.GetProperty("originalLength").GetInt32());
        Assert.IsTrue(File.Exists(root.GetProperty("physicalPath").GetString()));
    }

    [TestMethod]
    public async Task AgentSkillTool_Update_SetEnabled_Delete_Refreshes_Index()
    {
        using var temp = new TempDataRoot();
        var service = new AgentSkillFileService(temp.Paths);
        await service.CreateAsync("agent-a", new AgentSkillCreateRequest
        {
            SkillId = "review_rules",
            Name = "Review Rules",
            Summary = "Old summary.",
            SkillMarkdown = "old",
        });
                var tool = CreateTool(service);

        var update = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-update",
            ArgumentsJson = """
            {
              "action": "update",
              "skill_id": "review_rules",
              "summary": "Updated summary.",
              "skill_markdown": "# Updated"
            }
            """,
            Context = TestContext("agent-a"),
        });
        Assert.IsTrue(update.Success, update.Error);

        var disable = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-disable",
            ArgumentsJson = """{"action":"set_enabled","skill_id":"review_rules","enabled":false}""",
            Context = TestContext("agent-a"),
        });
        Assert.IsTrue(disable.Success, disable.Error);

        var indexAfterDisable = await service.GetIndexAsync("agent-a");
        Assert.AreEqual("Updated summary.", indexAfterDisable.Skills.Single().Summary);
        Assert.IsFalse(indexAfterDisable.Skills.Single().Enabled);

        var delete = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-delete",
            ArgumentsJson = """{"action":"delete","skill_id":"review_rules"}""",
            Context = TestContext("agent-a"),
        });

        Assert.IsTrue(delete.Success, delete.Error);
        using var deleteDoc = JsonDocument.Parse(delete.Output);
        Assert.AreEqual("delete", deleteDoc.RootElement.GetProperty("action").GetString());
        Assert.IsFalse(Directory.Exists(deleteDoc.RootElement.GetProperty("deletedPath").GetString()));
        Assert.AreEqual(0, (await service.GetIndexAsync("agent-a")).Skills.Count);
    }

    [TestMethod]
    public async Task SkillTools_Reject_Missing_Required_Arguments_And_Path_Traversal()
    {
        using var temp = new TempDataRoot();
                var service = new AgentSkillFileService(temp.Paths);
        var tool = CreateTool(service);

        var missingSkillId = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-missing",
            ArgumentsJson = """{"action":"read_file"}""",
            Context = TestContext("agent-a"),
        });
        Assert.IsFalse(missingSkillId.Success);
        StringAssert.Contains(missingSkillId.Error, "skill_id is required");

        var invalidPath = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-path",
            ArgumentsJson = """{"action":"read_file","skill_id":"safe","relative_path":"../secret.txt"}""",
            Context = TestContext("agent-a"),
        });
        Assert.IsFalse(invalidPath.Success);
        StringAssert.Contains(invalidPath.Error, "SKILL file path is invalid");

        var invalidAction = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-action",
            ArgumentsJson = """{"action":"publish","skill_id":"safe"}""",
            Context = TestContext("agent-a"),
        });
        Assert.IsFalse(invalidAction.Success);
        StringAssert.Contains(invalidAction.Error, "Unknown agent_skill action");
    }

    [TestMethod]
    public void AgentSkillTool_Descriptor_Is_Low_Risk_Agent_Private_Tool()
    {
        using var temp = new TempDataRoot();
        var service = new AgentSkillFileService(temp.Paths);

        var descriptor = CreateTool(service).Descriptor;
        Assert.AreEqual("agent_skill", descriptor.ToolId);
        Assert.AreEqual(ToolCategory.FileSystem, descriptor.Category);
        Assert.AreEqual(ToolPermissionLevel.Low, descriptor.PermissionLevel);
        Assert.IsTrue(descriptor.Safety.HasFlag(ToolSafetyFlags.ConcurrencySafe));
        Assert.IsFalse(descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresFileWrite));
        Assert.IsFalse(descriptor.Safety.HasFlag(ToolSafetyFlags.Destructive));
        CollectionAssert.Contains(descriptor.Parameters.Required.ToArray(), "action");

        var policy = new ToolPermissionPolicyService();
        var decision = policy.Classify(descriptor);
        Assert.AreEqual(ToolPermissionTier.AutoAllowed, decision.Tier);
        Assert.IsFalse(decision.RequiresRuntimeAuthorization);
        StringAssert.Contains(decision.Reason, "agent-private SKILL");
    }

    private static AgentSkillTool CreateTool(AgentSkillFileService service)
        => new(service, new NullHttpClientFactory(), new ConfigurationBuilder().Build());

    private static ToolExecutionContext TestContext(string agentInstanceId) => new()
    {
        AgentInstanceId = agentInstanceId,
        WorkspaceId = "default",
        SessionId = "session-1",
    };

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class TempDataRoot : IDisposable
    {
        public TempDataRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-agent-skill-tool-tests", Guid.NewGuid().ToString("N"));
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
