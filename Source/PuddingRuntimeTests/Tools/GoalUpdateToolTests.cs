using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class GoalUpdateToolTests
{
    [TestMethod]
    public async Task ExecuteCore_AppendSmall_NoSizeWarning()
    {
        // Arrange
        var tmpRoot = Path.Combine(Path.GetTempPath(), "pudding-goal-test-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = PuddingDataPaths.FromRoot(tmpRoot);
        var tool = new GoalUpdateTool(paths, NullLogger<GoalUpdateTool>.Instance);
        var context = new ToolExecutionContext
        {
            WorkspaceId = "ws-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-a",
        };
        try
        {
            // Act: append a small entry
            var result = await tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "call-1",
                ArgumentsJson = """{"append":"hello world"}""",
                Context = context,
            });

            // Assert
            Assert.IsTrue(result.Success, result.Error);
            Assert.IsNotNull(result.Output);
            var json = JsonDocument.Parse(result.Output).RootElement;
            Assert.AreEqual("ok", json.GetProperty("status").GetString());
            Assert.AreEqual("append", json.GetProperty("mode").GetString());
            // size_warning should be null (not present or null)
            if (json.TryGetProperty("size_warning", out var warningProp))
            {
                Assert.AreEqual(JsonValueKind.Null, warningProp.ValueKind);
            }
        }
        finally
        {
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true);
        }
    }

    [TestMethod]
    public async Task ExecuteCore_OverrideExceedsLimit_AddsSizeWarning()
    {
        // Arrange
        var tmpRoot = Path.Combine(Path.GetTempPath(), "pudding-goal-test-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = PuddingDataPaths.FromRoot(tmpRoot);
        var tool = new GoalUpdateTool(paths, NullLogger<GoalUpdateTool>.Instance);
        var context = new ToolExecutionContext
        {
            WorkspaceId = "ws-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-a",
        };
        try
        {
            // Create a large content > 32KB
            var largeContent = new string('A', GoalReadTool.WarnLimitBytes + 1024);
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(largeContent));

            // Act
            var result = await tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "call-1",
                ArgumentsJson = $"{{\"content_base64\":\"{base64}\"}}",
                Context = context,
            });

            // Assert
            Assert.IsTrue(result.Success, result.Error);
            Assert.IsNotNull(result.Output);
            var json = JsonDocument.Parse(result.Output).RootElement;
            Assert.AreEqual("ok", json.GetProperty("status").GetString());
            Assert.AreEqual("override", json.GetProperty("mode").GetString());
            // size_warning should be present and contain warning text
            var warning = json.GetProperty("size_warning").GetString();
            Assert.IsNotNull(warning);
            StringAssert.Contains(warning, "goal.md 已达");
            StringAssert.Contains(warning, "content_base64");
            StringAssert.Contains(warning, "memory/");
        }
        finally
        {
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true);
        }
    }

    [TestMethod]
    public async Task ExecuteCore_AppendCausesExceedLimit_AddsSizeWarning()
    {
        // Arrange
        var tmpRoot = Path.Combine(Path.GetTempPath(), "pudding-goal-test-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = PuddingDataPaths.FromRoot(tmpRoot);
        var tool = new GoalUpdateTool(paths, NullLogger<GoalUpdateTool>.Instance);
        var context = new ToolExecutionContext
        {
            WorkspaceId = "ws-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-a",
        };
        try
        {
            // First, write a file that's already near the limit
            var nearLimitContent = new string('B', GoalReadTool.WarnLimitBytes - 100);
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(nearLimitContent));
            await tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "call-1",
                ArgumentsJson = $"{{\"content_base64\":\"{base64}\"}}",
                Context = context,
            });

            // Act: append to push it over the limit
            var appendContent = new string('C', 500);
            var result = await tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "call-2",
                ArgumentsJson = $"{{\"append\":\"{appendContent}\"}}",
                Context = context,
            });

            // Assert
            Assert.IsTrue(result.Success, result.Error);
            var json = JsonDocument.Parse(result.Output!).RootElement;
            var warning = json.GetProperty("size_warning").GetString();
            Assert.IsNotNull(warning);
            StringAssert.Contains(warning, "32");
        }
        finally
        {
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true);
        }
    }

    [TestMethod]
    public async Task ExecuteCore_OverrideUnderLimit_NoSizeWarning()
    {
        // Arrange
        var tmpRoot = Path.Combine(Path.GetTempPath(), "pudding-goal-test-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = PuddingDataPaths.FromRoot(tmpRoot);
        var tool = new GoalUpdateTool(paths, NullLogger<GoalUpdateTool>.Instance);
        var context = new ToolExecutionContext
        {
            WorkspaceId = "ws-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-a",
        };
        try
        {
            var smallContent = "a small goal file";
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(smallContent));

            // Act
            var result = await tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "call-1",
                ArgumentsJson = $"{{\"content_base64\":\"{base64}\"}}",
                Context = context,
            });

            // Assert
            Assert.IsTrue(result.Success, result.Error);
            var json = JsonDocument.Parse(result.Output!).RootElement;
            // size_warning should be null for small files
            if (json.TryGetProperty("size_warning", out var warningProp))
            {
                Assert.AreEqual(JsonValueKind.Null, warningProp.ValueKind);
            }
        }
        finally
        {
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true);
        }
    }

    [TestMethod]
    public async Task ExecuteCore_WorkspaceFallback_SizeWarningWorks()
    {
        // Arrange: no agent instance, only workspace
        var tmpRoot = Path.Combine(Path.GetTempPath(), "pudding-goal-test-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = PuddingDataPaths.FromRoot(tmpRoot);
        var tool = new GoalUpdateTool(paths, NullLogger<GoalUpdateTool>.Instance);
        var context = new ToolExecutionContext
        {
            WorkspaceId = "ws-fallback",
            SessionId = "session-1",
            AgentInstanceId = "agent-a",
        };
        try
        {
            var largeContent = new string('X', GoalReadTool.WarnLimitBytes + 2048);
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(largeContent));

            // Act: use workspace fallback path (empty agent instance id)
            var result = await tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "call-1",
                ArgumentsJson = $"{{\"content_base64\":\"{base64}\",\"agent_instance_id\":\"\"}}",
                Context = context,
            });

            // Assert
            Assert.IsTrue(result.Success, result.Error);
            var json = JsonDocument.Parse(result.Output!).RootElement;
            Assert.AreEqual("ok", json.GetProperty("status").GetString());
            var warning = json.GetProperty("size_warning").GetString();
            Assert.IsNotNull(warning);
            StringAssert.Contains(warning, "content_base64");
        }
        finally
        {
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true);
        }
    }
}
