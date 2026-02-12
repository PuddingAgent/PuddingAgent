using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingCode.Configuration;
using PuddingCode.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingMemoryEngine.Data;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingRuntime;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed partial class PuddingToolInfrastructureTests
{
    [TestMethod]
    public void DescriptorFactory_Builds_Metadata_And_Parameter_Schema_From_Tool_Type()
    {
        var descriptor = ToolDescriptorFactory.Create<SampleSearchTool, SampleSearchArgs>();

        Assert.AreEqual("sample_search", descriptor.ToolId);
        Assert.AreEqual("Sample search", descriptor.Name);
        Assert.AreEqual("Searches sample data.", descriptor.Description);
        Assert.AreEqual(ToolCategory.Query, descriptor.Category);
        Assert.AreEqual(ToolPermissionLevel.Low, descriptor.PermissionLevel);
        Assert.IsTrue(descriptor.Safety.HasFlag(ToolSafetyFlags.ReadOnly));
        Assert.IsTrue(descriptor.Safety.HasFlag(ToolSafetyFlags.ConcurrencySafe));

        var properties = descriptor.Parameters.Properties.ToDictionary(p => p.Name);
        Assert.AreEqual("string", properties["query"].Type);
        Assert.AreEqual("Search query", properties["query"].Description);
        Assert.AreEqual("integer", properties["max_results"].Type);
        CollectionAssert.AreEquivalent(new[] { "query" }, descriptor.Parameters.Required.ToArray());
    }

    [TestMethod]
    public void CodeProjectManagementTools_Are_LowRisk_IndexState_Changes()
    {
        var add = ToolDescriptorFactory.Create<CodeProjectAddTool, CodeProjectAddArgs>();
        var remove = ToolDescriptorFactory.Create<CodeProjectRemoveTool, CodeProjectRemoveArgs>();

        Assert.AreEqual(ToolPermissionLevel.Low, add.PermissionLevel);
        Assert.AreEqual(ToolPermissionLevel.Low, remove.PermissionLevel);
        Assert.IsTrue(add.Safety.HasFlag(ToolSafetyFlags.ConcurrencySafe));
        Assert.IsTrue(remove.Safety.HasFlag(ToolSafetyFlags.ConcurrencySafe));
        Assert.IsTrue(add.Safety.HasFlag(ToolSafetyFlags.ReadOnly));
        Assert.IsFalse(add.Safety.HasFlag(ToolSafetyFlags.RequiresFileWrite));
        Assert.IsFalse(add.Safety.HasFlag(ToolSafetyFlags.Destructive));
        Assert.IsFalse(remove.Safety.HasFlag(ToolSafetyFlags.RequiresFileWrite));
        Assert.IsFalse(remove.Safety.HasFlag(ToolSafetyFlags.Destructive));
        StringAssert.Contains(add.Description, "Low-risk index-state change");
        StringAssert.Contains(remove.Description, "Low-risk index-state change");
    }

    [TestMethod]
    public void PuddingToolRegistry_Registers_Tools_And_Rejects_Duplicate_Ids()
    {
        var registry = new PuddingToolRegistry([new SampleSearchTool()]);

        var descriptor = registry.GetDescriptor("sample_search");

        Assert.IsNotNull(descriptor);
        Assert.AreEqual("Sample search", descriptor!.Name);
        Assert.AreEqual(1, registry.ListDescriptors().Count);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new PuddingToolRegistry([new SampleSearchTool(), new SampleSearchTool()]));
    }

    [TestMethod]
    public void PuddingToolRegistry_Rejects_Invalid_Tool_Ids()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            new PuddingToolRegistry([new InvalidDottedTool()]));

        StringAssert.Contains(ex.Message, "bad.tool");
    }

    [TestMethod]
    public void AgentSkillToolAdapter_Exposes_Legacy_Skill_As_Pudding_Tool()
    {
        var legacy = new LegacySkillTool();
        var tool = new AgentSkillToolAdapter(legacy);

        Assert.AreEqual("legacy_echo", tool.Descriptor.ToolId);
        Assert.AreEqual("Legacy Echo", tool.Descriptor.Name);
        Assert.AreEqual(ToolPermissionLevel.Medium, tool.Descriptor.PermissionLevel);
        Assert.AreEqual(legacy.Parameters, tool.Descriptor.Parameters);
    }

    [TestMethod]
    public void AgentSkillToolAdapter_Uses_Known_Schemas_For_Legacy_Runtime_Tools()
    {
        var shell = new AgentSkillToolAdapter(new LegacyRuntimeSkill(
            skillId: "shell",
            name: "shell",
            requiresShellExecution: true,
            permissionLevel: ToolPermissionLevel.High));
        Assert.AreEqual(ToolCategory.Execute, shell.Descriptor.Category);
        Assert.IsTrue(shell.Descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresShell));
        CollectionAssert.AreEqual(new[] { "command" }, shell.Descriptor.Parameters.Required.ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "command", "shell", "working_directory", "timeout_seconds" },
            shell.Descriptor.Parameters.Properties.Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public async Task AgentSkillToolAdapter_Uses_Url_Field_As_Legacy_Input()
    {
        var legacy = new CapturingLegacySkill("http_fetch");
        var tool = new AgentSkillToolAdapter(legacy);

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-http",
            ArgumentsJson = """{"url":"https://example.com/data","method":"GET"}""",
            Context = TestToolContext(),
        });

        Assert.IsTrue(result.Success);
        Assert.AreEqual("https://example.com/data", legacy.LastRequest?.Input);
    }

    [TestMethod]
    public async Task AgentSkillToolAdapter_Preserves_NonString_Json_Parameters()
    {
        var legacy = new CapturingLegacySkill("receive_messages");
        var tool = new AgentSkillToolAdapter(legacy);

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-receive",
            ArgumentsJson = """{"limit":7,"ack":true,"room_id":"default"}""",
            Context = TestToolContext(),
        });

        Assert.IsTrue(result.Success);
        Assert.AreEqual("7", legacy.LastRequest?.Parameters["limit"]);
        Assert.AreEqual("true", legacy.LastRequest?.Parameters["ack"]);
        Assert.AreEqual("default", legacy.LastRequest?.Parameters["room_id"]);
    }

    [TestMethod]
    public void AgentExecutionService_LegacyFallback_Preserves_NonString_Json_Parameters()
    {
        var method = typeof(AgentExecutionService).GetMethod(
            "ExtractParametersFromJson",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var parameters = (IReadOnlyDictionary<string, string>)method.Invoke(
            null,
            ["""{"limit":7,"ack":true,"room_id":"default","filters":{"kind":"agent"}}"""])!;

        Assert.AreEqual("7", parameters["limit"]);
        Assert.AreEqual("true", parameters["ack"]);
        Assert.AreEqual("default", parameters["room_id"]);
        Assert.AreEqual("""{"kind":"agent"}""", parameters["filters"]);
    }

    [TestMethod]
    public void AgentExecutionService_TerminalPayload_Continues_Immediately_For_Background_Process()
    {
        var method = typeof(AgentExecutionService).GetMethod(
            "BuildTerminalExecuteToolPayload",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var payload = (string)method.Invoke(
            null,
            [
                "pid-1",
                new PuddingCode.Abstractions.TerminalProcessInfo
                {
                    ProcessId = "pid-1",
                    SessionId = "session-1",
                    Command = "dotnet test",
                    WorkingDir = "E:\\repo",
                    StartedAt = DateTimeOffset.UtcNow,
                    Status = PuddingCode.Abstractions.TerminalProcessStatus.Running,
                },
                "partial output",
                1,
            ])!;

        StringAssert.Contains(payload, "started background terminal job");
        StringAssert.Contains(payload, "pid-1");
        StringAssert.Contains(payload, "partial output");
        StringAssert.Contains(payload, "terminal_wait");
        StringAssert.Contains(payload, "Do not wait for it in this turn");
        Assert.IsFalse(payload.Contains("exited with code=", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void AgentSkillToolAdapter_Uses_Known_Schemas_For_All_Legacy_Runtime_Tools()
    {
        var expectations = new Dictionary<string, string[]>
        {
            ["http_fetch"] = ["url", "method", "headers", "body", "content_type", "timeout_seconds", "output_format", "max_response_chars", "include_headers", "cookie_scope"],
                        ["search_memory"] = ["query", "book"],
            ["save_memory"] = ["action", "type", "book", "content", "key", "value", "title", "book_id", "chapter_id", "pointer_id", "source_ref", "source_label", "source_reference", "reference_type"],
            ["manage_memory"] = ["action", "book_id", "library_id", "title", "content", "summary", "chapter_id", "source_type", "source_id", "tags", "chapter_order", "source_reference", "reference_type"],
            ["grep_memory"] = ["action", "query", "mode", "book", "top_k"],
            ["query_sessions"] = ["action", "session_id", "before", "limit"],
            ["query_session_logs"] = ["action", "workspace_id", "agent_instance_id", "day", "from_day", "to_day", "session_id", "query", "regex", "diagnostic", "include_events", "after_sequence", "page", "window_size", "limit"],
            ["search_grep"] = ["query", "pattern", "case_sensitive", "max_results"],
            ["spawn_sub_agent"] = ["task", "tasks", "question", "scope", "already_known", "effort", "stop_condition", "output", "agent_template", "sync", "model", "tools", "permission_mode", "timeout_seconds", "plan_id", "task_node_id", "parent_task_node_id", "depth", "max_depth", "role_in_plan"],
            ["manage_tasks"] = ["operation", "task_id", "title", "status"],
            ["send_message"] = ["to", "content", "audience", "visibility", "room_id", "priority", "reply_to_message_id"],
            ["receive_messages"] = ["endpoint_id", "endpoint_kind", "room_id", "limit", "include_delivered", "ack"],
            ["query_sub_agents"] = ["action", "sub_agent_id", "keyword", "days"],
            ["event_subscribe"] = ["operation", "event_type_patterns", "subscription_id", "filter_expression"],
            ["terminal_execute"] = ["command", "cwd"],
        };

        foreach (var (skillId, expectedProperties) in expectations)
        {
            var adapter = new AgentSkillToolAdapter(new LegacyRuntimeSkill(
                skillId,
                skillId,
                requiresShellExecution: skillId.Equals("terminal_execute", StringComparison.OrdinalIgnoreCase),
                permissionLevel: skillId.Equals("terminal_execute", StringComparison.OrdinalIgnoreCase)
                    ? ToolPermissionLevel.High
                    : ToolPermissionLevel.Low));
            var actual = adapter.Descriptor.Parameters.Properties.Select(p => p.Name).ToArray();

            CollectionAssert.AreNotEqual(new[] { "input" }, actual, skillId);
            CollectionAssert.AreEquivalent(expectedProperties, actual, skillId);
        }
    }

    [TestMethod]
    public void AgentSkillToolAdapter_Does_Not_Mark_SideEffect_Low_Skills_As_ReadOnly()
    {
        var search = new AgentSkillToolAdapter(new LegacyRuntimeSkill(
            "search_grep",
            "search_grep",
            requiresShellExecution: false,
            permissionLevel: ToolPermissionLevel.Low));
        var subAgent = new AgentSkillToolAdapter(new LegacyRuntimeSkill(
            "spawn_sub_agent",
            "spawn_sub_agent",
            requiresShellExecution: false,
            permissionLevel: ToolPermissionLevel.Low));
        var sendMessage = new AgentSkillToolAdapter(new LegacyRuntimeSkill(
            "send_message",
            "send_message",
            requiresShellExecution: false,
            permissionLevel: ToolPermissionLevel.Low));
        var receiveMessages = new AgentSkillToolAdapter(new LegacyRuntimeSkill(
            "receive_messages",
            "receive_messages",
            requiresShellExecution: false,
            permissionLevel: ToolPermissionLevel.Low));

        Assert.IsTrue(search.Descriptor.Safety.HasFlag(ToolSafetyFlags.ReadOnly));
        Assert.IsFalse(subAgent.Descriptor.Safety.HasFlag(ToolSafetyFlags.ReadOnly));
        Assert.IsFalse(sendMessage.Descriptor.Safety.HasFlag(ToolSafetyFlags.ReadOnly));
        Assert.IsFalse(receiveMessages.Descriptor.Safety.HasFlag(ToolSafetyFlags.ReadOnly));
    }

    [TestMethod]
    public void SkillRuntime_AutoExposes_LowRisk_AgentCoordination_Skills()
    {
        var runtime = new SkillRuntime(
            [
                new LegacyRuntimeSkill("search_grep", "search_grep", false, ToolPermissionLevel.Low),
                new LegacyRuntimeSkill("spawn_sub_agent", "spawn_sub_agent", false, ToolPermissionLevel.Low),
                new LegacyRuntimeSkill("send_message", "send_message", false, ToolPermissionLevel.Low),
                new LegacyRuntimeSkill("receive_messages", "receive_messages", false, ToolPermissionLevel.Low),
            ],
            sandbox: null!,
            NullLogger<SkillRuntime>.Instance);

        var toolIdsWithoutPolicy = runtime.GetAvailableSkills(null)
            .Select(s => s.SkillId)
            .ToArray();
        var toolIdsWithPolicy = runtime.GetAvailableSkills(new CapabilityPolicy
            {
                DefaultToolNames = ["spawn_sub_agent", "send_message"],
            })
            .Select(s => s.SkillId)
            .ToArray();
        var toolIdsWithAllowList = runtime.GetAvailableSkills(new CapabilityPolicy
            {
                AllowedToolNames = ["receive_messages"],
            })
            .Select(s => s.SkillId)
            .ToArray();

        CollectionAssert.Contains(toolIdsWithoutPolicy, "search_grep");
        CollectionAssert.Contains(toolIdsWithoutPolicy, "spawn_sub_agent");
        CollectionAssert.Contains(toolIdsWithoutPolicy, "send_message");
        CollectionAssert.Contains(toolIdsWithoutPolicy, "receive_messages");
        CollectionAssert.Contains(toolIdsWithPolicy, "spawn_sub_agent");
        CollectionAssert.Contains(toolIdsWithPolicy, "send_message");
        CollectionAssert.Contains(toolIdsWithPolicy, "receive_messages");
        CollectionAssert.Contains(toolIdsWithAllowList, "search_grep");
        CollectionAssert.Contains(toolIdsWithAllowList, "receive_messages");
        CollectionAssert.Contains(toolIdsWithAllowList, "spawn_sub_agent");
    }

    [TestMethod]
    public void ToolPermissionPolicy_AutoAllows_LowRisk_AgentCoordination_Tools()
    {
        var policy = new ToolPermissionPolicyService();
        var toolIds = new[] { "spawn_sub_agent", "send_message", "receive_messages" };

        foreach (var toolId in toolIds)
        {
            var adapter = new AgentSkillToolAdapter(new LegacyRuntimeSkill(
                toolId,
                toolId,
                requiresShellExecution: false,
                permissionLevel: ToolPermissionLevel.Low));

            var decision = policy.Classify(adapter.Descriptor);

            Assert.AreEqual(ToolPermissionTier.AutoAllowed, decision.Tier, toolId);
            Assert.IsFalse(decision.RequiresRuntimeAuthorization, toolId);
            StringAssert.Contains(decision.Reason, "low-risk agent coordination tool");
        }
    }

    [TestMethod]
    public void Native_File_Tools_Classify_By_File_Risk_Not_Shell_Risk()
    {
        var policy = new ToolPermissionPolicyService();
        var readFile = new FileReadTool(NullLogger<FileReadTool>.Instance);
        var writeFile = new FileWriteTool(NullLogger<FileWriteTool>.Instance);
        var patchFile = new FilePatchTool(NullLogger<FilePatchTool>.Instance);

        Assert.AreEqual(ToolPermissionLevel.Low, readFile.Descriptor.PermissionLevel);
        Assert.IsTrue(readFile.Descriptor.Safety.HasFlag(ToolSafetyFlags.ReadOnly));
        Assert.IsFalse(readFile.Descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresShell));
        Assert.IsFalse(policy.RequiresRuntimeAuthorization(readFile.Descriptor));

        Assert.AreEqual(ToolPermissionLevel.High, writeFile.Descriptor.PermissionLevel);
        Assert.IsTrue(writeFile.Descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresFileWrite));
        Assert.IsTrue(writeFile.Descriptor.Safety.HasFlag(ToolSafetyFlags.Destructive));
        Assert.IsFalse(writeFile.Descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresShell));
        Assert.IsTrue(policy.RequiresRuntimeAuthorization(writeFile.Descriptor));

        Assert.AreEqual("file_patch", patchFile.Descriptor.ToolId);
        Assert.IsTrue(policy.RequiresRuntimeAuthorization(patchFile.Descriptor));
    }

    [TestMethod]
    public void Native_Tool_Ids_Are_Valid_Llm_Function_Names()
    {
        var dataPaths = PuddingDataPaths.FromRoot(Path.Combine(Path.GetTempPath(), "pudding-tool-infrastructure-tests"));
        IPuddingTool[] tools =
        [
            new HostShellTool(dataPaths, new AuditLogger(dataPaths), NullLogger<HostShellTool>.Instance),
            new FileReadTool(NullLogger<FileReadTool>.Instance),
            new FileWriteTool(NullLogger<FileWriteTool>.Instance),
            new FileSearchTool([new FakeFileSearchProvider("BuiltInRecursiveFileSearch")]),
            new FilePatchTool(NullLogger<FilePatchTool>.Instance),
        ];

        foreach (var tool in tools)
            Assert.IsTrue(ValidToolIdRegex().IsMatch(tool.Descriptor.ToolId), tool.Descriptor.ToolId);

        var registry = new PuddingToolRegistry(tools);
        var schema = new PuddingToolSchemaService(registry);
        foreach (var llmTool in schema.BuildLlmTools(null))
            Assert.IsTrue(ValidToolIdRegex().IsMatch(llmTool.Name), llmTool.Name);
    }

    [TestMethod]
    public async Task FilePatchTool_Applies_Text_And_Regex_Replacements()
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "temp", "file-patch-tests");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, "alpha beta beta\r\ncount=1\r\nLine 42: hello\r\n");

        try
        {
            var tool = new FilePatchTool(NullLogger<FilePatchTool>.Instance);
            var result = await tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "call-1",
                ArgumentsJson = $$"""
                {
                  "path": "{{JsonEscape(file)}}",
                  "operations": [
                    { "type": "replace", "old_text": "beta", "new_text": "gamma", "replace_all": true },
                    { "type": "regexReplace", "pattern": "count=\\d+", "replacement": "count=2" },
                    { "type": "regexReplace", "pattern": "Line (\\d+): (\\w+)", "replacement": "Line $1 => $2" }
                  ],
                  "dry_run": false
                }
                """,
                Context = SampleContext(),
            });

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual("alpha gamma gamma\r\ncount=2\r\nLine 42 => hello\r\n", await File.ReadAllTextAsync(file));
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    [TestMethod]
    public void FileSearchTool_Descriptor_Includes_Action_And_Provider_Parameters()
    {
        var tool = new FileSearchTool([new FakeFileSearchProvider("BuiltInRecursiveFileSearch")]);

        var parameters = tool.Descriptor.Parameters.Properties.Select(p => p.Name).ToArray();

        CollectionAssert.Contains(parameters, "action");
        CollectionAssert.Contains(parameters, "provider");
        StringAssert.Contains(tool.Descriptor.Description, "absolute");
        StringAssert.Contains(tool.Descriptor.Description, "available drive roots");
    }

    [TestMethod]
    public async Task FileSearchTool_Lists_Available_Providers()
    {
        var tool = new FileSearchTool(
            [
                new FakeFileSearchProvider("BuiltInRecursiveFileSearch"),
                new FakeFileSearchProvider("Everything"),
            ]);

        var result = await ExecuteFileSearchAsync(tool, """{"action":"list"}""");

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "BuiltInRecursiveFileSearch");
        StringAssert.Contains(result.Output, "Everything");
    }

    [TestMethod]
    public async Task FileSearchTool_Defaults_To_BuiltIn_Provider_For_Directory_Search()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "temp", "file-search-default-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "default-provider.py");
            await File.WriteAllTextAsync(file, "print('ok')");
            var provider = new FakeFileSearchProvider("BuiltInRecursiveFileSearch")
                .WithItem(file);
            var tool = new FileSearchTool([provider]);

            var result = await ExecuteFileSearchAsync(tool, $$"""
            {
              "pattern": "*.py",
              "directory": "{{JsonEscape(directory)}}",
              "recursive": true,
              "max_results": 1
            }
            """);

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(1, provider.SearchCallCount);
            Assert.AreEqual(1, provider.LastMaxResults);
            StringAssert.Contains(result.Output, "default-provider.py");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task FileSearchTool_Rejects_Empty_Directory_For_BuiltIn_Provider()
    {
        var provider = new FakeFileSearchProvider("BuiltInRecursiveFileSearch");
        var tool = new FileSearchTool([provider]);

        var result = await ExecuteFileSearchAsync(tool, """
        {
          "provider": "BuiltInRecursiveFileSearch",
          "directory": ""
        }
        """);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Directory is required");
        StringAssert.Contains(result.Error, "provider=Everything");
        Assert.AreEqual(0, provider.SearchCallCount);
    }

    [TestMethod]
    public async Task FileSearchTool_Rejects_Empty_Directory_For_Everything_Provider_With_Guidance()
    {
        var provider = new FakeFileSearchProvider("Everything");
        var tool = new FileSearchTool([provider]);

        var result = await ExecuteFileSearchAsync(tool, """
        {
          "provider": "Everything",
          "pattern": "*.txt"
        }
        """);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "absolute directory");
        StringAssert.Contains(result.Error, "Available drive roots");
        StringAssert.Contains(result.Error, ExpectedCurrentDriveRoot());
        Assert.AreEqual(0, provider.SearchCallCount);
    }

    [TestMethod]
    public async Task FileSearchTool_Rejects_Relative_Directory_For_Everything_Provider_With_Guidance()
    {
        var provider = new FakeFileSearchProvider("Everything");
        var tool = new FileSearchTool([provider]);

        var result = await ExecuteFileSearchAsync(tool, """
        {
          "provider": "Everything",
          "directory": "Source",
          "pattern": "*.cs"
        }
        """);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Everything requires an absolute directory");
        StringAssert.Contains(result.Error, "BuiltInRecursiveFileSearch");
        StringAssert.Contains(result.Error, "Available drive roots");
        StringAssert.Contains(result.Error, ExpectedCurrentDriveRoot());
        Assert.AreEqual(0, provider.SearchCallCount);
    }

    [TestMethod]
    public async Task FileSearchTool_Rejects_Missing_Directory_Before_Provider_Call()
    {
        var provider = new FakeFileSearchProvider("BuiltInRecursiveFileSearch");
        var tool = new FileSearchTool([provider]);
        var missingDirectory = Path.Combine(Path.GetTempPath(), "pudding-missing-" + Guid.NewGuid().ToString("N"));

        var result = await ExecuteFileSearchAsync(tool, $$"""
        {
          "provider": "BuiltInRecursiveFileSearch",
          "directory": "{{JsonEscape(missingDirectory)}}"
        }
        """);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Directory not found");
        Assert.AreEqual(0, provider.SearchCallCount);
    }

    [TestMethod]
    public async Task FileSearchTool_Allows_Outside_Workspace_Search_With_Warning()
    {
        var externalDirectory = Path.Combine(Path.GetTempPath(), "pudding-file-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalDirectory);
        try
        {
            var externalFile = Path.Combine(externalDirectory, "outside.txt");
            await File.WriteAllTextAsync(externalFile, "outside");
            var provider = new FakeFileSearchProvider("BuiltInRecursiveFileSearch")
                .WithItem(externalFile);
            var tool = new FileSearchTool([provider]);

            var result = await ExecuteFileSearchAsync(tool, $$"""
            {
              "provider": "BuiltInRecursiveFileSearch",
              "directory": "{{JsonEscape(externalDirectory)}}",
              "pattern": "*.txt"
            }
            """);

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "outside.txt");
        }
        finally
        {
            if (Directory.Exists(externalDirectory))
                Directory.Delete(externalDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task FileSearchTool_Returns_Fail_When_Provider_Throws()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "temp", "file-search-throw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var provider = new FakeFileSearchProvider("BuiltInRecursiveFileSearch")
                .ThrowOnSearch(new InvalidOperationException("provider boom"));
            var tool = new FileSearchTool([provider]);

            var result = await ExecuteFileSearchAsync(tool, $$"""
            {
              "provider": "BuiltInRecursiveFileSearch",
              "directory": "{{JsonEscape(directory)}}"
            }
            """);

            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Error, "BuiltInRecursiveFileSearch");
            StringAssert.Contains(result.Error, "provider boom");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task FileSearchTool_Returns_Guidance_When_Search_Has_No_Results()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "temp", "file-search-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var provider = new FakeFileSearchProvider("Everything");
            var tool = new FileSearchTool([provider]);

            var result = await ExecuteFileSearchAsync(tool, $$"""
            {
              "provider": "Everything",
              "directory": "{{JsonEscape(directory)}}",
              "pattern": "*.definitely-missing"
            }
            """);

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "[]");
            StringAssert.Contains(result.Output, "No files matched");
            StringAssert.Contains(result.Output, "absolute directory");
            StringAssert.Contains(result.Output, "Available drive roots");
            StringAssert.Contains(result.Output, ExpectedCurrentDriveRoot());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task EverythingSearchProvider_Filters_Sdk_Results_To_Requested_Directory_And_Pattern()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-everything-provider-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "nested");
        var outside = Path.Combine(Path.GetTempPath(), "pudding-everything-provider-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(outside);

        try
        {
            var directMatch = Path.Combine(root, "agent.cs");
            var nestedMatch = Path.Combine(nested, "runtime.cs");
            var wrongPattern = Path.Combine(root, "notes.txt");
            var outsideMatch = Path.Combine(outside, "outside.cs");

            var sdk = new FakeEverythingSdk(
                new EverythingQueryItem(directMatch),
                new EverythingQueryItem(nestedMatch),
                new EverythingQueryItem(wrongPattern),
                new EverythingQueryItem(outsideMatch));
            var provider = new EverythingSearchProvider(sdk);

            var results = await provider.SearchAsync(root, "*.cs", recursive: true, maxResults: 10, CancellationToken.None);

            CollectionAssert.AreEquivalent(new[] { directMatch, nestedMatch }, results.ToArray());
            Assert.AreEqual(root, sdk.LastRequest?.Directory);
            Assert.AreEqual("*.cs", sdk.LastRequest?.Pattern);
            Assert.AreEqual(10, sdk.LastRequest?.MaxResults);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            if (Directory.Exists(outside))
                Directory.Delete(outside, recursive: true);
        }
    }

    [TestMethod]
    public async Task EverythingSearchProvider_NonRecursive_Returns_Only_Direct_Children()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-everything-flat-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);

        try
        {
            var directMatch = Path.Combine(root, "agent.cs");
            var nestedMatch = Path.Combine(nested, "runtime.cs");
            var sdk = new FakeEverythingSdk(
                new EverythingQueryItem(directMatch),
                new EverythingQueryItem(nestedMatch));
            var provider = new EverythingSearchProvider(sdk);

            var results = await provider.SearchAsync(root, "*.cs", recursive: false, maxResults: 10, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { directMatch }, results.ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FileSearchTool_Falls_Back_To_BuiltIn_When_Everything_Sdk_Unavailable()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "temp", "file-search-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "fallback.cs");
            await File.WriteAllTextAsync(file, "class Fallback {}");
            var sdk = new FakeEverythingSdk([], available: false);
            var everything = new EverythingSearchProvider(sdk);
            var builtIn = new FakeFileSearchProvider("BuiltInRecursiveFileSearch").WithItem(file);
            var tool = new FileSearchTool([everything, builtIn]);

            var result = await ExecuteFileSearchAsync(tool, $$"""
            {
              "pattern": "*.cs",
              "directory": "{{JsonEscape(directory)}}"
            }
            """);

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(0, sdk.QueryCallCount);
            Assert.AreEqual(1, builtIn.SearchCallCount);
            StringAssert.Contains(result.Output, "fallback.cs");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void PuddingToolSchemaService_Converts_Descriptors_To_Llm_Tool_Definitions()
    {
        var registry = new PuddingToolRegistry([new SampleSearchTool()]);
        var schema = new PuddingToolSchemaService(registry);

        var tools = schema.BuildLlmTools(new CapabilityPolicy
        {
            DefaultToolNames = ["sample_search"],
        });

        Assert.AreEqual(1, tools.Count);
        Assert.AreEqual("sample_search", tools[0].Name);
        Assert.AreEqual("Searches sample data.", tools[0].Description);
        Assert.AreEqual("query", tools[0].Parameters.Required[0]);
    }

    [TestMethod]
    public void ServiceCollectionExtension_Registers_Explicit_AgentSkill_Adapter_In_Tool_Catalog()
    {
        var services = new ServiceCollection();
        services.AddPuddingTool<SampleSearchTool>();
        services.AddPuddingAgentTool<LegacySkillTool>();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IPuddingToolCatalogService>();

        var descriptors = catalog.ListTools();

        CollectionAssert.AreEquivalent(
            new[] { "legacy_echo", "list_tool_approvals", "request_tool_approval", "sample_search" },
            descriptors.Select(d => d.ToolId).ToArray());
    }

    [TestMethod]
    public void ServiceCollectionExtension_Registers_MessageTools_With_Unambiguous_Factories()
    {
        var services = new ServiceCollection();
        services.AddPuddingAgentTool<SendMessageTool>();
        services.AddPuddingAgentTool<ReceiveMessagesTool>();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IPuddingToolRegistry>();

        Assert.IsNotNull(provider.GetRequiredService<SendMessageTool>());
        Assert.IsNotNull(provider.GetRequiredService<ReceiveMessagesTool>());
        Assert.IsNotNull(registry.GetTool("send_message"));
        Assert.IsNotNull(registry.GetTool("receive_messages"));
    }

    [TestMethod]
    public void ServiceCollectionExtension_Registers_MemoryToolHandlers_For_ManageMemoryTool()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MemoryLibraryDbContext>()
            .UseSqlite(connection)
            .Options;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<MemoryLibraryDbContext>>(
            new TestMemoryLibraryDbContextFactory(options));
        services.AddSingleton<IMemoryLibrary, MemoryLibrary>();
        services.AddPuddingTool<ManageMemoryTool>();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IPuddingToolRegistry>();

        Assert.IsNotNull(provider.GetRequiredService<ManageMemoryTool>());
        Assert.IsInstanceOfType<ManageMemoryTool>(registry.GetTool("manage_memory"));
    }

    [TestMethod]
    public void ServiceCollectionExtension_Does_Not_Implicitly_Register_IAgentSkill_As_Agent_Tool()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentSkill, LegacySkillTool>();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IPuddingToolRegistry>();

        Assert.IsNull(registry.GetTool("legacy_echo"));
    }

    [TestMethod]
    public void ServiceCollectionExtension_Registers_Migrated_Runtime_Tools_As_Native_Tools()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddPuddingAgentTool<HttpFetchSkill>();
        services.AddPuddingAgentTool<TaskManagerTool>();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IPuddingToolRegistry>();

        Assert.IsInstanceOfType<HttpFetchSkill>(registry.GetTool("http_fetch"));
        Assert.IsInstanceOfType<TaskManagerTool>(registry.GetTool("manage_tasks"));
        Assert.AreSame(provider.GetRequiredService<HttpFetchSkill>(), registry.GetTool("http_fetch"));
        Assert.AreSame(provider.GetRequiredService<TaskManagerTool>(), registry.GetTool("manage_tasks"));
    }

    [TestMethod]
    public async Task HttpFetchSkill_Markdown_Output_Extracts_Readable_Html()
    {
        var html = """
                   <html>
                     <body>
                       <nav>navigation noise</nav>
                       <main>
                         <h1>Article Title</h1>
                         <p>Hello <a href="https://example.com/docs">docs</a>.</p>
                         <script>alert('noise')</script>
                       </main>
                     </body>
                   </html>
                   """;
        var converter = new RecordingHtmlToMarkdownConverter("# Article Title\n\nHello [docs](https://example.com/docs).");
        var webClient = new RecordingWebClient(new WebClientResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            ContentType = "text/html; charset=utf-8",
            Body = html,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            FinalUrl = "https://example.com/article",
        });
        var tool = new HttpFetchSkill(
            webClient,
            new HttpFetchContentFormatter(converter),
            NullLogger<HttpFetchSkill>.Instance);

        var result = await ExecuteHttpFetchAsync(tool, """
            {"url":"https://example.com/article","output_format":"markdown"}
            """);

        Assert.IsTrue(result.Success);
        StringAssert.Contains(result.Output, "HTTP 200 OK");
        StringAssert.Contains(result.Output, "# Article Title");
        Assert.IsNotNull(converter.LastHtml);
        StringAssert.Contains(converter.LastHtml!, "Article Title");
        Assert.IsFalse(converter.LastHtml!.Contains("<nav>", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(converter.LastHtml!.Contains("<script", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task HttpFetchSkill_Text_Output_Removes_Html_Noise()
    {
        var webClient = new RecordingWebClient(new WebClientResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            ContentType = "text/html",
            Body = "<html><body><header>skip</header><article><h1>Title</h1><p>First paragraph.</p></article></body></html>",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            FinalUrl = "https://example.com/text",
        });
        var tool = CreateHttpFetchSkill(webClient);

        var result = await ExecuteHttpFetchAsync(tool, """
            {"url":"https://example.com/text","output_format":"text"}
            """);

        Assert.IsTrue(result.Success);
        StringAssert.Contains(result.Output, "Title");
        StringAssert.Contains(result.Output, "First paragraph.");
        Assert.IsFalse(result.Output!.Contains("skip", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Output.Contains("<article>", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task HttpFetchSkill_Raw_Output_Preserves_Response_Body()
    {
        var body = "<html><body><script>keep raw</script><p>Raw body</p></body></html>";
        var webClient = new RecordingWebClient(new WebClientResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            ContentType = "text/html",
            Body = body,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            FinalUrl = "https://example.com/raw",
        });
        var tool = CreateHttpFetchSkill(webClient);

        var result = await ExecuteHttpFetchAsync(tool, """
            {"url":"https://example.com/raw","output_format":"raw"}
            """);

        Assert.IsTrue(result.Success);
        StringAssert.Contains(result.Output, body);
    }

    [TestMethod]
    public async Task HttpFetchSkill_Json_Output_Includes_Metadata_Headers_And_Truncation()
    {
        var webClient = new RecordingWebClient(new WebClientResponse
        {
            StatusCode = 201,
            ReasonPhrase = "Created",
            ContentType = "application/json",
            Body = """{"message":"abcdef"}""",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-request-id"] = "req-1",
            },
            FinalUrl = "https://api.example.com/items",
        });
        var tool = CreateHttpFetchSkill(webClient);

        var result = await ExecuteHttpFetchAsync(tool, """
            {"url":"https://api.example.com/items","method":"POST","headers":{"Authorization":"Bearer token"},"body":"{}","content_type":"application/json","timeout_seconds":12,"output_format":"json","max_response_chars":8,"include_headers":true,"cookie_scope":"session"}
            """);

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(webClient.LastRequest);
        Assert.AreEqual("POST", webClient.LastRequest!.Method);
        Assert.AreEqual("Bearer token", webClient.LastRequest.Headers["Authorization"]);
        Assert.AreEqual(12, webClient.LastRequest.TimeoutSeconds);
        Assert.AreEqual("session", webClient.LastRequest.CookieScope);
        Assert.AreEqual("workspace-1:session-1:agent-1", webClient.LastRequest.CookieKey);

        using var doc = JsonDocument.Parse(result.Output!);
        var root = doc.RootElement;
        Assert.AreEqual(201, root.GetProperty("status_code").GetInt32());
        Assert.AreEqual("Created", root.GetProperty("reason_phrase").GetString());
        Assert.IsTrue(root.GetProperty("success").GetBoolean());
        Assert.IsTrue(root.GetProperty("truncated").GetBoolean());
        Assert.AreEqual("""{"messag""", root.GetProperty("body").GetString());
        Assert.AreEqual("req-1", root.GetProperty("headers").GetProperty("x-request-id").GetString());
    }

    [TestMethod]
    public async Task HttpFetchSkill_Rejects_Non_Http_Urls_Before_Transport()
    {
        var webClient = new RecordingWebClient(new WebClientResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            ContentType = "text/plain",
            Body = "should not call",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            FinalUrl = "file:///etc/passwd",
        });
        var tool = CreateHttpFetchSkill(webClient);

        var result = await ExecuteHttpFetchAsync(tool, """
            {"url":"file:///etc/passwd"}
            """);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Only http:// and https:// URLs are supported.");
        Assert.IsNull(webClient.LastRequest);
    }

    [TestMethod]
    public void RequestToolApprovalTool_Is_Auto_Exposed_Without_Template_Grant()
    {
        var services = new ServiceCollection();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IPuddingToolRegistry>();

        var toolIds = registry.ListAvailable(new CapabilityPolicy()).Select(d => d.ToolId).ToArray();

        CollectionAssert.Contains(toolIds, "request_tool_approval");
    }

    [TestMethod]
    public void ServiceCollectionExtension_Registers_Fake_Tool_Approval_Reviewer()
    {
        var services = new ServiceCollection();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var reviewer = provider.GetRequiredService<IToolApprovalReviewer>();

        Assert.IsInstanceOfType<FakeToolApprovalReviewer>(reviewer);
    }

    [TestMethod]
    public void ServiceCollectionExtension_Uses_Llm_Tool_Approval_Reviewer_When_Explicitly_Configured()
    {
        var services = new ServiceCollection();
        services.Configure<ToolApprovalRuntimeOptions>(options => options.Reviewer = "llm");
        services.AddSingleton<IToolApprovalLlmClient>(new RecordingToolApprovalLlmClient("""
        {
          "decision": "need_human",
          "reason": "test"
        }
        """));
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var reviewer = provider.GetRequiredService<IToolApprovalReviewer>();

        Assert.IsInstanceOfType<LlmToolApprovalReviewer>(reviewer);
    }

    [TestMethod]
    public void ServiceCollectionExtension_Requires_Audit_Agent_When_Configured()
    {
        var services = new ServiceCollection();
        services.Configure<ToolApprovalRuntimeOptions>(options =>
        {
            options.Reviewer = "fake";
            options.RequireAuditAgent = true;
        });
        services.AddSingleton<IToolApprovalLlmClient>(new RecordingToolApprovalLlmClient("""
        {
          "decision": "need_human",
          "reason": "test"
        }
        """));
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var reviewer = provider.GetRequiredService<IToolApprovalReviewer>();

        Assert.IsInstanceOfType<LlmToolApprovalReviewer>(reviewer);
    }

    [TestMethod]
    public void ServiceCollectionExtension_Binds_Tool_Approval_Reviewer_From_Configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ToolApproval:Reviewer"] = "llm",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IToolApprovalLlmClient>(new RecordingToolApprovalLlmClient("""
        {
          "decision": "need_human",
          "reason": "test"
        }
        """));
        services.AddPuddingToolRegistry(configuration);

        using var provider = services.BuildServiceProvider();
        var reviewer = provider.GetRequiredService<IToolApprovalReviewer>();

        Assert.IsInstanceOfType<LlmToolApprovalReviewer>(reviewer);
    }

    [TestMethod]
    public void ServiceCollectionExtension_Rejects_Unknown_Tool_Approval_Reviewer_Config()
    {
        var services = new ServiceCollection();
        services.Configure<ToolApprovalRuntimeOptions>(options => options.Reviewer = "subconscious");
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            provider.GetRequiredService<IToolApprovalReviewer>());
    }

    [TestMethod]
    public void ServiceCollectionExtension_Discovers_Native_Tools_From_Assembly()
    {
        var services = new ServiceCollection();
        services.AddPuddingToolsFromAssembly(typeof(AssemblyDiscoveredTool).Assembly);
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IPuddingToolCatalogService>();

        var descriptor = catalog.ListTools()
            .SingleOrDefault(t => t.ToolId == "assembly_discovered");

        Assert.IsNotNull(descriptor);
        Assert.AreEqual("Assembly discovered", descriptor!.Name);
    }

    [TestMethod]
    public void ServiceCollectionExtension_Does_Not_Duplicate_Explicit_Native_Tool_When_Assembly_Scanned()
    {
        var services = new ServiceCollection();
        services.AddPuddingAgentTool<AssemblyDiscoveredTool>();
        services.AddPuddingToolsFromAssembly(typeof(AssemblyDiscoveredTool).Assembly);
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IPuddingToolCatalogService>();

        Assert.AreEqual(
            1,
            catalog.ListTools().Count(t => t.ToolId == "assembly_discovered"));
    }

    [TestMethod]
    public async Task PuddingToolExecutionService_Executes_Allowed_Tool()
    {
        var registry = new PuddingToolRegistry([new SampleSearchTool()]);
        var executor = new PuddingToolExecutionService(
            registry,
            new SandboxExecutor(NullLogger<SandboxExecutor>.Instance),
            NullLogger<PuddingToolExecutionService>.Instance);

        var result = await executor.ExecuteAsync(
            "sample_search",
            """{"query":"pudding"}""",
            SampleContext(),
            new CapabilityPolicy { DefaultToolNames = ["sample_search"] });

        Assert.IsTrue(result.Success);
        Assert.AreEqual("query=pudding", result.Output);
    }

    [TestMethod]
    public async Task PuddingToolExecutionService_Records_Telemetry_For_Tool_Result()
    {
        var telemetry = new RecordingTelemetrySink();
        var registry = new PuddingToolRegistry([new SampleSearchTool()]);
        var executor = new PuddingToolExecutionService(
            registry,
            new SandboxExecutor(NullLogger<SandboxExecutor>.Instance),
            NullLogger<PuddingToolExecutionService>.Instance,
            telemetrySink: telemetry);

        var context = SampleContext() with
        {
            Trace = RuntimeTraceContext.CreateNew(
                sessionId: "session-1",
                workspaceId: "workspace-1",
                executionId: "execution-1"),
        };

        var result = await executor.ExecuteAsync(
            "sample_search",
            """{"query":"pudding"}""",
            context,
            new CapabilityPolicy { DefaultToolNames = ["sample_search"] });

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, telemetry.Metrics.Count);
        var metric = telemetry.Metrics[0];
        Assert.AreEqual(TelemetryMetricCategories.Tool, metric.Category);
        Assert.AreEqual("tool.execution", metric.Name);
        Assert.AreEqual(TelemetryMetricStatuses.Succeeded, metric.Status);
        Assert.AreEqual("sample_search", metric.Dimensions!["tool_name"]);
        Assert.AreEqual(RuntimePipelineStages.Tool, metric.Dimensions["stage"]);
        Assert.AreEqual("execute", metric.Dimensions["tool_stage"]);
        Assert.AreEqual("13", metric.Dimensions["output_char_count"]);
        Assert.AreEqual("1", metric.Dimensions["output_line_count"]);
        Assert.AreEqual("0", metric.Dimensions["error_char_count"]);
        Assert.AreEqual("0", metric.Dimensions["error_line_count"]);
        Assert.AreEqual("13", metric.Dimensions["total_text_char_count"]);
        Assert.AreEqual("1", metric.Dimensions["total_text_line_count"]);
        Assert.AreEqual("normal", metric.Dimensions["output_size_level"]);
        Assert.AreEqual("workspace-1", metric.Trace.WorkspaceId);
        Assert.AreEqual("session-1", metric.Trace.SessionId);
    }

    [TestMethod]
    public async Task PuddingToolExecutionService_Classifies_Oversized_Tool_Output()
    {
        var telemetry = new RecordingTelemetrySink();
        var registry = new PuddingToolRegistry([new SampleLargeOutputTool()]);
        var executor = new PuddingToolExecutionService(
            registry,
            new SandboxExecutor(NullLogger<SandboxExecutor>.Instance),
            NullLogger<PuddingToolExecutionService>.Instance,
            telemetrySink: telemetry);

        var result = await executor.ExecuteAsync(
            "sample_large_output",
            "{}",
            SampleContext(),
            new CapabilityPolicy { DefaultToolNames = ["sample_large_output"] });

        Assert.IsTrue(result.Success);
        var metric = telemetry.Metrics.Single();
        Assert.AreEqual("warning", metric.Dimensions!["output_size_level"]);
        Assert.AreEqual("9000", metric.Dimensions["output_char_count"]);
        Assert.AreEqual("9000", metric.Dimensions["total_text_char_count"]);
    }

    [TestMethod]
    public async Task PuddingToolExecutionService_Blocks_Unlisted_Medium_Tool()
    {
        var registry = new PuddingToolRegistry([new SampleMediumTool()]);
        var executor = new PuddingToolExecutionService(
            registry,
            new SandboxExecutor(NullLogger<SandboxExecutor>.Instance),
            NullLogger<PuddingToolExecutionService>.Instance);

        var result = await executor.ExecuteAsync(
            "sample_medium",
            "{}",
            SampleContext(),
            new CapabilityPolicy());

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Tool 'sample_medium' is not allowed by the agent's capability policy.");
    }

    [TestMethod]
    public async Task PuddingToolExecutionService_Blocks_High_Tool_Without_Grant_Authorization()
    {
        var registry = new PuddingToolRegistry([new SampleHighTool()]);
        var executor = new PuddingToolExecutionService(
            registry,
            new SandboxExecutor(NullLogger<SandboxExecutor>.Instance),
            NullLogger<PuddingToolExecutionService>.Instance);

        var result = await executor.ExecuteAsync(
            "sample_high",
            "{}",
            SampleContext(),
            new CapabilityPolicy());

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Tool 'sample_high' is not allowed by the agent's capability policy.");
    }

    [TestMethod]
    public async Task PuddingToolExecutionService_Allows_High_Tool_With_Grant_Authorization()
    {
        var registry = new PuddingToolRegistry([new SampleHighTool()]);
        var executor = new PuddingToolExecutionService(
            registry,
            new SandboxExecutor(NullLogger<SandboxExecutor>.Instance),
            NullLogger<PuddingToolExecutionService>.Instance);

        var result = await executor.ExecuteAsync(
            "sample_high",
            "{}",
            SampleContext(),
            new CapabilityPolicy { RequiresGrantToolNames = ["sample_high"] });

        Assert.IsTrue(result.Success);
        Assert.AreEqual("executed high", result.Output);
    }

    [TestMethod]
    public async Task PuddingToolExecutionService_Blocks_High_Tool_Without_Runtime_Authorization()
    {
        var registry = new PuddingToolRegistry([new SampleHighTool()]);
        var executor = new PuddingToolExecutionService(
            registry,
            new SandboxExecutor(NullLogger<SandboxExecutor>.Instance),
            NullLogger<PuddingToolExecutionService>.Instance,
            authorizationService: new InMemoryToolAuthorizationService());

        var result = await executor.ExecuteAsync(
            "sample_high",
            "{}",
            SampleContext() with
            {
                Trace = RuntimeTraceContext.CreateNew(userId: "user-1"),
            },
            new CapabilityPolicy { RequiresGrantToolNames = ["sample_high"] });

        Assert.IsFalse(result.Success);
        Assert.AreEqual(403, result.ExitCode);
        StringAssert.Contains(result.Error, "Runtime approval required");
        StringAssert.Contains(result.Error, "request_tool_approval");
        StringAssert.Contains(result.Error, "Recommended next step");
        StringAssert.Contains(result.Error, "tool_id='sample_high'");
        StringAssert.Contains(result.Error, "/authorize sample_high 10m");
    }

    [TestMethod]
    public async Task PuddingToolExecutionService_Allows_High_Tool_With_Runtime_Authorization()
    {
        var authorization = new InMemoryToolAuthorizationService();
        await authorization.ApplyCommandAsync(
            new ToolAuthorizationCommand
            {
                RawText = "/authorize sample_high session",
                Action = ToolAuthorizationAction.Authorize,
                ToolId = "sample_high",
                Scope = ToolAuthorizationScope.Session,
            },
            new ToolAuthorizationContext
            {
                WorkspaceId = "workspace-1",
                SessionId = "session-1",
                AgentInstanceId = "agent-1",
                UserId = "user-1",
                ToolId = "sample_high",
            });

        var registry = new PuddingToolRegistry([new SampleHighTool()]);
        var executor = new PuddingToolExecutionService(
            registry,
            new SandboxExecutor(NullLogger<SandboxExecutor>.Instance),
            NullLogger<PuddingToolExecutionService>.Instance,
            authorizationService: authorization);

        var result = await executor.ExecuteAsync(
            "sample_high",
            "{}",
            SampleContext() with
            {
                Trace = RuntimeTraceContext.CreateNew(userId: "user-1"),
            },
            new CapabilityPolicy { RequiresGrantToolNames = ["sample_high"] });

        Assert.IsTrue(result.Success);
        Assert.AreEqual("executed high", result.Output);
    }

    [TestMethod]
    public async Task PuddingToolExecutionService_Allows_High_Tool_With_Auto_Approval()
    {
        var approval = new InMemoryToolApprovalService();
        var descriptor = new SampleHighTool().Descriptor;
        var identity = SampleApprovalIdentity();
        var plannedArguments = "{}";
        var submit = await approval.SubmitAsync(
            ValidApprovalRequest(plannedArguments),
            identity,
            descriptor);
        Assert.AreEqual(ToolApprovalDecision.Approved, submit.Decision, submit.DecisionReason);

        var registry = new PuddingToolRegistry([new SampleHighTool()]);
        var executor = new PuddingToolExecutionService(
            registry,
            new SandboxExecutor(NullLogger<SandboxExecutor>.Instance),
            NullLogger<PuddingToolExecutionService>.Instance,
            authorizationService: new InMemoryToolAuthorizationService(),
            approvalService: approval);

        var result = await executor.ExecuteAsync(
            "sample_high",
            plannedArguments,
            SampleContext() with
            {
                Trace = RuntimeTraceContext.CreateNew(userId: "user-1"),
            },
            new CapabilityPolicy { RequiresGrantToolNames = ["sample_high"] });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("executed high", result.Output);
    }

    [TestMethod]
    public async Task RequestToolApprovalTool_Uses_Audit_Agent_Llm_And_Allows_Exact_High_Risk_Call()
    {
        var invocation = new RecordingLlmInvocationService("""
        {
          "decision": "approved",
          "reason": "Audit agent approved the exact high-risk sample call.",
          "reviewerModel": "approval-model"
        }
        """);
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<ToolApprovalRuntimeOptions>(options => options.Reviewer = "llm");
        services.AddSingleton<ILlmInvocationService>(invocation);
        services.AddSingleton<IWorkspaceAuditAgentProvider>(new StaticWorkspaceAuditAgentProvider(new WorkspaceAuditAgentProfile
        {
            WorkspaceId = "workspace-1",
            AgentInstanceId = "audit-agent-1",
            AgentTemplateId = "workspace-audit-agent",
            ProviderId = "approval-provider",
            ProfileId = "approval.default",
            ModelId = "approval-model",
        }));
        services.AddPuddingTool<SampleHighTool>();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var requestApprovalTool = provider.GetServices<IPuddingTool>()
            .Single(t => t.Descriptor.ToolId == "request_tool_approval");
        var context = SampleContext() with
        {
            Trace = RuntimeTraceContext.CreateNew(userId: "user-1"),
        };

        var approvalResult = await requestApprovalTool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "approval-call-1",
            Context = context,
            ArgumentsJson = """
            {
              "tool_id": "sample_high",
              "command_name": "sample_high",
              "purpose": "Request approval for the exact high-risk sample call.",
              "necessity": "The integration path must verify audit-agent-backed approval before execution.",
              "fact_basis": ["The sample high tool descriptor is registered in this test service provider."],
              "requested_arguments_json": "{}",
              "target_resources": ["sample_high"],
              "authorized_area": ["workspace-1"],
              "may_damage_or_delete_data": false,
              "is_irreversible_operation": false,
              "backup_taken": false,
              "rollback_plan": "No mutation is expected.",
              "operation_context": "MSTest local runtime test context.",
              "operation_plan": "Submit an approval ticket, then execute the exact same high-risk tool call.",
              "operation_steps": [
                {
                  "step_number": 1,
                  "command": "sample_high {}",
                  "working_directory": "E:/github/AgentNetworkPlan/PuddingAgent",
                  "environment": "MSTest local runtime test",
                  "target_object": "sample_high",
                  "purpose": "Verify the approved high-risk call can execute.",
                  "expected_effect": "The sample high-risk tool returns its test output.",
                  "reasonableness": "The execution arguments exactly match requested_arguments_json.",
                  "safety_check_before": "Confirm the tool id and arguments match the ticket.",
                  "stop_condition": "Stop if the actual call arguments differ.",
                  "rollback_for_step": "No source or data mutation is expected."
                }
              ],
              "may_expose_secrets": false,
              "user_consent_status": "implied",
              "alternatives_considered": ["No lower-risk call exercises the approval-to-execution path."],
              "requested_scope": "once",
              "risk_notes": "No destructive behavior is present."
            }
            """,
        });

        Assert.IsTrue(approvalResult.Success, approvalResult.Error);
        StringAssert.Contains(approvalResult.Output, "\"decision\": \"approved\"");
        Assert.IsNotNull(invocation.LastRequest);
        Assert.AreEqual("audit-agent-1", invocation.LastRequest!.AgentInstanceId);
        Assert.AreEqual("workspace-audit-agent", invocation.LastRequest.AgentTemplateId);

        var executor = new PuddingToolExecutionService(
            provider.GetRequiredService<IPuddingToolRegistry>(),
            new SandboxExecutor(NullLogger<SandboxExecutor>.Instance),
            NullLogger<PuddingToolExecutionService>.Instance,
            authorizationService: new InMemoryToolAuthorizationService(),
            approvalService: provider.GetRequiredService<IToolApprovalService>());

        var executionResult = await executor.ExecuteAsync(
            "sample_high",
            "{}",
            context,
            new CapabilityPolicy { RequiresGrantToolNames = ["sample_high"] });

        Assert.IsTrue(executionResult.Success, executionResult.Error);
        Assert.AreEqual("executed high", executionResult.Output);
    }

    [TestMethod]
    public async Task ToolApprovalService_HardDenies_Irreversible_Operation_Without_Backup_Or_Rollback()
    {
        var approval = new InMemoryToolApprovalService(new ThrowingToolApprovalReviewer());
        var request = ValidApprovalRequest("{}") with
        {
            MayDamageOrDeleteData = true,
            IsIrreversibleOperation = true,
            BackupTaken = false,
            RollbackPlan = "",
            OperationSteps =
            [
                ValidOperationSteps()[0] with
                {
                    Command = "format X:",
                    ExpectedEffect = "Formats a disk volume.",
                    RollbackForStep = "",
                },
            ],
            UserConsentStatus = ToolApprovalUserConsentStatus.Explicit,
        };

        var result = await approval.SubmitAsync(
            request,
            SampleApprovalIdentity(),
            new SampleHighTool().Descriptor);

        Assert.AreEqual(ToolApprovalDecision.Denied, result.Decision);
        Assert.AreEqual(ToolApprovalTicketStatus.Denied, result.Status);
        StringAssert.Contains(result.DecisionReason, "Hard safety denial");
        StringAssert.Contains(result.DecisionReason, "disk formatting");
    }

    [TestMethod]
    public async Task ToolApprovalService_HardDenies_File_Delete_Without_Backup_Or_Temporary_Evidence()
    {
        var approval = new InMemoryToolApprovalService(new ThrowingToolApprovalReviewer());
        var request = ValidApprovalRequest("""{"command":"Remove-Item important.txt","shell":"powershell"}""") with
        {
            ToolId = "shell",
            CommandName = "Remove-Item important.txt",
            MayDamageOrDeleteData = true,
            BackupTaken = false,
            TemporaryFileEvidence = "",
            RollbackPlan = "No rollback is available.",
            OperationSteps =
            [
                ValidOperationSteps()[0] with
                {
                    ToolId = "shell",
                    Command = "Remove-Item important.txt",
                    RequestedArgumentsJson = """{"command":"Remove-Item important.txt","shell":"powershell"}""",
                    ExpectedEffect = "Deletes important.txt.",
                },
            ],
        };

        var result = await approval.SubmitAsync(
            request,
            SampleApprovalIdentity(),
            new SampleHighTool().Descriptor with { ToolId = "shell" });

        Assert.AreEqual(ToolApprovalDecision.Denied, result.Decision);
        StringAssert.Contains(result.DecisionReason, "file deletion");
        StringAssert.Contains(result.DecisionReason, "backup plan");
    }

    [TestMethod]
    public async Task ToolApprovalService_Implicitly_Approves_Workspace_Temporary_File_Cleanup()
    {
        var approval = new InMemoryToolApprovalService();
        var identity = SampleApprovalIdentity();
        var descriptor = new SampleHighTool().Descriptor with { ToolId = "shell" };

        var check = await approval.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = identity.SessionId,
                AgentInstanceId = identity.AgentInstanceId,
                UserId = identity.UserId,
                ToolId = "shell",
                ActualArgumentsJson = """
                {
                  "command": "Remove-Item -Path E:\\github\\AgentNetworkPlan\\PuddingAgent\\data\\workspaces\\workspace-1\\test_sample.png, E:\\github\\AgentNetworkPlan\\PuddingAgent\\data\\workspaces\\workspace-1\\test_result.jpg -Force",
                  "shell": "powershell"
                }
                """,
            },
            descriptor);

        Assert.IsTrue(check.IsApproved, check.Message);
        Assert.AreEqual("ImplicitAudit", check.ApprovalSource);
    }

    [TestMethod]
    public async Task ToolApprovalService_FakeReviewer_Approves_Operation_Without_Detailed_Steps()
    {
        var approval = new InMemoryToolApprovalService();
        var request = ValidApprovalRequest("{}") with
        {
            OperationSteps = [],
        };

        var result = await approval.SubmitAsync(
            request,
            SampleApprovalIdentity(),
            new SampleHighTool().Descriptor);

        Assert.AreEqual(ToolApprovalDecision.Approved, result.Decision);
        StringAssert.Contains(result.DecisionReason, "fake automatic approval");
    }

    [TestMethod]
    public async Task ToolApprovalService_Uses_Reviewer_Decision()
    {
        var approval = new InMemoryToolApprovalService(new DenyingToolApprovalReviewer());

        var result = await approval.SubmitAsync(
            ValidApprovalRequest("{}"),
            SampleApprovalIdentity(),
            new SampleHighTool().Descriptor);

        Assert.AreEqual(ToolApprovalDecision.Denied, result.Decision);
        Assert.AreEqual(ToolApprovalTicketStatus.Denied, result.Status);
        StringAssert.Contains(result.DecisionReason, "reviewer denied");
    }

    [TestMethod]
    public void ToolApprovalPromptBuilder_Builds_Clean_Room_Request()
    {
        var prompts = ToolApprovalPromptBuilder.Build(
            ValidApprovalRequest("{}"),
            SampleApprovalIdentity(),
            new SampleHighTool().Descriptor);

        StringAssert.Contains(prompts.SystemPrompt, "single clean-room approval review");
        StringAssert.Contains(prompts.SystemPrompt, "Do not use chat history");
        StringAssert.Contains(prompts.UserPrompt, "\"toolId\": \"sample_high\"");
        StringAssert.Contains(prompts.UserPrompt, "\"workspaceId\": \"workspace-1\"");
        StringAssert.Contains(prompts.UserPrompt, "\"toolDescriptor\"");
        Assert.IsFalse(prompts.UserPrompt.Contains("chatMessages", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ToolApprovalReviewParser_Parses_Approved_Json()
    {
        var result = ToolApprovalReviewParser.Parse("""
        {
          "decision": "approved",
          "reason": "The request is scoped to one exact test command.",
          "allowedScope": "session",
          "allowedDurationMinutes": 10,
          "requiresHumanAuthorization": false,
          "checklistFindings": ["facts verified"],
          "missingRequirements": [],
          "allowlistProposals": [
            {
              "toolId": "shell",
              "command": "pwd",
              "argumentsJson": {"command":"pwd","shell":"auto"},
              "reason": "Exact read-only workspace inspection."
            }
          ],
          "recommendedFix": null
        }
        """);

        Assert.AreEqual(ToolApprovalDecision.Approved, result.Decision);
        Assert.AreEqual("The request is scoped to one exact test command.", result.DecisionReason);
        Assert.AreEqual(ToolApprovalScope.Session, result.AllowedScope);
        Assert.AreEqual(TimeSpan.FromMinutes(10), result.AllowedDuration);
        Assert.IsFalse(result.RequiresHumanAuthorization);
        CollectionAssert.Contains(result.ChecklistFindings.ToArray(), "facts verified");
        var proposal = result.AllowlistProposals.Single();
        Assert.AreEqual("shell", proposal.ToolId);
        Assert.AreEqual("pwd", proposal.Command);
        Assert.AreEqual("""{"command":"pwd","shell":"auto"}""", proposal.ArgumentsJson);
        Assert.AreEqual("Exact read-only workspace inspection.", proposal.Reason);
    }

    [TestMethod]
    public void ToolApprovalReviewParser_Returns_NeedHuman_For_Invalid_Json()
    {
        var result = ToolApprovalReviewParser.Parse("not json");

        Assert.AreEqual(ToolApprovalDecision.NeedHuman, result.Decision);
        StringAssert.Contains(result.DecisionReason, "Invalid approval reviewer JSON");
        Assert.IsTrue(result.RequiresHumanAuthorization);
    }

    [TestMethod]
    public async Task LlmToolApprovalReviewer_Uses_Clean_Prompt_And_Parses_Response()
    {
        var client = new RecordingToolApprovalLlmClient("""
        {
          "decision": "denied",
          "reason": "Missing rollback plan.",
          "requiresHumanAuthorization": true,
          "missingRequirements": ["rollback plan"],
          "recommendedFix": "Add a rollback plan."
        }
        """);
        var reviewer = new LlmToolApprovalReviewer(client);

        var result = await reviewer.ReviewAsync(
            ValidApprovalRequest("{}") with { RollbackPlan = "" },
            SampleApprovalIdentity(),
            new SampleHighTool().Descriptor);

        Assert.AreEqual(ToolApprovalDecision.Denied, result.Decision);
        Assert.AreEqual("Missing rollback plan.", result.DecisionReason);
        Assert.IsTrue(result.RequiresHumanAuthorization);
        StringAssert.Contains(client.SystemPrompt!, "single clean-room approval review");
        StringAssert.Contains(client.UserPrompt!, "\"toolId\": \"sample_high\"");
    }

    [TestMethod]
    public async Task InvocationToolApprovalLlmClient_Returns_NeedHuman_Without_Approval_Profile()
    {
        var invocation = new RecordingLlmInvocationService("""
        {
          "decision": "approved",
          "reason": "should not be called"
        }
        """);
        var client = new InvocationToolApprovalLlmClient(
            invocation,
            new StaticToolApprovalLlmProfileResolver(null),
            NullLogger<InvocationToolApprovalLlmClient>.Instance);

        var prompt = ToolApprovalPromptBuilder.Build(
            ValidApprovalRequest("{}"),
            SampleApprovalIdentity(),
            new SampleHighTool().Descriptor);

        var raw = await client.ReviewAsync(
            ValidApprovalRequest("{}"),
            SampleApprovalIdentity(),
            new SampleHighTool().Descriptor,
            prompt);
        var result = ToolApprovalReviewParser.Parse(raw);

        Assert.AreEqual(ToolApprovalDecision.NeedHuman, result.Decision);
        StringAssert.Contains(result.DecisionReason, "approval LLM profile is not configured");
        Assert.IsTrue(result.RequiresHumanAuthorization);
        Assert.IsNull(invocation.LastRequest);
    }

    [TestMethod]
    public async Task InvocationToolApprovalLlmClient_Returns_NeedHuman_When_Workspace_Has_No_Audit_Agent()
    {
        var invocation = new RecordingLlmInvocationService("should not be called");
        var resolver = new StrictConfiguredToolApprovalLlmProfileResolver(
            Options.Create(new ToolApprovalLlmOptions()),
            workspaceAuditAgentProvider: new StaticWorkspaceAuditAgentProvider(null));
        var client = new InvocationToolApprovalLlmClient(
            invocation,
            resolver,
            NullLogger<InvocationToolApprovalLlmClient>.Instance);

        var prompt = ToolApprovalPromptBuilder.Build(
            ValidApprovalRequest("{}"),
            SampleApprovalIdentity(),
            new SampleHighTool().Descriptor);

        var raw = await client.ReviewAsync(
            ValidApprovalRequest("{}"),
            SampleApprovalIdentity(),
            new SampleHighTool().Descriptor,
            prompt);
        var result = ToolApprovalReviewParser.Parse(raw);

        Assert.AreEqual(ToolApprovalDecision.NeedHuman, result.Decision);
        StringAssert.Contains(result.DecisionReason, "当前工作空间不具有审计类型的agent");
        Assert.IsTrue(result.RequiresHumanAuthorization);
        Assert.IsNull(invocation.LastRequest);
    }

    [TestMethod]
    public async Task InvocationToolApprovalLlmClient_Uses_Explicit_Approval_Profile()
    {
        var invocation = new RecordingLlmInvocationService("""
        {
          "decision": "approved",
          "reason": "Approval profile reviewed the exact request.",
          "reviewerModel": "approval-model"
        }
        """);
        var profile = new ToolApprovalLlmProfile
        {
            ProviderId = "approval-provider",
            ProfileId = "approval.default",
            ModelId = "approval-model",
            AgentInstanceId = "audit-agent-1",
            AgentTemplateId = "approval-auditor",
        };
        var client = new InvocationToolApprovalLlmClient(
            invocation,
            new StaticToolApprovalLlmProfileResolver(profile),
            NullLogger<InvocationToolApprovalLlmClient>.Instance);

        var prompt = ToolApprovalPromptBuilder.Build(
            ValidApprovalRequest("{}"),
            SampleApprovalIdentity(),
            new SampleHighTool().Descriptor);

        var raw = await client.ReviewAsync(
            ValidApprovalRequest("{}"),
            SampleApprovalIdentity(),
            new SampleHighTool().Descriptor,
            prompt);
        var result = ToolApprovalReviewParser.Parse(raw);

        Assert.AreEqual(ToolApprovalDecision.Approved, result.Decision);
        Assert.IsNotNull(invocation.LastRequest);
        Assert.AreEqual("approval", invocation.LastRequest!.Profile.Role);
        Assert.AreEqual("approval-provider", invocation.LastRequest.Profile.ProviderId);
        Assert.AreEqual("approval.default", invocation.LastRequest.Profile.ProfileId);
        Assert.AreEqual("approval-model", invocation.LastRequest.Profile.ModelId);
        Assert.AreEqual("audit-agent-1", invocation.LastRequest.AgentInstanceId);
        Assert.AreEqual("approval-auditor", invocation.LastRequest.AgentTemplateId);
        Assert.AreEqual(2, invocation.LastRequest.Messages.Count);
        StringAssert.Contains(invocation.LastRequest.Messages[0].Content, "single clean-room approval review");
        StringAssert.Contains(invocation.LastRequest.Messages[1].Content, "\"toolId\": \"sample_high\"");
    }

    [TestMethod]
    public async Task StrictConfiguredToolApprovalLlmProfileResolver_Returns_Null_When_Profile_Is_Incomplete()
    {
        var resolver = new StrictConfiguredToolApprovalLlmProfileResolver(Options.Create(new ToolApprovalLlmOptions
        {
            ProviderId = "approval-provider",
            ProfileId = "approval.default",
        }));

        var profile = await resolver.ResolveAsync(
            ValidApprovalRequest("{}"),
            SampleApprovalIdentity(),
            new SampleHighTool().Descriptor);

        Assert.IsNull(profile);
    }

    [TestMethod]
    public async Task StrictConfiguredToolApprovalLlmProfileResolver_Resolves_Only_Explicit_Profile()
    {
        var resolver = new StrictConfiguredToolApprovalLlmProfileResolver(Options.Create(new ToolApprovalLlmOptions
        {
            ProviderId = " approval-provider ",
            ProfileId = " approval.default ",
            ModelId = " approval-model ",
            AgentTemplateId = " approval-auditor ",
        }));

        var profile = await resolver.ResolveAsync(
            ValidApprovalRequest("{}"),
            SampleApprovalIdentity(),
            new SampleHighTool().Descriptor);

        Assert.IsNotNull(profile);
        Assert.AreEqual("approval-provider", profile!.ProviderId);
        Assert.AreEqual("approval.default", profile.ProfileId);
        Assert.AreEqual("approval-model", profile.ModelId);
        Assert.AreEqual("approval-auditor", profile.AgentTemplateId);
    }

    [TestMethod]
    public async Task StrictConfiguredToolApprovalLlmProfileResolver_Resolves_Profile_From_Llm_Config_Service()
    {
        var llmConfig = new PuddingFileLlmConfigService(CreateApprovalLlmProvidersConfig());
        var resolver = new StrictConfiguredToolApprovalLlmProfileResolver(
            Options.Create(new ToolApprovalLlmOptions
            {
                ProfileId = "approval.default",
                AgentTemplateId = "approval-auditor",
            }),
            llmConfig);

        var profile = await resolver.ResolveAsync(
            ValidApprovalRequest("{}"),
            SampleApprovalIdentity(),
            new SampleHighTool().Descriptor);

        Assert.IsNotNull(profile);
        Assert.AreEqual("approval-provider", profile!.ProviderId);
        Assert.AreEqual("approval.default", profile.ProfileId);
        Assert.AreEqual("approval-model", profile.ModelId);
        Assert.AreEqual("approval-auditor", profile.AgentTemplateId);
    }

    [TestMethod]
    public async Task StrictConfiguredToolApprovalLlmProfileResolver_Returns_Null_When_Configured_Profile_Is_Missing()
    {
        var llmConfig = new PuddingFileLlmConfigService(CreateApprovalLlmProvidersConfig());
        var resolver = new StrictConfiguredToolApprovalLlmProfileResolver(
            Options.Create(new ToolApprovalLlmOptions
            {
                ProviderId = "approval-provider",
                ProfileId = "missing.approval",
                ModelId = "approval-model",
            }),
            llmConfig);

        var profile = await resolver.ResolveAsync(
            ValidApprovalRequest("{}"),
            SampleApprovalIdentity(),
            new SampleHighTool().Descriptor);

        Assert.IsNull(profile);
    }

    [TestMethod]
    public async Task WorkspaceAuditAgentProfileResolver_Throws_When_Workspace_Has_No_Audit_Agent()
    {
        var resolver = new StrictConfiguredToolApprovalLlmProfileResolver(
            Options.Create(new ToolApprovalLlmOptions
            {
                ProviderId = "approval-provider",
                ProfileId = "approval.default",
                ModelId = "approval-model",
            }),
            workspaceAuditAgentProvider: new StaticWorkspaceAuditAgentProvider(null));

        try
        {
            await resolver.ResolveAsync(
                ValidApprovalRequest("{}"),
                SampleApprovalIdentity(),
                new SampleHighTool().Descriptor);
            Assert.Fail("Expected ToolApprovalLlmProfileResolutionException.");
        }
        catch (ToolApprovalLlmProfileResolutionException ex)
        {
            StringAssert.Contains(ex.Message, "当前工作空间不具有审计类型的agent");
        }
    }

    [TestMethod]
    public async Task WorkspaceAuditAgentProfileResolver_Uses_First_Workspace_Audit_Agent()
    {
        var resolver = new StrictConfiguredToolApprovalLlmProfileResolver(
            Options.Create(new ToolApprovalLlmOptions()),
            workspaceAuditAgentProvider: new StaticWorkspaceAuditAgentProvider(new WorkspaceAuditAgentProfile
            {
                WorkspaceId = "workspace-1",
                AgentInstanceId = "audit-agent-1",
                AgentTemplateId = "audit-template",
                ProviderId = "approval-provider",
                ProfileId = "approval.default",
                ModelId = "approval-model",
            }));

        var profile = await resolver.ResolveAsync(
            ValidApprovalRequest("{}"),
            SampleApprovalIdentity(),
            new SampleHighTool().Descriptor);

        Assert.IsNotNull(profile);
        Assert.AreEqual("approval-provider", profile!.ProviderId);
        Assert.AreEqual("approval.default", profile.ProfileId);
        Assert.AreEqual("approval-model", profile.ModelId);
        Assert.AreEqual("audit-agent-1", profile.AgentInstanceId);
        Assert.AreEqual("audit-template", profile.AgentTemplateId);
    }

    [TestMethod]
    public async Task RuntimeLlmProfileResolver_Uses_Llm_Config_Service_For_Profile_Config()
    {
        var resolver = new PuddingRuntime.Services.LlmProfileResolver(
            NullLogger<PuddingRuntime.Services.LlmProfileResolver>.Instance,
            new PuddingFileLlmConfigService(CreateApprovalLlmProvidersConfig()));

        var resolved = await resolver.ResolveAsync(
            workspaceId: "workspace-1",
            agentInstanceId: "agent-1",
            new LlmInvocationProfile
            {
                ProviderId = "approval-provider",
                ProfileId = "approval.default",
                ModelId = "approval-model",
                Role = "approval",
            });

        Assert.AreEqual("approval-provider", resolved.ProviderId);
        Assert.AreEqual("approval.default", resolved.ProfileId);
        Assert.AreEqual("approval-model", resolved.ModelId);
        Assert.AreEqual("approval", resolved.Role);
        Assert.AreEqual("https://approval.example/v1", resolved.Config.Endpoint);
        Assert.AreEqual("approval-key", resolved.Config.ApiKey);
        Assert.AreEqual("approval-model", resolved.Config.ModelId);
    }

    [TestMethod]
    public async Task ToolApprovalService_Saves_Denied_Ticket_To_Store()
    {
        var store = new InMemoryToolApprovalTicketStore();
        var approval = new InMemoryToolApprovalService(new DenyingToolApprovalReviewer(), store);

        var result = await approval.SubmitAsync(
            ValidApprovalRequest("{}"),
            SampleApprovalIdentity(),
            new SampleHighTool().Descriptor);

        var stored = await store.GetAsync(result.TicketId);

        Assert.IsNotNull(stored);
        Assert.AreEqual(ToolApprovalTicketStatus.Denied, stored!.Status);
        Assert.AreEqual("reviewer denied", stored.DecisionReason);
    }

    [TestMethod]
    public async Task ToolApprovalService_Checks_Ticket_From_Injected_Store()
    {
        var store = new InMemoryToolApprovalTicketStore();
        var submitter = new InMemoryToolApprovalService(new FakeToolApprovalReviewer(), store);
        var checker = new InMemoryToolApprovalService(new DenyingToolApprovalReviewer(), store);
        var descriptor = new SampleHighTool().Descriptor;
        var identity = SampleApprovalIdentity();
        var submit = await submitter.SubmitAsync(
            ValidApprovalRequest("""{"command":"pwd","shell":"auto","timeout_seconds":10}"""),
            identity,
            descriptor);

        var check = await checker.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = identity.SessionId,
                AgentInstanceId = identity.AgentInstanceId,
                UserId = identity.UserId,
                ToolId = descriptor.ToolId,
                ActualArgumentsJson = """{"command": "pwd", "shell": "auto", "timeout_seconds": 10}""",
            },
            descriptor);

        Assert.IsTrue(check.IsApproved, check.Message);
        Assert.AreEqual(submit.TicketId, check.TicketId);
    }

    [TestMethod]
    public async Task ToolApprovalService_Ignores_Unrelated_Approved_Ticket_And_Uses_Implicit_Audit()
    {
        var store = new InMemoryToolApprovalTicketStore();
        var allowlistStore = new InMemoryToolApprovalAllowlistStore();
        var auditStore = new InMemoryToolApprovalAuditStore();
        var submitter = new InMemoryToolApprovalService(
            new FakeToolApprovalReviewer(),
            store,
            allowlistStore,
            auditStore);
        var checker = new InMemoryToolApprovalService(
            new FakeToolApprovalReviewer(),
            store,
            allowlistStore,
            auditStore);
        var descriptor = new SampleHighTool().Descriptor;
        var identity = SampleApprovalIdentity();

        await submitter.SubmitAsync(
            ValidApprovalRequest("""{"command":"pwd","shell":"auto","timeout_seconds":10}"""),
            identity,
            descriptor);

        var check = await checker.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = identity.SessionId,
                AgentInstanceId = identity.AgentInstanceId,
                UserId = identity.UserId,
                ToolId = descriptor.ToolId,
                ActualArgumentsJson = """{"command":"whoami","shell":"auto","timeout_seconds":10}""",
            },
            descriptor);

        var auditEvents = await auditStore.ListAsync();

        Assert.IsTrue(check.IsApproved, check.Message);
        Assert.AreEqual("ImplicitAudit", check.ApprovalSource);
        Assert.IsNull(check.TicketId);
        Assert.IsFalse(auditEvents.Any(e => e.EventType == ToolApprovalAuditEventType.TicketMismatch));
    }

    [TestMethod]
    public async Task ToolApprovalService_Allows_Approved_Job_Step_Arguments()
    {
        var store = new InMemoryToolApprovalTicketStore();
        var approval = new InMemoryToolApprovalService(new FakeToolApprovalReviewer(), store);
        var descriptor = new SampleHighTool().Descriptor with { ToolId = "shell" };
        var identity = SampleApprovalIdentity();

        var submit = await approval.SubmitAsync(
            ValidApprovalRequest(null!) with
            {
                ToolId = "shell",
                CommandName = "workspace_report job",
                RequestedScope = ToolApprovalScope.Timed,
                RequestedDuration = TimeSpan.FromMinutes(10),
                OperationSteps =
                [
                    ValidOperationSteps()[0] with
                    {
                        StepNumber = 1,
                        ToolId = "shell",
                        Command = "echo one",
                        RequestedArgumentsJson = """{"command":"echo one","shell":"powershell","working_directory":"E:/workspace"}""",
                    },
                    ValidOperationSteps()[0] with
                    {
                        StepNumber = 2,
                        ToolId = "shell",
                        Command = "echo two",
                        RequestedArgumentsJson = """{"command":"echo two","shell":"powershell","working_directory":"E:/workspace"}""",
                    },
                ],
            },
            identity,
            descriptor);

        var check = await approval.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = identity.SessionId,
                AgentInstanceId = identity.AgentInstanceId,
                UserId = identity.UserId,
                ToolId = "shell",
                ActualArgumentsJson = """{"working_directory":"E:/workspace","shell":"powershell","command":"echo two"}""",
            },
            descriptor);

        Assert.AreEqual(ToolApprovalDecision.Approved, submit.Decision, submit.DecisionReason);
        Assert.IsTrue(check.IsApproved, check.Message);
        Assert.AreEqual(submit.TicketId, check.TicketId);
    }

    [TestMethod]
    public async Task ToolApprovalService_Does_Not_Approve_Command_Outside_Job_Steps()
    {
        var store = new InMemoryToolApprovalTicketStore();
        var approval = new InMemoryToolApprovalService(new FakeToolApprovalReviewer(), store);
        var descriptor = new SampleHighTool().Descriptor with { ToolId = "shell" };
        var identity = SampleApprovalIdentity();

        await approval.SubmitAsync(
            ValidApprovalRequest(null!) with
            {
                ToolId = "shell",
                CommandName = "workspace_report job",
                RequestedScope = ToolApprovalScope.Timed,
                RequestedDuration = TimeSpan.FromMinutes(10),
                OperationSteps =
                [
                    ValidOperationSteps()[0] with
                    {
                        StepNumber = 1,
                        ToolId = "shell",
                        Command = "echo one",
                        RequestedArgumentsJson = """{"command":"echo one","shell":"powershell"}""",
                    },
                ],
            },
            identity,
            descriptor);

        var check = await approval.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = identity.SessionId,
                AgentInstanceId = identity.AgentInstanceId,
                UserId = identity.UserId,
                ToolId = "shell",
                ActualArgumentsJson = """{"command":"Remove-Item important.txt","shell":"powershell"}""",
            },
            descriptor);

        Assert.IsFalse(check.IsApproved);
        StringAssert.Contains(check.Message, "Implicit audit denied");
        StringAssert.Contains(check.Message, "request_tool_approval");
    }

    [TestMethod]
    public async Task ToolApprovalService_Explains_Consumed_Once_Ticket()
    {
        var store = new InMemoryToolApprovalTicketStore();
        var approval = new InMemoryToolApprovalService(new FakeToolApprovalReviewer(), store);
        var descriptor = new SampleHighTool().Descriptor;
        var identity = SampleApprovalIdentity();

        await approval.SubmitAsync(
            ValidApprovalRequest("""{"command":"custom-write","shell":"auto","timeout_seconds":10}"""),
            identity,
            descriptor);

        var executionRequest = new ToolApprovalExecutionRequest
        {
            WorkspaceId = identity.WorkspaceId,
            SessionId = identity.SessionId,
            AgentInstanceId = identity.AgentInstanceId,
            UserId = identity.UserId,
            ToolId = descriptor.ToolId,
            ActualArgumentsJson = """{"command":"custom-write","shell":"auto","timeout_seconds":10}""",
        };

        var first = await approval.CheckAsync(executionRequest, descriptor);
        var second = await approval.CheckAsync(executionRequest, descriptor);

        Assert.IsTrue(first.IsApproved, first.Message);
        Assert.IsTrue(second.IsApproved, second.Message);
        Assert.AreEqual(first.TicketId, second.TicketId);
        StringAssert.Contains(second.Message, "verification replay");
    }

    [TestMethod]
    public async Task ToolApprovalService_Allows_BuiltIn_ReadOnly_Shell_Command()
    {
        var ticketStore = new InMemoryToolApprovalTicketStore();
        var allowlistStore = new InMemoryToolApprovalAllowlistStore();
        var auditStore = new InMemoryToolApprovalAuditStore();
        var approval = new InMemoryToolApprovalService(
            new DenyingToolApprovalReviewer(),
            ticketStore,
            allowlistStore,
            auditStore);
        var identity = SampleApprovalIdentity();

        var check = await approval.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = identity.SessionId,
                AgentInstanceId = identity.AgentInstanceId,
                UserId = identity.UserId,
                ToolId = "shell",
                ActualArgumentsJson = """{"command":"pwd","shell":"auto","timeout_seconds":10}""",
            },
            new SampleHighTool().Descriptor with { ToolId = "shell" });

        var auditEvents = await auditStore.ListAsync();
        var builtInRule = await allowlistStore.GetAsync(check.AllowlistRuleId!);

        Assert.IsTrue(check.IsApproved, check.Message);
        Assert.AreEqual("BuiltIn", check.ApprovalSource);
        Assert.IsNotNull(check.AllowlistRuleId);
        Assert.AreEqual(1L, builtInRule?.HitCount);
        Assert.IsNotNull(builtInRule?.LastHitAtUtc);
        Assert.IsTrue(auditEvents.Any(e =>
            e.EventType == ToolApprovalAuditEventType.AllowlistHit
            && e.OriginalCommand == "pwd"
            && e.OriginalArgumentsJson == """{"command":"pwd","shell":"auto","timeout_seconds":10}"""
            && e.AllowlistRuleCommand == "pwd"
            && e.AllowlistRuleHitCount == 1));
    }

    [TestMethod]
    public async Task ToolApprovalService_Allows_ReadOnly_Tool_By_BuiltIn_Policy()
    {
        var auditStore = new InMemoryToolApprovalAuditStore();
        var allowlistStore = new InMemoryToolApprovalAllowlistStore();
        var approval = new InMemoryToolApprovalService(
            new DenyingToolApprovalReviewer(),
            new InMemoryToolApprovalTicketStore(),
            allowlistStore,
            auditStore);
        var identity = SampleApprovalIdentity();
        var descriptor = new SampleSearchTool().Descriptor;

        var check = await approval.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = identity.SessionId,
                AgentInstanceId = identity.AgentInstanceId,
                UserId = identity.UserId,
                ToolId = descriptor.ToolId,
                ActualArgumentsJson = """{"query":"approval","maxResults":3}""",
            },
            descriptor);

        var auditEvents = await auditStore.ListAsync();
        var policyRule = await allowlistStore.GetAsync("builtin_read_only_tool");

        Assert.IsTrue(check.IsApproved, check.Message);
        Assert.AreEqual("BuiltInPolicy", check.ApprovalSource);
        Assert.AreEqual("builtin_read_only_tool", check.AllowlistRuleId);
        Assert.AreEqual(1L, policyRule?.HitCount);
        Assert.IsNotNull(policyRule?.LastHitAtUtc);
        Assert.IsTrue(auditEvents.Any(e =>
            e.EventType == ToolApprovalAuditEventType.AllowlistHit
            && e.AllowlistRuleId == "builtin_read_only_tool"
            && e.OriginalArgumentsJson == """{"query":"approval","maxResults":3}"""
            && e.AllowlistRuleHitCount == 1));
    }

    [TestMethod]
    public async Task ToolApprovalService_Allows_Workspace_FileWrite_By_BuiltIn_Policy()
    {
        var auditStore = new InMemoryToolApprovalAuditStore();
        var allowlistStore = new InMemoryToolApprovalAllowlistStore();
        var approval = new InMemoryToolApprovalService(
            new DenyingToolApprovalReviewer(),
            new InMemoryToolApprovalTicketStore(),
            allowlistStore,
            auditStore);
        var identity = SampleApprovalIdentity();
        var descriptor = new FileWriteTool(NullLogger<FileWriteTool>.Instance).Descriptor;

        var check = await approval.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = identity.SessionId,
                AgentInstanceId = identity.AgentInstanceId,
                UserId = identity.UserId,
                ToolId = descriptor.ToolId,
                ActualArgumentsJson = """{"path":"temp/implicit-approval.txt","content":"ok"}""",
            },
            descriptor);

        var auditEvents = await auditStore.ListAsync();
        var policyRule = await allowlistStore.GetAsync("builtin_workspace_file_write");

        Assert.IsTrue(check.IsApproved, check.Message);
        Assert.AreEqual("BuiltInPolicy", check.ApprovalSource);
        Assert.AreEqual("builtin_workspace_file_write", check.AllowlistRuleId);
        Assert.AreEqual(1L, policyRule?.HitCount);
        Assert.IsNotNull(policyRule?.LastHitAtUtc);
        Assert.IsTrue(auditEvents.Any(e =>
            e.EventType == ToolApprovalAuditEventType.AllowlistHit
            && e.AllowlistRuleId == "builtin_workspace_file_write"
            && e.OriginalArgumentsJson == """{"path":"temp/implicit-approval.txt","content":"ok"}"""
            && e.AllowlistRuleHitCount == 1));
    }

    [TestMethod]
    public async Task ToolApprovalService_Does_Not_BuiltIn_Allow_FileWrite_Outside_Workspace()
    {
        var auditStore = new InMemoryToolApprovalAuditStore();
        var approval = new InMemoryToolApprovalService(
            new DenyingToolApprovalReviewer(),
            new InMemoryToolApprovalTicketStore(),
            new InMemoryToolApprovalAllowlistStore(),
            auditStore);
        var identity = SampleApprovalIdentity();
        var descriptor = new FileWriteTool(NullLogger<FileWriteTool>.Instance).Descriptor;

        var check = await approval.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = identity.SessionId,
                AgentInstanceId = identity.AgentInstanceId,
                UserId = identity.UserId,
                ToolId = descriptor.ToolId,
                ActualArgumentsJson = """{"path":"../outside.txt","content":"no"}""",
            },
            descriptor);

        var auditEvents = await auditStore.ListAsync();

        Assert.IsFalse(check.IsApproved);
        StringAssert.Contains(check.Message, "request_tool_approval");
        Assert.IsFalse(auditEvents.Any(e => e.AllowlistRuleId == "builtin_workspace_file_write"));
    }

    [TestMethod]
    public async Task ToolApprovalService_Records_Telemetry_For_Submit_And_Check()
    {
        var telemetry = new RecordingTelemetrySink();
        var store = new InMemoryToolApprovalTicketStore();
        var approval = new InMemoryToolApprovalService(
            new FakeToolApprovalReviewer(),
            store,
            new InMemoryToolApprovalAllowlistStore(),
            new InMemoryToolApprovalAuditStore(),
            telemetryMetricSink: telemetry);
        var descriptor = new SampleHighTool().Descriptor;
        var identity = SampleApprovalIdentity();
        var argumentsJson = """{"command":"custom-write","shell":"auto","timeout_seconds":10}""";

        var submit = await approval.SubmitAsync(
            ValidApprovalRequest(argumentsJson),
            identity,
            descriptor);
        var check = await approval.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = identity.SessionId,
                AgentInstanceId = identity.AgentInstanceId,
                UserId = identity.UserId,
                ToolId = descriptor.ToolId,
                ActualArgumentsJson = argumentsJson,
            },
            descriptor);

        Assert.AreEqual(ToolApprovalDecision.Approved, submit.Decision);
        Assert.IsTrue(check.IsApproved, check.Message);
        Assert.IsTrue(telemetry.Metrics.Any(metric =>
            metric.Name == "tool_approval.submit"
            && metric.Status == TelemetryMetricStatuses.Succeeded
            && metric.Dimensions is not null
            && metric.Dimensions.TryGetValue("decision", out var decision)
            && decision == nameof(ToolApprovalDecision.Approved)));
        Assert.IsTrue(telemetry.Metrics.Any(metric =>
            metric.Name == "tool_approval.check"
            && metric.Status == TelemetryMetricStatuses.Succeeded
            && metric.Dimensions is not null
            && metric.Dimensions.TryGetValue("approval_source", out var source)
            && source == "ticket"
            && metric.Dimensions.TryGetValue("tool_stage", out var toolStage)
            && toolStage == "check"
            && !metric.Dimensions.ContainsKey("stage")));
    }

    [TestMethod]
    public async Task ToolApprovalService_Preserves_Mismatch_Context_When_Implicit_Audit_Denies()
    {
        var telemetry = new RecordingTelemetrySink();
        var ticketStore = new InMemoryToolApprovalTicketStore();
        var allowlistStore = new InMemoryToolApprovalAllowlistStore();
        var auditStore = new InMemoryToolApprovalAuditStore();
        var submitter = new InMemoryToolApprovalService(
            new FakeToolApprovalReviewer(),
            ticketStore,
            allowlistStore,
            auditStore,
            telemetryMetricSink: telemetry);
        var checker = new InMemoryToolApprovalService(
            new DenyingToolApprovalReviewer(),
            ticketStore,
            allowlistStore,
            auditStore,
            telemetryMetricSink: telemetry);
        var descriptor = new SampleHighTool().Descriptor with { ToolId = "shell" };
        var identity = SampleApprovalIdentity();

        await submitter.SubmitAsync(
            ValidApprovalRequest("""{"command":"python png2jpg.py --help","shell":"auto","timeout_seconds":10}""") with
            {
                ToolId = "shell",
            },
            identity,
            descriptor);
        var check = await checker.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = identity.SessionId,
                AgentInstanceId = identity.AgentInstanceId,
                UserId = identity.UserId,
                ToolId = "shell",
                ActualArgumentsJson = """{"command":"python png2jpg.py","shell":"auto","timeout_seconds":10}""",
            },
            descriptor);

        Assert.IsFalse(check.IsApproved);
        StringAssert.Contains(check.Message, "Implicit audit denied");
        StringAssert.Contains(check.Message, "Previous approval check failed with approval_mismatch");
        StringAssert.Contains(check.Message, "approved_command='python png2jpg.py --help'");
        StringAssert.Contains(check.Message, "actual_command='python png2jpg.py'");
        Assert.IsTrue(telemetry.Metrics.Any(metric =>
            metric.Name == "tool_approval.check"
            && metric.Status == TelemetryMetricStatuses.Failed
            && metric.Dimensions is not null
            && metric.Dimensions.TryGetValue("failure_type", out var failureType)
            && failureType == "approval_implicit_denied"
            && metric.Dimensions.TryGetValue("previous_failure_type", out var previousFailureType)
            && previousFailureType == "approval_mismatch"
            && metric.Dimensions.TryGetValue("approved_command", out var approvedCommand)
            && approvedCommand.Contains("--help")
            && metric.Dimensions.TryGetValue("actual_command", out var actualCommand)
            && actualCommand == "python png2jpg.py"));
    }

    [TestMethod]
    public async Task ToolApprovalService_Falls_Back_To_Implicit_Audit_When_Ticket_Does_Not_Match()
    {
        var telemetry = new RecordingTelemetrySink();
        var auditStore = new InMemoryToolApprovalAuditStore();
        var approval = new InMemoryToolApprovalService(
            new FakeToolApprovalReviewer(),
            new InMemoryToolApprovalTicketStore(),
            new InMemoryToolApprovalAllowlistStore(),
            auditStore,
            telemetryMetricSink: telemetry);
        var descriptor = new SampleHighTool().Descriptor with { ToolId = "shell" };
        var identity = SampleApprovalIdentity();

        await approval.SubmitAsync(
            ValidApprovalRequest("""{"command":"python png2jpg.py --help","shell":"powershell","timeout_seconds":10}""") with
            {
                ToolId = "shell",
            },
            identity,
            descriptor);

        var check = await approval.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = identity.SessionId,
                AgentInstanceId = identity.AgentInstanceId,
                UserId = identity.UserId,
                ToolId = "shell",
                ActualArgumentsJson = """{"command":"python png2jpg.py","shell":"powershell","timeout_seconds":10}""",
            },
            descriptor);

        var auditEvents = await auditStore.ListAsync();

        Assert.IsTrue(check.IsApproved, check.Message);
        Assert.AreEqual("ImplicitAudit", check.ApprovalSource);
        Assert.IsNull(check.TicketId);
        Assert.IsFalse(auditEvents.Any(e => e.EventType == ToolApprovalAuditEventType.TicketMismatch));
        Assert.IsTrue(auditEvents.Any(e =>
            e.EventType == ToolApprovalAuditEventType.ImplicitApproved
            && e.Command == "python png2jpg.py"));
        Assert.IsTrue(telemetry.Metrics.Any(metric =>
            metric.Name == "tool_approval.check"
            && metric.Status == TelemetryMetricStatuses.Succeeded
            && metric.Dimensions is not null
            && metric.Dimensions.TryGetValue("approval_source", out var source)
            && source == "implicit_audit"));
    }

    [TestMethod]
    public async Task ToolApprovalService_Allows_Implicit_Audit_When_No_Ticket_And_Reviewer_Approves()
    {
        var telemetry = new RecordingTelemetrySink();
        var auditStore = new InMemoryToolApprovalAuditStore();
        var approval = new InMemoryToolApprovalService(
            new FakeToolApprovalReviewer(),
            new InMemoryToolApprovalTicketStore(),
            new InMemoryToolApprovalAllowlistStore(),
            auditStore,
            telemetryMetricSink: telemetry);
        var descriptor = new SampleHighTool().Descriptor with { ToolId = "shell" };
        var identity = SampleApprovalIdentity();

        var check = await approval.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = identity.SessionId,
                AgentInstanceId = identity.AgentInstanceId,
                UserId = identity.UserId,
                ToolId = "shell",
                ActualArgumentsJson = """{"command":"python png2jpg.py --help","shell":"powershell","timeout_seconds":10}""",
            },
            descriptor);

        var auditEvents = await auditStore.ListAsync();

        Assert.IsTrue(check.IsApproved, check.Message);
        Assert.AreEqual("ImplicitAudit", check.ApprovalSource);
        Assert.IsNull(check.TicketId);
        Assert.IsTrue(auditEvents.Any(e =>
            e.EventType == ToolApprovalAuditEventType.ImplicitApproved
            && e.Decision == ToolApprovalDecision.Approved
            && e.Command == "python png2jpg.py --help"));
        Assert.IsTrue(telemetry.Metrics.Any(metric =>
            metric.Name == "tool_approval.check"
            && metric.Status == TelemetryMetricStatuses.Succeeded
            && metric.Dimensions is not null
            && metric.Dimensions.TryGetValue("approval_source", out var source)
            && source == "implicit_audit"
            && metric.Dimensions.TryGetValue("decision", out var decision)
            && decision == "allowed"));
    }

    [TestMethod]
    public async Task ToolApprovalService_Uses_Implicit_Audit_When_Dynamic_Allowlist_Is_Unrelated()
    {
        var telemetry = new RecordingTelemetrySink();
        var allowlistStore = new InMemoryToolApprovalAllowlistStore();
        var auditStore = new InMemoryToolApprovalAuditStore();
        var approval = new InMemoryToolApprovalService(
            new FakeToolApprovalReviewer(),
            new InMemoryToolApprovalTicketStore(),
            allowlistStore,
            auditStore,
            telemetryMetricSink: telemetry);
        var descriptor = new SampleHighTool().Descriptor with { ToolId = "shell" };
        var identity = SampleApprovalIdentity();
        await allowlistStore.SaveAsync(new ToolApprovalAllowlistRule
        {
            RuleId = "tap_allow_txt_listing",
            WorkspaceId = identity.WorkspaceId,
            ToolId = "shell",
            Source = ToolApprovalAllowlistRuleSource.AuditAgent,
            Status = ToolApprovalAllowlistRuleStatus.Enabled,
            Command = """Get-ChildItem -Filter "*.txt" | Select-Object Name""",
            ArgumentsJson = """{"command":"Get-ChildItem -Filter \"*.txt\" | Select-Object Name","shell":"powershell"}""",
            Reason = "Reusable txt listing command.",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        var check = await approval.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = identity.SessionId,
                AgentInstanceId = identity.AgentInstanceId,
                UserId = identity.UserId,
                ToolId = "shell",
                ActualArgumentsJson = """{"command":"powershell -NoProfile -ExecutionPolicy Bypass -File \"E:\\workspace\\generate-md-summary.ps1\"","shell":"powershell","timeout_seconds":30}""",
            },
            descriptor);

        var auditEvents = await auditStore.ListAsync();

        Assert.IsTrue(check.IsApproved, check.Message);
        Assert.AreEqual("ImplicitAudit", check.ApprovalSource);
        Assert.IsFalse(auditEvents.Any(e => e.EventType == ToolApprovalAuditEventType.TicketMismatch));
        Assert.IsTrue(auditEvents.Any(e =>
            e.EventType == ToolApprovalAuditEventType.ImplicitApproved
            && e.Command == "powershell -NoProfile -ExecutionPolicy Bypass -File \"E:\\workspace\\generate-md-summary.ps1\""));
        Assert.IsTrue(telemetry.Metrics.Any(metric =>
            metric.Name == "tool_approval.check"
            && metric.Status == TelemetryMetricStatuses.Succeeded
            && metric.Dimensions is not null
            && metric.Dimensions.TryGetValue("approval_source", out var source)
            && source == "implicit_audit"));
    }

    [TestMethod]
    public async Task ToolApprovalService_Requires_Explicit_Ticket_When_Implicit_Audit_Denies()
    {
        var telemetry = new RecordingTelemetrySink();
        var auditStore = new InMemoryToolApprovalAuditStore();
        var approval = new InMemoryToolApprovalService(
            new DenyingToolApprovalReviewer(),
            new InMemoryToolApprovalTicketStore(),
            new InMemoryToolApprovalAllowlistStore(),
            auditStore,
            telemetryMetricSink: telemetry);
        var descriptor = new SampleHighTool().Descriptor with { ToolId = "shell" };
        var identity = SampleApprovalIdentity();

        var check = await approval.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = identity.SessionId,
                AgentInstanceId = identity.AgentInstanceId,
                UserId = identity.UserId,
                ToolId = "shell",
                ActualArgumentsJson = """{"command":"custom-risky-command","shell":"powershell","timeout_seconds":10}""",
            },
            descriptor);

        var auditEvents = await auditStore.ListAsync();

        Assert.IsFalse(check.IsApproved);
        StringAssert.Contains(check.Message, "Implicit audit denied");
        StringAssert.Contains(check.Message, "request_tool_approval");
        Assert.IsTrue(auditEvents.Any(e =>
            e.EventType == ToolApprovalAuditEventType.ImplicitDenied
            && e.Decision == ToolApprovalDecision.Denied
            && e.Command == "custom-risky-command"));
        Assert.IsTrue(telemetry.Metrics.Any(metric =>
            metric.Name == "tool_approval.check"
            && metric.Status == TelemetryMetricStatuses.Failed
            && metric.Dimensions is not null
            && metric.Dimensions.TryGetValue("failure_type", out var failureType)
            && failureType == "approval_implicit_denied"
            && metric.Dimensions.TryGetValue("approval_source", out var source)
            && source == "implicit_audit"));
    }

    [TestMethod]
    public async Task ToolApprovalService_Does_Not_Allow_BuiltIn_Command_With_Control_Operator()
    {
        var approval = new InMemoryToolApprovalService(
            new DenyingToolApprovalReviewer(),
            new InMemoryToolApprovalTicketStore(),
            new InMemoryToolApprovalAllowlistStore(),
            new InMemoryToolApprovalAuditStore());
        var identity = SampleApprovalIdentity();

        var check = await approval.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = identity.SessionId,
                AgentInstanceId = identity.AgentInstanceId,
                UserId = identity.UserId,
                ToolId = "shell",
                ActualArgumentsJson = """{"command":"ls; del important.txt","shell":"auto","timeout_seconds":10}""",
            },
            new SampleHighTool().Descriptor with { ToolId = "shell" });

        Assert.IsFalse(check.IsApproved);
    }

    [TestMethod]
    public async Task FileToolApprovalAllowlistStore_Persists_Custom_Rules_And_Seeds_BuiltIns()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "pudding-tool-approval-allowlist-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PuddingDataPaths.FromRoot(dataRoot);
            var store = new FileToolApprovalAllowlistStore(
                paths,
                NullLogger<FileToolApprovalAllowlistStore>.Instance);

            await store.SaveAsync(new ToolApprovalAllowlistRule
            {
                RuleId = "custom-shell-pwd",
                WorkspaceId = "workspace-1",
                ToolId = "shell",
                Command = "pwd",
                Source = ToolApprovalAllowlistRuleSource.Human,
                Status = ToolApprovalAllowlistRuleStatus.Enabled,
                Reason = "Test custom rule.",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });

            var reloaded = new FileToolApprovalAllowlistStore(
                paths,
                NullLogger<FileToolApprovalAllowlistStore>.Instance);
            var rules = await reloaded.ListAsync();

            Assert.IsTrue(rules.Any(r => r.RuleId == "custom-shell-pwd"));
            Assert.IsTrue(rules.Any(r => r.RuleId == "builtin_shell_pwd"));
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task FileToolApprovalAuditStore_Persists_Audit_Events()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "pudding-tool-approval-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PuddingDataPaths.FromRoot(dataRoot);
            var store = new FileToolApprovalAuditStore(
                paths,
                NullLogger<FileToolApprovalAuditStore>.Instance);

            await store.SaveAsync(new ToolApprovalAuditEvent
            {
                EventId = "audit-1",
                EventType = ToolApprovalAuditEventType.AllowlistRuleCreated,
                WorkspaceId = "workspace-1",
                ToolId = "shell",
                Reason = "Created by test.",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });

            var reloaded = new FileToolApprovalAuditStore(
                paths,
                NullLogger<FileToolApprovalAuditStore>.Instance);
            var events = await reloaded.ListAsync();

            Assert.IsTrue(events.Any(e => e.EventId == "audit-1"));
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ToolApprovalService_Creates_Workspace_Allowlist_Rule_When_Approved_And_Requested()
    {
        var ticketStore = new InMemoryToolApprovalTicketStore();
        var allowlistStore = new InMemoryToolApprovalAllowlistStore();
        var auditStore = new InMemoryToolApprovalAuditStore();
        var approval = new InMemoryToolApprovalService(
            new FakeToolApprovalReviewer(),
            ticketStore,
            allowlistStore,
            auditStore);
        var identity = SampleApprovalIdentity();
        var descriptor = new SampleHighTool().Descriptor with { ToolId = "shell" };

        var submit = await approval.SubmitAsync(
            ValidApprovalRequest("""{"command":"pwd","shell":"auto","timeout_seconds":10}""") with
            {
                ToolId = "shell",
                CommandName = "pwd",
                RequestAllowlistRule = true,
                AllowlistReason = "Repeated read-only workspace inspection.",
            },
            identity,
            descriptor);

        var rules = await allowlistStore.ListAsync();
        var events = await auditStore.ListAsync();
        var rule = rules.Single(r => r.RuleId == submit.AllowlistRuleId);

        Assert.AreEqual(ToolApprovalDecision.Approved, submit.Decision);
        Assert.IsNotNull(submit.AllowlistRuleId);
        Assert.AreEqual(identity.WorkspaceId, rule.WorkspaceId);
        Assert.AreEqual(ToolApprovalAllowlistRuleSource.AuditAgent, rule.Source);
        Assert.AreEqual(submit.TicketId, rule.ApprovalTicketId);
        Assert.IsTrue(events.Any(e =>
            e.EventType == ToolApprovalAuditEventType.AllowlistRuleCreated
            && e.AllowlistRuleId == submit.AllowlistRuleId));
    }

    [TestMethod]
    public async Task ToolApprovalService_Creates_Allowlist_Rule_From_Reviewer_Proposal_When_Approved()
    {
        var ticketStore = new InMemoryToolApprovalTicketStore();
        var allowlistStore = new InMemoryToolApprovalAllowlistStore();
        var auditStore = new InMemoryToolApprovalAuditStore();
        var approval = new InMemoryToolApprovalService(
            new StaticToolApprovalReviewer(new ToolApprovalReviewResult
            {
                Decision = ToolApprovalDecision.Approved,
                DecisionReason = "Approved exact read-only command.",
                AllowedScope = ToolApprovalScope.Once,
                AllowlistProposals =
                [
                    new ToolApprovalAllowlistProposal
                    {
                        ToolId = "shell",
                        Command = "custom-status --json",
                        ArgumentsJson = """{"command":"custom-status --json","shell":"auto","timeout_seconds":10}""",
                        Reason = "Exact read-only status command is safe to reuse.",
                    },
                ],
                ReviewerModel = "approval-test",
            }),
            ticketStore,
            allowlistStore,
            auditStore);
        var identity = SampleApprovalIdentity();
        var descriptor = new SampleHighTool().Descriptor with { ToolId = "shell" };

        var submit = await approval.SubmitAsync(
            ValidApprovalRequest("""{"command":"custom-status --json","shell":"auto","timeout_seconds":10}""") with
            {
                ToolId = "shell",
                CommandName = "custom-status --json",
                RequestAllowlistRule = false,
            },
            identity,
            descriptor);

        var rules = await allowlistStore.ListAsync();
        var rule = rules.Single(r => r.RuleId == submit.AllowlistRuleId);
        var check = await approval.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = "different-session",
                AgentInstanceId = "other-agent",
                UserId = identity.UserId,
                ToolId = "shell",
                ActualArgumentsJson = """{"command":"custom-status --json","shell":"auto","timeout_seconds":10}""",
            },
            descriptor);
        var events = await auditStore.ListAsync();
        var updatedRule = await allowlistStore.GetAsync(submit.AllowlistRuleId!);

        Assert.AreEqual(ToolApprovalDecision.Approved, submit.Decision);
        Assert.IsNotNull(submit.AllowlistRuleId);
        Assert.AreEqual(ToolApprovalAllowlistRuleSource.AuditAgent, rule.Source);
        Assert.AreEqual("Exact read-only status command is safe to reuse.", rule.Reason);
        Assert.AreEqual(submit.TicketId, rule.ApprovalTicketId);
        Assert.AreEqual(1L, updatedRule?.HitCount);
        Assert.IsNotNull(updatedRule?.LastHitAtUtc);
        Assert.IsTrue(check.IsApproved);
        Assert.AreEqual(submit.AllowlistRuleId, check.AllowlistRuleId);
        Assert.AreEqual("AuditAgent", check.ApprovalSource);
        Assert.IsTrue(events.Any(e =>
            e.EventType == ToolApprovalAuditEventType.AllowlistRuleCreated
            && e.AllowlistRuleId == submit.AllowlistRuleId
            && e.ReviewerModel == "approval-test"));
        Assert.IsTrue(events.Any(e =>
            e.EventType == ToolApprovalAuditEventType.AllowlistHit
            && e.AllowlistRuleId == submit.AllowlistRuleId
            && e.OriginalCommand == "custom-status --json"
            && e.OriginalArgumentsJson == """{"command":"custom-status --json","shell":"auto","timeout_seconds":10}"""
            && e.AllowlistRuleCommand == "custom-status --json"
            && e.AllowlistRuleArgumentsJson == """{"command":"custom-status --json","shell":"auto","timeout_seconds":10}"""
            && e.AllowlistRuleHitCount == 1));
    }

    [TestMethod]
    public async Task ToolApprovalService_Does_Not_Create_Allowlist_When_Reviewer_Returns_No_Proposal()
    {
        var ticketStore = new InMemoryToolApprovalTicketStore();
        var allowlistStore = new InMemoryToolApprovalAllowlistStore();
        var auditStore = new InMemoryToolApprovalAuditStore();
        var approval = new InMemoryToolApprovalService(
            new StaticToolApprovalReviewer(new ToolApprovalReviewResult
            {
                Decision = ToolApprovalDecision.Approved,
                DecisionReason = "Approved once, no reusable shape.",
                AllowedScope = ToolApprovalScope.Once,
            }),
            ticketStore,
            allowlistStore,
            auditStore);
        var identity = SampleApprovalIdentity();
        var descriptor = new SampleHighTool().Descriptor with { ToolId = "shell" };

        var submit = await approval.SubmitAsync(
            ValidApprovalRequest("""{"command":"one-off-deploy","shell":"auto","timeout_seconds":10}""") with
            {
                ToolId = "shell",
                CommandName = "one-off-deploy",
                RequestAllowlistRule = false,
            },
            identity,
            descriptor);

        var events = await auditStore.ListAsync();

        Assert.AreEqual(ToolApprovalDecision.Approved, submit.Decision);
        Assert.IsNull(submit.AllowlistRuleId);
        Assert.IsFalse(events.Any(e =>
            e.TicketId == submit.TicketId
            && e.EventType == ToolApprovalAuditEventType.AllowlistRuleCreated));
    }

    [TestMethod]
    public async Task ToolApprovalService_Dynamic_Allowlist_With_Arguments_Falls_Back_To_Implicit_Audit_When_Arguments_Change()
    {
        var ticketStore = new InMemoryToolApprovalTicketStore();
        var allowlistStore = new InMemoryToolApprovalAllowlistStore();
        var auditStore = new InMemoryToolApprovalAuditStore();
        var approval = new InMemoryToolApprovalService(
            new FakeToolApprovalReviewer(),
            ticketStore,
            allowlistStore,
            auditStore);
        var identity = SampleApprovalIdentity();
        var descriptor = new SampleHighTool().Descriptor with { ToolId = "shell" };

        await approval.SubmitAsync(
            ValidApprovalRequest("""{"command":"custom-read --id 1","shell":"auto","timeout_seconds":10}""") with
            {
                ToolId = "shell",
                CommandName = "custom-read --id 1",
                RequestAllowlistRule = true,
                RequestedScope = ToolApprovalScope.Once,
            },
            identity,
            descriptor);

        var check = await approval.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = "different-session",
                AgentInstanceId = "other-agent",
                UserId = identity.UserId,
                ToolId = "shell",
                ActualArgumentsJson = """{"command":"custom-read --id 1","shell":"auto","timeout_seconds":20}""",
            },
            descriptor);

        var events = await auditStore.ListAsync();

        Assert.IsTrue(check.IsApproved, check.Message);
        Assert.AreEqual("ImplicitAudit", check.ApprovalSource);
        Assert.IsFalse(events.Any(e => e.EventType == ToolApprovalAuditEventType.AllowlistHit));
        Assert.IsTrue(events.Any(e =>
            e.EventType == ToolApprovalAuditEventType.ImplicitApproved
            && e.Command == "custom-read --id 1"));
    }

    [TestMethod]
    public async Task RequestToolApprovalTool_Accepts_Structured_Checklist_With_Snake_Case_Steps()
    {
        var services = new ServiceCollection();
        services.AddPuddingTool<SampleHighTool>();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var tool = provider.GetServices<IPuddingTool>()
            .Single(t => t.Descriptor.ToolId == "request_tool_approval");

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "approval-call-1",
            Context = SampleContext() with
            {
                Trace = RuntimeTraceContext.CreateNew(userId: "user-1"),
            },
            ArgumentsJson = """
            {
              "tool_id": "sample_high",
              "command_name": "sample high",
              "purpose": "Request approval for a focused sample high-risk tool call.",
              "necessity": "The runtime must verify the request tool path.",
              "fact_basis": ["The sample high tool descriptor exists in the test catalog."],
              "requested_arguments_json": "{}",
              "target_resources": ["sample_high"],
              "authorized_area": ["workspace-1"],
              "may_damage_or_delete_data": false,
              "is_irreversible_operation": false,
              "backup_taken": false,
              "rollback_plan": "No mutation is expected.",
              "operation_context": "MSTest local runtime test context.",
              "operation_plan": "Submit one approval request.",
              "operation_steps": [
                {
                  "step_number": 1,
                  "command": "sample_high {}",
                  "working_directory": "E:/github/AgentNetworkPlan/PuddingAgent",
                  "environment": "MSTest local runtime test",
                  "target_object": "sample_high",
                  "purpose": "Verify request_tool_approval can submit the ticket.",
                  "expected_effect": "Creates an in-memory approval ticket only.",
                  "reasonableness": "This exact request covers the approval tool behavior.",
                  "safety_check_before": "Confirm the target tool exists.",
                  "stop_condition": "Stop if the target tool cannot be found.",
                  "rollback_for_step": "No source or data mutation is expected."
                }
              ],
              "may_expose_secrets": false,
              "user_consent_status": "implied",
              "alternatives_considered": ["No lower-risk call exercises the request tool."],
              "requested_scope": "once",
              "risk_notes": "No destructive behavior is present."
            }
            """,
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "\"decision\": \"approved\"");
        StringAssert.Contains(result.Output, "\"status\": \"approved\"");
        StringAssert.Contains(result.Output, "\"argumentsHash\":");
    }

    [TestMethod]
    public async Task RequestToolApprovalTool_Accepts_String_Operation_Steps()
    {
        var services = new ServiceCollection();
        services.AddPuddingTool<SampleHighTool>();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var tool = provider.GetServices<IPuddingTool>()
            .Single(t => t.Descriptor.ToolId == "request_tool_approval");

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "approval-call-string-step",
            Context = SampleContext() with
            {
                Trace = RuntimeTraceContext.CreateNew(userId: "user-1"),
            },
            ArgumentsJson = """
            {
              "tool_id": "sample_high",
              "command_name": "sample high",
              "purpose": "Request approval with a shorthand string step.",
              "necessity": "The runtime should accept common agent shorthand for operation steps.",
              "fact_basis": ["The sample high tool descriptor exists in the test catalog."],
              "requested_arguments_json": "{}",
              "target_resources": ["sample_high"],
              "authorized_area": ["workspace-1"],
              "may_damage_or_delete_data": false,
              "is_irreversible_operation": false,
              "backup_taken": false,
              "rollback_plan": "No mutation is expected.",
              "operation_context": "MSTest local runtime test context.",
              "operation_plan": "Submit one approval request.",
              "operation_steps": ["Execute command: sample_high {}"],
              "may_expose_secrets": false,
              "user_consent_status": "implied",
              "alternatives_considered": ["No lower-risk call exercises the request tool."],
              "requested_scope": "once",
              "risk_notes": "No destructive behavior is present."
            }
            """,
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "\"decision\": \"approved\"");
        StringAssert.Contains(result.Output, "\"status\": \"approved\"");
    }

    [TestMethod]
    public async Task RequestToolApprovalTool_Returns_Actionable_Error_For_Invalid_Json_Arguments()
    {
        var services = new ServiceCollection();
        services.AddPuddingTool<SampleHighTool>();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var tool = provider.GetServices<IPuddingTool>()
            .Single(t => t.Descriptor.ToolId == "request_tool_approval");

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "approval-call-invalid-json",
            Context = SampleContext() with
            {
                Trace = RuntimeTraceContext.CreateNew(userId: "user-1"),
            },
            ArgumentsJson = """
            {
              "tool_id": "sample_high",
              "requested_arguments_json": "{}",
              "rollback_plan": 无需回滚
            }
            """,
        });

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Tool arguments must be valid JSON");
        StringAssert.Contains(result.Error, "rollback_plan");
        StringAssert.Contains(result.Error, "\"rollback_plan\": \"No rollback is required.\"");
    }

    [TestMethod]
    public async Task RequestToolApprovalTool_Rejects_Placeholder_Requested_Arguments()
    {
        var services = new ServiceCollection();
        services.AddPuddingTool<SampleHighTool>();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var tool = provider.GetServices<IPuddingTool>()
            .Single(t => t.Descriptor.ToolId == "request_tool_approval");

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "approval-call-placeholder",
            Context = SampleContext() with
            {
                Trace = RuntimeTraceContext.CreateNew(userId: "user-1"),
            },
            ArgumentsJson = """
            {
              "tool_id": "sample_high",
              "command_name": "sample high",
              "purpose": "Request approval for multiple future commands.",
              "necessity": "The agent wants one broad ticket.",
              "fact_basis": ["The task may require several shell commands."],
              "requested_arguments_json": "{\"command\":\"Multiple shell commands as detailed in operation steps\",\"shell\":\"powershell\"}",
              "target_resources": ["sample_high"],
              "authorized_area": ["workspace-1"],
              "may_damage_or_delete_data": false,
              "is_irreversible_operation": false,
              "backup_taken": false,
              "rollback_plan": "No mutation is expected.",
              "operation_context": "MSTest local runtime test context.",
              "operation_plan": "Run several commands later.",
              "operation_steps": [
                "Create script",
                "Run script"
              ],
              "may_expose_secrets": false,
              "user_consent_status": "implied",
              "alternatives_considered": ["Submit exact command tickets."],
              "requested_scope": "timed",
              "requested_duration_minutes": 10,
              "risk_notes": "No destructive behavior is present."
            }
            """,
        });

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "requested_arguments_json.command");
        StringAssert.Contains(result.Error, "concrete executable command");
        StringAssert.Contains(result.Error, "operation_steps");
    }

    [TestMethod]
    public async Task RequestToolApprovalTool_Returns_Clear_Error_For_Invalid_Operation_Step()
    {
        var services = new ServiceCollection();
        services.AddPuddingTool<SampleHighTool>();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var tool = provider.GetServices<IPuddingTool>()
            .Single(t => t.Descriptor.ToolId == "request_tool_approval");

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "approval-call-bad-step",
            Context = SampleContext() with
            {
                Trace = RuntimeTraceContext.CreateNew(userId: "user-1"),
            },
            ArgumentsJson = """
            {
              "tool_id": "sample_high",
              "command_name": "sample high",
              "purpose": "Request approval with an invalid step.",
              "necessity": "The runtime should return a clear repair hint.",
              "requested_arguments_json": "{}",
              "operation_context": "MSTest local runtime test context.",
              "operation_steps": [123],
              "user_consent_status": "implied",
              "requested_scope": "once"
            }
            """,
        });

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "operation_steps[0] must be either a string shorthand or an object");
        StringAssert.Contains(result.Error, "Example object");
    }

    [TestMethod]
    public async Task ListToolApprovalsTool_Filters_By_Status_And_Tool()
    {
        var store = new InMemoryToolApprovalTicketStore();
        var identity = SampleApprovalIdentity();
        await store.SaveAsync(new ToolApprovalTicketRecord
        {
            TicketId = "tap_approved",
            Identity = identity,
            ToolId = "sample_high",
            ArgumentsHash = "hash-approved",
            Scope = ToolApprovalScope.Once,
            Status = ToolApprovalTicketStatus.Approved,
            DecisionReason = "approved",
            CreatedAtUtc = DateTimeOffset.Parse("2026-06-03T00:00:00Z"),
            DecidedAtUtc = DateTimeOffset.Parse("2026-06-03T00:00:01Z"),
            RemainingUses = 1,
        });
        await store.SaveAsync(new ToolApprovalTicketRecord
        {
            TicketId = "tap_denied",
            Identity = identity,
            ToolId = "sample_high",
            ArgumentsHash = "hash-denied",
            Scope = ToolApprovalScope.Once,
            Status = ToolApprovalTicketStatus.Denied,
            DecisionReason = "denied",
            CreatedAtUtc = DateTimeOffset.Parse("2026-06-03T00:00:02Z"),
            DecidedAtUtc = DateTimeOffset.Parse("2026-06-03T00:00:03Z"),
        });

        var tool = new ListToolApprovalsTool(store);
        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "list-approval-1",
            Context = SampleContext() with
            {
                Trace = RuntimeTraceContext.CreateNew(userId: "user-1"),
            },
            ArgumentsJson = """
            {
              "tool_id": "sample_high",
              "status": "approved"
            }
            """,
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "\"count\": 1");
        StringAssert.Contains(result.Output, "\"ticketId\": \"tap_approved\"");
        Assert.IsFalse(result.Output.Contains("tap_denied", StringComparison.Ordinal));
        Assert.IsFalse(result.Output.Contains("requested_arguments_json", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ToolAuthorizationService_Persists_Permanent_Authorization()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "pudding-tool-auth-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PuddingDataPaths.FromRoot(dataRoot);
            var context = new ToolAuthorizationContext
            {
                WorkspaceId = "workspace-1",
                SessionId = "session-1",
                AgentInstanceId = "agent-1",
                UserId = "user-1",
                ToolId = "sample_high",
            };

            var first = new InMemoryToolAuthorizationService(dataPaths: paths);
            await first.ApplyCommandAsync(
                new ToolAuthorizationCommand
                {
                    RawText = "/authorize sample_high permanent",
                    Action = ToolAuthorizationAction.Authorize,
                    ToolId = "sample_high",
                    Scope = ToolAuthorizationScope.Permanent,
                },
                context);

            var second = new InMemoryToolAuthorizationService(dataPaths: paths);
            var check = await second.CheckAsync(context, new SampleHighTool().Descriptor);

            Assert.IsTrue(check.IsAuthorized);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ToolInvocationService_Delegates_To_Unified_Tool_Execution_Service()
    {
        var executor = new RecordingToolExecutionService();
        var service = new ToolInvocationService(
            executor,
            workspaceGuard: null,
            NullLogger<ToolInvocationService>.Instance);
        var policy = new CapabilityPolicy { DefaultToolNames = ["sample_search"] };

        var result = await service.InvokeAsync(new ToolInvocationRequest
        {
            WorkspaceId = "workspace-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-1",
            AgentTemplateId = "template-1",
            ToolCallId = "call-1",
            ToolName = "sample_search",
            ArgumentsJson = """{"query":"pudding"}""",
            CapabilityPolicy = policy,
        });

        Assert.IsTrue(result.Success);
        Assert.AreEqual("executed via unified tool service", result.Output);
        Assert.AreEqual("sample_search", executor.ToolId);
        Assert.AreEqual("""{"query":"pudding"}""", executor.ArgumentsJson);
        Assert.AreSame(policy, executor.Policy);
        Assert.IsNotNull(executor.Context);
        Assert.AreEqual("workspace-1", executor.Context!.WorkspaceId);
        Assert.AreEqual("session-1", executor.Context.SessionId);
        Assert.AreEqual("agent-1", executor.Context.AgentInstanceId);
        Assert.AreEqual("template-1", executor.Context.AgentTemplateId);
    }

    [TestMethod]
    public async Task ToolInvocationService_Records_Tool_Completion_As_Activity()
    {
        var executor = new RecordingToolExecutionService();
        var idleDetector = new RecordingIdleDetector();
        var service = new ToolInvocationService(
            executor,
            workspaceGuard: null,
            NullLogger<ToolInvocationService>.Instance,
            idleDetector: idleDetector);

        await service.InvokeAsync(new ToolInvocationRequest
        {
            WorkspaceId = "workspace-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-1",
            ToolCallId = "call-1",
            ToolName = "sample_search",
            ArgumentsJson = """{"query":"pudding"}""",
        });

        Assert.AreEqual(1, idleDetector.ToolCompletedCount);
    }

    private static ToolApprovalIdentity SampleApprovalIdentity() => new()
    {
        WorkspaceId = "workspace-1",
        SessionId = "session-1",
        AgentInstanceId = "agent-1",
        UserId = "user-1",
    };

    private static ToolApprovalTicketRequest ValidApprovalRequest(string argumentsJson) => new()
    {
        ToolId = "sample_high",
        CommandName = "sample high",
        Purpose = "Execute the focused high-risk sample tool in a test.",
        Necessity = "The execution service must verify auto approval behavior.",
        FactBasis = ["The test constructed this sample tool descriptor."],
        RequestedArgumentsJson = argumentsJson,
        TargetResources = ["sample_high"],
        AuthorizedArea = ["workspace-1"],
        OutsideAuthorizedAreaReason = null,
        MayDamageOrDeleteData = false,
        IsIrreversibleOperation = false,
        BackupTaken = false,
        RollbackPlan = "No mutation is expected from this sample tool.",
        OperationContext = "MSTest local runtime test context.",
        OperationPlan = "Call the sample tool once with exact planned arguments.",
        OperationSteps = ValidOperationSteps(),
        TemporaryFileEvidence = null,
        MayExposeSecrets = false,
        UserConsentStatus = ToolApprovalUserConsentStatus.Implied,
        AlternativesConsidered = ["No lower-risk call exercises runtime high-risk authorization."],
        RequestedScope = ToolApprovalScope.Once,
        RiskNotes = "No secret or destructive behavior is present in the sample tool.",
    };

    private static IReadOnlyList<ToolApprovalOperationStep> ValidOperationSteps() =>
    [
        new ToolApprovalOperationStep
        {
            StepNumber = 1,
            Command = "sample_high {}",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            Environment = "MSTest local runtime test",
            TargetObject = "sample_high",
            Purpose = "Verify the approved tool can execute once.",
            ExpectedEffect = "Returns the sample high-risk tool output without mutating data.",
            Reasonableness = "This exact call is the behavior under test.",
            SafetyCheckBefore = "Confirm the tool id and exact arguments match the ticket.",
            StopCondition = "Stop if the actual arguments differ from the approved arguments.",
            RollbackForStep = "No rollback is required because no mutation is expected.",
        },
    ];

    private static PuddingLlmProvidersConfig CreateApprovalLlmProvidersConfig() => new()
    {
        Providers =
        [
            new PuddingLlmProviderConfig
            {
                ProviderId = "approval-provider",
                Name = "Approval Provider",
                BaseUrl = "https://approval.example/v1",
                ApiKey = "approval-key",
                Models =
                [
                    new PuddingLlmModelConfig
                    {
                        ModelId = "approval-model",
                        Name = "Approval Model",
                        MaxContextTokens = 128000,
                        MaxOutputTokens = 4096,
                        IsDefault = true,
                    },
                ],
            },
        ],
        Profiles = new Dictionary<string, PuddingLlmProfileConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["approval.default"] = new()
            {
                ProviderId = "approval-provider",
                ModelId = "approval-model",
                ReasoningEffort = "low",
            },
        },
    };

    [Tool(
        id: "sample_search",
        name: "Sample search",
        description: "Searches sample data.",
        category: ToolCategory.Query,
        permission: ToolPermissionLevel.Low,
        safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe)]
    private sealed class SampleSearchTool : PuddingToolBase<SampleSearchArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            SampleSearchArgs args,
            ToolExecutionContext context,
            CancellationToken ct)
        {
            return Task.FromResult(ToolExecutionResult.Ok($"query={args.Query}"));
        }
    }

    private sealed record SampleSearchArgs
    {
        [ToolParam("Search query")]
        public required string Query { get; init; }

        [ToolParam("Maximum result count")]
        public int MaxResults { get; init; } = 10;
    }

    [Tool(
        id: "sample_medium",
        name: "Sample medium",
        description: "Requires template authorization.",
        permission: ToolPermissionLevel.Medium)]
    private sealed class SampleMediumTool : PuddingToolBase<object>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            object args,
            ToolExecutionContext context,
            CancellationToken ct)
        {
            return Task.FromResult(ToolExecutionResult.Ok("executed"));
        }
    }

    [Tool(
        id: "sample_large_output",
        name: "Sample large output",
        description: "Returns a large output payload for telemetry threshold tests.",
        permission: ToolPermissionLevel.Low)]
    private sealed class SampleLargeOutputTool : PuddingToolBase<object>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            object args,
            ToolExecutionContext context,
            CancellationToken ct)
        {
            return Task.FromResult(ToolExecutionResult.Ok(new string('x', 9000)));
        }
    }

    [Tool(
        id: "sample_high",
        name: "Sample high",
        description: "Requires explicit grant authorization.",
        permission: ToolPermissionLevel.High,
        safety: ToolSafetyFlags.RequiresShell)]
    private sealed class SampleHighTool : PuddingToolBase<object>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            object args,
            ToolExecutionContext context,
            CancellationToken ct)
        {
            return Task.FromResult(ToolExecutionResult.Ok("executed high"));
        }
    }

    [Tool(
        id: "assembly_discovered",
        name: "Assembly discovered",
        description: "Discovered by DI assembly scan.",
        permission: ToolPermissionLevel.Low)]
    public sealed class AssemblyDiscoveredTool : PuddingToolBase<object>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            object args,
            ToolExecutionContext context,
            CancellationToken ct)
        {
            return Task.FromResult(ToolExecutionResult.Ok("discovered"));
        }
    }

    private sealed class LegacySkillTool : IAgentSkill, PuddingCode.Abstractions.ITool
    {
        public string SkillId => "legacy_echo";
        public string Name => "Legacy Echo";
        public string Description => "Echoes legacy input.";
        public bool RequiresShellExecution => false;
        public ToolPermissionLevel PermissionLevel => ToolPermissionLevel.Medium;

        public ToolParameterSchema Parameters { get; } = new(
            [new ToolParameter("input", "string", "Echo input")],
            ["input"]);

        public Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new SkillResult
            {
                Success = true,
                Output = request.Input,
                ExitCode = 0,
            });
        }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            return Task.FromResult(argumentsJson);
        }
    }

    private sealed class CapturingLegacySkill : IAgentSkill
    {
        public CapturingLegacySkill(string skillId)
        {
            SkillId = skillId;
        }

        public SkillInvokeRequest? LastRequest { get; private set; }
        public string SkillId { get; }
        public string Name => SkillId;
        public string Description => "Captures legacy skill invocation.";
        public bool RequiresShellExecution => false;
        public ToolPermissionLevel PermissionLevel => ToolPermissionLevel.Low;

        public Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new SkillResult
            {
                Success = true,
                Output = request.Input,
                ExitCode = 0,
            });
        }
    }

    private static ToolExecutionContext TestToolContext() => new()
    {
        WorkspaceId = "workspace-test",
        SessionId = "session-test",
        AgentInstanceId = "agent-test",
    };

    private sealed class LegacyRuntimeSkill : IAgentSkill
    {
        public LegacyRuntimeSkill(
            string skillId,
            string name,
            bool requiresShellExecution,
            ToolPermissionLevel permissionLevel)
        {
            SkillId = skillId;
            Name = name;
            RequiresShellExecution = requiresShellExecution;
            PermissionLevel = permissionLevel;
        }

        public string SkillId { get; }
        public string Name { get; }
        public string Description => $"{Name} description";
        public bool RequiresShellExecution { get; }
        public ToolPermissionLevel PermissionLevel { get; }

        public Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new SkillResult
            {
                Success = true,
                Output = request.Input,
                ExitCode = 0,
            });
        }
    }

    private sealed class RecordingToolExecutionService : IPuddingToolExecutionService
    {
        public string? ToolId { get; private set; }
        public string? ArgumentsJson { get; private set; }
        public ToolExecutionContext? Context { get; private set; }
        public CapabilityPolicy? Policy { get; private set; }

        public Task<ToolExecutionResult> ExecuteAsync(
            string toolId,
            string argumentsJson,
            ToolExecutionContext context,
            CapabilityPolicy? policy,
            CancellationToken ct = default)
        {
            ToolId = toolId;
            ArgumentsJson = argumentsJson;
            Context = context;
            Policy = policy;

            return Task.FromResult(ToolExecutionResult.Ok("executed via unified tool service"));
        }
    }

    private sealed class RecordingTelemetrySink : ITelemetryMetricSink
    {
        public List<TelemetryMetric> Metrics { get; } = [];

        public Task RecordAsync(TelemetryMetric metric, CancellationToken ct = default)
        {
            Metrics.Add(metric);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingIdleDetector : IIdleDetector
    {
        public int ToolCompletedCount { get; private set; }
        public DateTimeOffset LastActiveAt { get; private set; } = DateTimeOffset.UtcNow;
        public TimeSpan IdleDuration => TimeSpan.Zero;

        public void RecordUserMessage()
        {
        }

        public void RecordToolCompleted()
        {
            ToolCompletedCount++;
        }

        public event Func<TimeSpan, CancellationToken, Task>? OnIdleThresholdReached;

        public void RecordActivity()
        {
        }

        public void ReArm()
        {
        }
    }

    private sealed class InvalidDottedTool : IPuddingTool
    {
        public ToolDescriptor Descriptor { get; } = new()
        {
            ToolId = "bad.tool",
            Name = "Bad tool",
            Description = "Invalid dotted tool id.",
        };

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken ct = default)
            => Task.FromResult(ToolExecutionResult.Ok("bad"));
    }

    private sealed class FakeFileSearchProvider(string providerId) : IFileSearchProvider
    {
        private readonly List<string> _items = [];
        private Exception? _searchException;

        public string ProviderId { get; } = providerId;
        public string DisplayName => providerId;
        public bool IsAvailable { get; set; } = true;
        public int SearchCallCount { get; private set; }
        public string? LastDirectory { get; private set; }
        public string? LastPattern { get; private set; }
        public bool? LastRecursive { get; private set; }
        public int? LastMaxResults { get; private set; }

        public FakeFileSearchProvider WithItem(string fullPath)
        {
            _items.Add(fullPath);
            return this;
        }

        public FakeFileSearchProvider ThrowOnSearch(Exception exception)
        {
            _searchException = exception;
            return this;
        }

        public Task<IReadOnlyList<string>> SearchAsync(
            string directory,
            string pattern,
            bool recursive,
            int maxResults,
            CancellationToken ct)
        {
            SearchCallCount++;
            LastDirectory = directory;
            LastPattern = pattern;
            LastRecursive = recursive;
            LastMaxResults = maxResults;
            if (_searchException is not null)
                throw _searchException;

            return Task.FromResult<IReadOnlyList<string>>(_items.Take(maxResults).ToArray());
        }
    }

    private sealed class FakeEverythingSdk : IEverythingSdk
    {
        private readonly IReadOnlyList<EverythingQueryItem> _items;
        private readonly bool _available;

        public FakeEverythingSdk(params EverythingQueryItem[] items)
            : this(items, available: true)
        {
        }

        public FakeEverythingSdk(IReadOnlyList<EverythingQueryItem> items, bool available)
        {
            _items = items;
            _available = available;
        }

        public int QueryCallCount { get; private set; }
        public EverythingQueryRequest? LastRequest { get; private set; }

        public bool IsAvailable(out string? error)
        {
            error = _available ? null : "Everything64.dll unavailable";
            return _available;
        }

        public Task<EverythingQueryResult> QueryAsync(EverythingQueryRequest request, CancellationToken ct)
        {
            QueryCallCount++;
            LastRequest = request;
            return Task.FromResult(new EverythingQueryResult(_items));
        }
    }

    private sealed class TestMemoryLibraryDbContextFactory(DbContextOptions<MemoryLibraryDbContext> options)
        : IDbContextFactory<MemoryLibraryDbContext>
    {
        public MemoryLibraryDbContext CreateDbContext() => new(options);

        public Task<MemoryLibraryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryLibraryDbContext(options));
    }

    private sealed class DenyingToolApprovalReviewer : IToolApprovalReviewer
    {
        public Task<ToolApprovalReviewResult> ReviewAsync(
            ToolApprovalTicketRequest request,
            ToolApprovalIdentity identity,
            ToolDescriptor descriptor,
            CancellationToken ct = default)
        {
            return Task.FromResult(new ToolApprovalReviewResult
            {
                Decision = ToolApprovalDecision.Denied,
                DecisionReason = "reviewer denied",
            });
        }
    }

    private sealed class StaticToolApprovalReviewer(ToolApprovalReviewResult result) : IToolApprovalReviewer
    {
        public Task<ToolApprovalReviewResult> ReviewAsync(
            ToolApprovalTicketRequest request,
            ToolApprovalIdentity identity,
            ToolDescriptor descriptor,
            CancellationToken ct = default)
            => Task.FromResult(result);
    }

    private sealed class ThrowingToolApprovalReviewer : IToolApprovalReviewer
    {
        public Task<ToolApprovalReviewResult> ReviewAsync(
            ToolApprovalTicketRequest request,
            ToolApprovalIdentity identity,
            ToolDescriptor descriptor,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Reviewer should not be called for software hard safety denial.");
    }

    private sealed class RecordingToolApprovalLlmClient(string response) : IToolApprovalLlmClient
    {
        public string? SystemPrompt { get; private set; }
        public string? UserPrompt { get; private set; }

        public Task<string> ReviewAsync(
            ToolApprovalTicketRequest request,
            ToolApprovalIdentity identity,
            ToolDescriptor descriptor,
            ToolApprovalPrompt prompt,
            CancellationToken ct = default)
        {
            SystemPrompt = prompt.SystemPrompt;
            UserPrompt = prompt.UserPrompt;
            return Task.FromResult(response);
        }
    }

    private sealed class StaticToolApprovalLlmProfileResolver(ToolApprovalLlmProfile? profile)
        : IToolApprovalLlmProfileResolver
    {
        public Task<ToolApprovalLlmProfile?> ResolveAsync(
            ToolApprovalTicketRequest request,
            ToolApprovalIdentity identity,
            ToolDescriptor descriptor,
            CancellationToken ct = default)
            => Task.FromResult(profile);
    }

    private sealed class StaticWorkspaceAuditAgentProvider(WorkspaceAuditAgentProfile? profile)
        : IWorkspaceAuditAgentProvider
    {
        public Task<WorkspaceAuditAgentProfile?> FindFirstEnabledAuditAgentAsync(
            string workspaceId,
            CancellationToken ct = default)
            => Task.FromResult(profile);
    }

    private sealed class RecordingLlmInvocationService(string response) : ILlmInvocationService
    {
        public LlmInvocationRequest? LastRequest { get; private set; }

        public Task<LlmInvocationResult> InvokeAsync(LlmInvocationRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new LlmInvocationResult
            {
                Success = true,
                ReplyText = response,
            });
        }

        public async IAsyncEnumerable<StreamDelta> InvokeStreamAsync(
            LlmInvocationRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequest = request;
            await Task.CompletedTask;
            yield break;
        }
    }

    private static ToolExecutionContext SampleContext() => new()
    {
        WorkspaceId = "workspace-1",
        SessionId = "session-1",
        AgentInstanceId = "agent-1",
    };

    private static Task<ToolExecutionResult> ExecuteFileSearchAsync(FileSearchTool tool, string argumentsJson) =>
        tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = argumentsJson,
            Context = SampleContext(),
        });

    private static HttpFetchSkill CreateHttpFetchSkill(RecordingWebClient webClient) =>
        new(
            webClient,
            new HttpFetchContentFormatter(new RecordingHtmlToMarkdownConverter(string.Empty)),
            NullLogger<HttpFetchSkill>.Instance);

    private static Task<ToolExecutionResult> ExecuteHttpFetchAsync(HttpFetchSkill tool, string argumentsJson) =>
        tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = argumentsJson,
            Context = SampleContext(),
        });

    private static string JsonEscape(string value)
        => JsonSerializer.Serialize(value)[1..^1];

    private static string ExpectedCurrentDriveRoot()
        => Path.GetPathRoot(Directory.GetCurrentDirectory()) ?? Path.GetFullPath(Path.DirectorySeparatorChar.ToString());

    [GeneratedRegex("^[a-zA-Z0-9_]+$")]
    private static partial Regex ValidToolIdRegex();

    private sealed class RecordingWebClient(WebClientResponse response) : IWebClient
    {
        public WebClientRequest? LastRequest { get; private set; }

        public Task<WebClientResponse> SendAsync(WebClientRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingHtmlToMarkdownConverter(string markdown) : IHtmlToMarkdownConverter
    {
        public string? LastHtml { get; private set; }

        public string Convert(string html)
        {
            LastHtml = html;
            return string.IsNullOrEmpty(markdown) ? HtmlContentExtractor.ToPlainText(html) : markdown;
        }
    }
}
