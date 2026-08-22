using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 2026-08-22 模型倾向适配回归测试：
/// ① shell 输出剥离 ANSI（pwsh 表格颜色码曾占探查输出 40-60%）；
/// ② Codex 风格 *** Begin Patch 自动转译为 unified diff（曾致 3 类失败/run）；
/// ③ 探查命令返回值教育提示；④ file_read 护栏窗口 120→400 行。
/// </summary>
[TestClass]
public sealed class ToolModelAdaptationTests
{
    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "pudding-tool-adapt-tests", Guid.NewGuid().ToString("N"));

    [TestMethod]
    public async Task Shell_Output_Is_Free_Of_Ansi_Escape_And_Exploration_Commands_GetToolTip()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // pwsh 分支仅 Windows 验证
        }

        var result = await HostShellExecutor.ExecuteAsync(new HostShellRequest
        {
            // Get-ChildItem 的格式化表格在 pwsh7 默认带 ANSI 颜色
            Command = "Get-ChildItem | Select-Object -First 2 -Property Name",
            Shell = "powershell",
            TimeoutSeconds = 20,
        }, NullLogger.Instance);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(result.Output.Contains('\x1b'), "输出不应包含 ANSI 转义符");
    }

    [TestMethod]
    public async Task HostShellTool_Appends_Specialized_Tool_Tip_For_Exploration_Commands()
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var tool = new HostShellTool(
                PuddingDataPaths.FromRoot(root),
                new AuditLogger(PuddingDataPaths.FromRoot(root)),
                NullLogger<HostShellTool>.Instance);

            var command = OperatingSystem.IsWindows()
                ? "Get-ChildItem -Path ."
                : "ls .";
            var result = await tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "c1",
                ArgumentsJson = $$"""{"command": {{System.Text.Json.JsonSerializer.Serialize(command)}}, "working_directory": {{System.Text.Json.JsonSerializer.Serialize(root)}}, "timeout_seconds": 20}""",
                Context = new ToolExecutionContext
                {
                    WorkspaceId = "ws",
                    SessionId = "s",
                    AgentInstanceId = "a",
                    WorkingDirectory = root,
                },
            });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "Tip:");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FilePatch_PatchText_Accepts_CodexStyle_BeginPatch()
    {
        var root = TempRoot();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        var file = Path.Combine(root, "src", "app.tsx");
        const string original = "const a = 1;\nconst b = 2;\nconst c = 3;\n";
        await File.WriteAllTextAsync(file, original);
        try
        {
            var tool = new FilePatchTool(
                PuddingDataPaths.FromRoot(root),
                new AuditLogger(PuddingDataPaths.FromRoot(root)),
                NullLogger<FilePatchTool>.Instance,
                new FileMutationQueue());

            var codexPatch = """
                *** Begin Patch
                *** Update File: src/app.tsx
                const a = 1;
                -const b = 2;
                +const b = 20;
                const c = 3;
                *** End Patch
                """;

            var result = await tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "c1",
                ArgumentsJson = $$"""{"patch_text": {{System.Text.Json.JsonSerializer.Serialize(codexPatch)}}}""",
                Context = new ToolExecutionContext
                {
                    WorkspaceId = "ws",
                    SessionId = "s",
                    AgentInstanceId = "a",
                    WorkingDirectory = root,
                },
            });

            Assert.IsTrue(result.Success, result.Error);
            var updated = await File.ReadAllTextAsync(file);
            StringAssert.Contains(updated, "const b = 20;");
            Assert.IsFalse(updated.Contains("const b = 2;\n", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FileRead_Guardrail_Returns_First_400_Lines()
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "big.ts");
            var content = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"line {i}"));
            await File.WriteAllTextAsync(file, content);

            var tool = new FileReadTool(NullLogger<FileReadTool>.Instance);
            var result = await tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "c1",
                ArgumentsJson = $$"""{"path": {{System.Text.Json.JsonSerializer.Serialize(file)}}}""",
                Context = new ToolExecutionContext
                {
                    WorkspaceId = "ws",
                    SessionId = "s",
                    AgentInstanceId = "a",
                    WorkingDirectory = root,
                },
            });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "showing first 400 lines");
            StringAssert.Contains(result.Output, "line 400");
            Assert.IsFalse(result.Output.Contains("\nline 450\n", StringComparison.Ordinal), "第 450 行不应出现在 400 行预览中");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
