using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// TaskManagerTool 持久化测试：
/// ① create 后新实例（模拟重启）能 list 到任务（持久化生效）；
/// ② 不同 AgentInstanceId 完全隔离；
/// ③ 文件损坏时回退空列表不崩溃；
/// ④ 原子写不产生半截/临时文件残留。
/// </summary>
[TestClass]
public sealed class TaskManagerToolTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "pudding-tasks-test-" + Guid.NewGuid().ToString("N")[..8]);

    private static TaskManagerTool CreateTool(string root) =>
        new(NullLogger<TaskManagerTool>.Instance, PuddingDataPaths.FromRoot(root));

    private static ToolExecutionContext Context(string agentId) => new()
    {
        WorkspaceId = "ws-1",
        SessionId = "session-1",
        AgentInstanceId = agentId,
    };

    private static async Task<ToolExecutionResult> RunAsync(
        TaskManagerTool tool, string agentId, string argsJson)
    {
        return await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-" + Guid.NewGuid().ToString("N")[..8],
            ArgumentsJson = argsJson,
            Context = Context(agentId),
        });
    }

    private static async Task<int> CreateTaskAsync(TaskManagerTool tool, string agentId, string title)
    {
        var result = await RunAsync(tool, agentId, $$"""{"operation":"create","title":"{{title}}"}""");
        Assert.IsTrue(result.Success, result.Error);
        var json = JsonDocument.Parse(result.Output!).RootElement;
        Assert.AreEqual("created", json.GetProperty("action").GetString());
        return json.GetProperty("task").GetProperty("id").GetInt32();
    }

    private static async Task<JsonElement> ListAsync(TaskManagerTool tool, string agentId)
    {
        var result = await RunAsync(tool, agentId, """{"operation":"list"}""");
        Assert.IsTrue(result.Success, result.Error);
        return JsonDocument.Parse(result.Output!).RootElement.Clone();
    }

    [TestMethod]
    public async Task Create_PersistsAcrossInstances_ReloadSeesTasks()
    {
        var root = NewRoot();
        try
        {
            var tool1 = CreateTool(root);
            var id = await CreateTaskAsync(tool1, "agent-a", "write report");

            // 新实例（模拟重启）：同一 agent 必须能 list 到已持久化的任务
            var tool2 = CreateTool(root);
            var list = await ListAsync(tool2, "agent-a");

            Assert.AreEqual(1, list.GetProperty("total").GetInt32());
            var tasks = list.GetProperty("tasks");
            Assert.AreEqual(1, tasks.GetArrayLength());
            Assert.AreEqual(id, tasks[0].GetProperty("id").GetInt32());
            Assert.AreEqual("write report", tasks[0].GetProperty("title").GetString());
            Assert.AreEqual("pending", tasks[0].GetProperty("status").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task DifferentAgents_AreFullyIsolated_SameInstance()
    {
        var root = NewRoot();
        try
        {
            var tool = CreateTool(root);
            await CreateTaskAsync(tool, "agent-a", "task for A");
            await CreateTaskAsync(tool, "agent-b", "task for B");

            var listA = await ListAsync(tool, "agent-a");
            var listB = await ListAsync(tool, "agent-b");

            Assert.AreEqual(1, listA.GetProperty("total").GetInt32());
            Assert.AreEqual(1, listB.GetProperty("total").GetInt32());
            Assert.AreEqual("task for A", listA.GetProperty("tasks")[0].GetProperty("title").GetString());
            Assert.AreEqual("task for B", listB.GetProperty("tasks")[0].GetProperty("title").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task DifferentAgents_AreFullyIsolated_AcrossInstances()
    {
        var root = NewRoot();
        try
        {
            var tool1 = CreateTool(root);
            await CreateTaskAsync(tool1, "agent-a", "A-only");
            await CreateTaskAsync(tool1, "agent-b", "B-only");

            // 新实例分别加载各自文件，互不可见
            var tool2 = CreateTool(root);
            var listA = await ListAsync(tool2, "agent-a");
            var listB = await ListAsync(tool2, "agent-b");

            Assert.AreEqual(1, listA.GetProperty("total").GetInt32());
            Assert.AreEqual(1, listB.GetProperty("total").GetInt32());
            Assert.AreEqual("A-only", listA.GetProperty("tasks")[0].GetProperty("title").GetString());
            Assert.AreEqual("B-only", listB.GetProperty("tasks")[0].GetProperty("title").GetString());

            // 每个 agent 的磁盘文件相互独立
            Assert.IsTrue(File.Exists(Path.Combine(root, "agents", "agent-a", "tasks.json")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "agents", "agent-b", "tasks.json")));
            var fileA = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "agents", "agent-a", "tasks.json"))).RootElement;
            var fileB = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "agents", "agent-b", "tasks.json"))).RootElement;
            Assert.AreEqual("A-only", fileA.GetProperty("tasks")[0].GetProperty("title").GetString());
            Assert.AreEqual("B-only", fileB.GetProperty("tasks")[0].GetProperty("title").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task CorruptFile_FallsBackToEmpty_NoCrash_AndRecoversOnNextWrite()
    {
        var root = NewRoot();
        try
        {
            var agentDir = Path.Combine(root, "agents", "agent-a");
            Directory.CreateDirectory(agentDir);
            await File.WriteAllTextAsync(Path.Combine(agentDir, "tasks.json"), "{ this is not valid json !!!");

            var tool = CreateTool(root);
            var list = await ListAsync(tool, "agent-a");
            Assert.AreEqual(0, list.GetProperty("total").GetInt32());

            // 后续写操作必须能继续工作，并用合法内容覆盖损坏文件
            await CreateTaskAsync(tool, "agent-a", "after corruption");
            var list2 = await ListAsync(tool, "agent-a");
            Assert.AreEqual(1, list2.GetProperty("total").GetInt32());

            var text = await File.ReadAllTextAsync(Path.Combine(agentDir, "tasks.json"));
            var doc = JsonDocument.Parse(text);
            Assert.AreEqual(1, doc.RootElement.GetProperty("tasks").GetArrayLength());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task AtomicWrite_NoTmpLeftovers_FileAlwaysValid_NextIdKeepsIncreasing()
    {
        var root = NewRoot();
        try
        {
            var tool = CreateTool(root);
            await CreateTaskAsync(tool, "agent-a", "task 1");
            await CreateTaskAsync(tool, "agent-a", "task 2");
            var upd = await RunAsync(tool, "agent-a", """{"operation":"update_status","task_id":1,"status":"in-progress"}""");
            Assert.IsTrue(upd.Success, upd.Error);
            var del = await RunAsync(tool, "agent-a", """{"operation":"delete","task_id":2}""");
            Assert.IsTrue(del.Success, del.Error);

            var agentDir = Path.Combine(root, "agents", "agent-a");
            var tmpFiles = Directory.GetFiles(agentDir, "*.tmp");
            Assert.AreEqual(0, tmpFiles.Length, "原子写后不得残留半截 .tmp 文件");

            var file = Path.Combine(agentDir, "tasks.json");
            Assert.IsTrue(File.Exists(file));

            // 文件始终是合法 JSON，且只含当前任务
            var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            var tasks = doc.RootElement.GetProperty("tasks");
            Assert.AreEqual(1, tasks.GetArrayLength());
            Assert.AreEqual("in-progress", tasks[0].GetProperty("status").GetString());

            // nextId 已越过被删除的 id=2，新任务应获得 id=3
            var newId = await CreateTaskAsync(tool, "agent-a", "task 3");
            Assert.AreEqual(3, newId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task UpdateAndDelete_PersistAcrossInstances()
    {
        var root = NewRoot();
        try
        {
            var tool1 = CreateTool(root);
            var id = await CreateTaskAsync(tool1, "agent-a", "do work");

            var upd = await RunAsync(tool1, "agent-a", $$"""{"operation":"update_status","task_id":{{id}},"status":"completed"}""");
            Assert.IsTrue(upd.Success, upd.Error);

            var del = await RunAsync(tool1, "agent-a", $$"""{"operation":"delete","task_id":{{id}}}""");
            Assert.IsTrue(del.Success, del.Error);

            // 新实例：删除后的状态持久化
            var tool2 = CreateTool(root);
            var list = await ListAsync(tool2, "agent-a");
            Assert.AreEqual(0, list.GetProperty("total").GetInt32());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task NextId_PersistsAcrossInstances()
    {
        var root = NewRoot();
        try
        {
            var tool1 = CreateTool(root);
            await CreateTaskAsync(tool1, "agent-a", "first");   // id=1
            await CreateTaskAsync(tool1, "agent-a", "second");  // id=2

            var tool2 = CreateTool(root);
            var id3 = await CreateTaskAsync(tool2, "agent-a", "third");
            Assert.AreEqual(3, id3, "nextId 必须持久化，重载后 id 继续递增");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task OutputContract_Unchanged()
    {
        var root = NewRoot();
        try
        {
            var tool = CreateTool(root);

            // create → { action: "created", task: {...} }
            var id = await CreateTaskAsync(tool, "agent-a", "contract task");

            // list → { total, tasks }
            var list = await ListAsync(tool, "agent-a");
            Assert.IsTrue(list.TryGetProperty("total", out _));
            Assert.IsTrue(list.TryGetProperty("tasks", out _));

            // update_status → { action: "updated", task_id, status }
            var upd = await RunAsync(tool, "agent-a", $$"""{"operation":"update_status","task_id":{{id}},"status":"completed"}""");
            Assert.IsTrue(upd.Success, upd.Error);
            var updJson = JsonDocument.Parse(upd.Output!).RootElement;
            Assert.AreEqual("updated", updJson.GetProperty("action").GetString());
            Assert.AreEqual(id, updJson.GetProperty("task_id").GetInt32());
            Assert.AreEqual("completed", updJson.GetProperty("status").GetString());

            // delete → { action: "deleted", task_id }
            var del = await RunAsync(tool, "agent-a", $$"""{"operation":"delete","task_id":{{id}}}""");
            Assert.IsTrue(del.Success, del.Error);
            var delJson = JsonDocument.Parse(del.Output!).RootElement;
            Assert.AreEqual("deleted", delJson.GetProperty("action").GetString());
            Assert.AreEqual(id, delJson.GetProperty("task_id").GetInt32());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task MissingAgentId_FallsBackToDefaultKey_NoCrash()
    {
        var root = NewRoot();
        try
        {
            var tool = CreateTool(root);
            // AgentInstanceId 为空时回退到 "default" 键，不抛异常
            var result = await tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "call-default",
                ArgumentsJson = """{"operation":"create","title":"anonymous task"}""",
                Context = new ToolExecutionContext
                {
                    WorkspaceId = "ws-1",
                    SessionId = "session-1",
                    AgentInstanceId = string.Empty,
                },
            });
            Assert.IsTrue(result.Success, result.Error);
            Assert.IsTrue(File.Exists(Path.Combine(root, "agents", "default", "tasks.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
