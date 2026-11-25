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

    [TestMethod]
    public async Task ExecuteCore_Base64DecodesToNonUtf8Bytes_FailsWithoutWriting()
    {
        // 回归：2026-11-25 goal.md 被写入 1747 处 U+FFFD 而工具回「成功」。
        // 合法 base64 但字节流不是 UTF-8 时必须 fail-closed，而不是用替换字符填补。
        var tmpRoot = Path.Combine(Path.GetTempPath(), "pudding-goal-test-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = PuddingDataPaths.FromRoot(tmpRoot);
        var tool = new GoalUpdateTool(paths, NullLogger<GoalUpdateTool>.Instance);
        var context = new ToolExecutionContext
        {
            WorkspaceId = "ws-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-binary",
        };
        try
        {
            // 0xFF 0xFE 在任何 UTF-8 上下文中都是非法起始字节
            var base64 = Convert.ToBase64String(new byte[] { 0xFF, 0xFE, 0x41 });

            var result = await tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "call-1",
                ArgumentsJson = $"{{\"content_base64\":\"{base64}\"}}",
                Context = context,
            });

            Assert.IsFalse(result.Success, "非 UTF-8 字节应被拒绝");
            StringAssert.Contains(result.Error!, "UTF-8");
            var goalPath = Path.Combine(paths.AgentInstanceRoot("agent-binary"), "goal.md");
            Assert.IsFalse(File.Exists(goalPath), "拒绝时不得落下任何文件");
        }
        finally
        {
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true);
        }
    }

    [TestMethod]
    public async Task ExecuteCore_ContentWithReplacementChar_FailsWithoutWriting()
    {
        // 回归：即使字节流是合法 UTF-8，内容里已经含 U+FFFD 也说明上游已坏，不能写进 goal.md。
        var tmpRoot = Path.Combine(Path.GetTempPath(), "pudding-goal-test-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = PuddingDataPaths.FromRoot(tmpRoot);
        var tool = new GoalUpdateTool(paths, NullLogger<GoalUpdateTool>.Instance);
        var context = new ToolExecutionContext
        {
            WorkspaceId = "ws-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-poisoned",
        };
        try
        {
            var poisoned = "# goal\n\n这一段已经被损��了\n";
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(poisoned));

            var result = await tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "call-1",
                ArgumentsJson = $"{{\"content_base64\":\"{base64}\"}}",
                Context = context,
            });

            Assert.IsFalse(result.Success, "含 U+FFFD 的内容应被拒绝");
            StringAssert.Contains(result.Error!, "U+FFFD");
            var goalPath = Path.Combine(paths.AgentInstanceRoot("agent-poisoned"), "goal.md");
            Assert.IsFalse(File.Exists(goalPath), "拒绝时不得落下任何文件");
        }
        finally
        {
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true);
        }
    }

    [TestMethod]
    public async Task ExecuteCore_Override_WritesBytesWithoutBomAndExactContent()
    {
        // 回归：Encoding.UTF8 静态实例会写 BOM，而 goal.md 约定禁 BOM。
        var tmpRoot = Path.Combine(Path.GetTempPath(), "pudding-goal-test-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = PuddingDataPaths.FromRoot(tmpRoot);
        var tool = new GoalUpdateTool(paths, NullLogger<GoalUpdateTool>.Instance);
        var context = new ToolExecutionContext
        {
            WorkspaceId = "ws-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-bom",
        };
        try
        {
            const string body = "# goal\n\n中文内容，含 ASCII `code`。\n";
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));

            var result = await tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "call-1",
                ArgumentsJson = $"{{\"content_base64\":\"{base64}\"}}",
                Context = context,
            });

            Assert.IsTrue(result.Success, result.Error);

            var goalPath = Path.Combine(paths.AgentInstanceRoot("agent-bom"), "goal.md");
            var bytes = File.ReadAllBytes(goalPath);

            Assert.IsFalse(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "goal.md 不得带 UTF-8 BOM");
            CollectionAssert.AreEqual(
                new UTF8Encoding(false).GetBytes(body), bytes,
                "磁盘字节应与内容逐字节一致（无 BOM、无额外行尾）");
        }
        finally
        {
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true);
        }
    }

    // ═══════════════════════════════════════
    // append 不得损失性解码旧文件（P1-4）
    // ═══════════════════════════════════════

    [TestMethod]
    public async Task ExecuteCore_AppendOnCorruptExistingFile_FailsAndKeepsOriginalBytes()
    {
        // 回归（2026-11-25 隔离实验）：新输入有严格校验，但 append 仍用 File.ReadAllText
        // 读旧 goal.md——宽松解码把旧的 0xFF 永久换成 U+FFFD 再整文件写回，工具回执成功。
        // 一次 append 就把损坏固化下来，而且此后每次读到的都是「合法但不真」的内容。
        var tmpRoot = Path.Combine(Path.GetTempPath(), "pudding-goal-test-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = PuddingDataPaths.FromRoot(tmpRoot);
        var tool = new GoalUpdateTool(paths, NullLogger<GoalUpdateTool>.Instance);
        var context = new ToolExecutionContext
        {
            WorkspaceId = "ws-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-corrupt",
        };
        try
        {
            var goalPath = Path.Combine(paths.AgentInstanceRoot("agent-corrupt"), "goal.md");
            Directory.CreateDirectory(Path.GetDirectoryName(goalPath)!);
            // 合法 UTF-8 前缀 + 非法字节 0xFF：只看前两百字难以看出异常，正是它危险的地方。
            var original = new byte[] { 0x23, 0x20, 0x67, 0x6F, 0x61, 0x6C, 0x0A, 0xFF, 0x0A };
            File.WriteAllBytes(goalPath, original);

            var result = await tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "call-1",
                ArgumentsJson = "{\"append\":\"new line\"}",
                Context = context,
            });

            Assert.IsFalse(result.Success, "旧文件含非法字节时 append 必须失败，不能静默改写");
            CollectionAssert.AreEqual(original, File.ReadAllBytes(goalPath),
                "拒绝时必须保留原字节：一份还能救的文件不应在失败时变得更坏");
        }
        finally
        {
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true);
        }
    }

    [TestMethod]
    public async Task ExecuteCore_Override_RepairsCorruptFile()
    {
        // 正向对照：append 拒绝退化了文件，override 必须是**唯一出路**。
        // 若 override 也去解析旧内容，这份文件就再也救不回来了。
        var tmpRoot = Path.Combine(Path.GetTempPath(), "pudding-goal-test-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = PuddingDataPaths.FromRoot(tmpRoot);
        var tool = new GoalUpdateTool(paths, NullLogger<GoalUpdateTool>.Instance);
        var context = new ToolExecutionContext
        {
            WorkspaceId = "ws-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-repair",
        };
        try
        {
            var goalPath = Path.Combine(paths.AgentInstanceRoot("agent-repair"), "goal.md");
            Directory.CreateDirectory(Path.GetDirectoryName(goalPath)!);
            File.WriteAllBytes(goalPath, new byte[] { 0x23, 0xFF, 0x0A });

            const string body = "# goal\n\n修好了\n";
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));

            var result = await tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "call-1",
                ArgumentsJson = $"{{\"content_base64\":\"{base64}\"}}",
                Context = context,
            });

            Assert.IsTrue(result.Success, result.Error);
            CollectionAssert.AreEqual(
                new UTF8Encoding(false).GetBytes(body), File.ReadAllBytes(goalPath),
                "override 必须能用有效内容整体替换掉已损坏的文件");
        }
        finally
        {
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true);
        }
    }
}
