using System.Text;
using System.Text.Json;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 守卫 file_write 的成功回执：必须报**实际落盘路径**，而不是回显入参原文。
///
/// <para>动机（实测踩过两次）：<c>args.Path</c> 为相对路径时，它相对的是执行工作目录 / 工作区根，
/// 与调用方心里的根不一定一致。旧回执 <c>Wrote temp/x.ps1</c> 只重复了入参，于是「写到哪儿去了」
/// 在回执里根本看不出来 —— 文件其实落在工作区根，而调用方按仓库根去找，找不到又误判成「工具没写」。</para>
///
/// <para>断言分两半，缺一侧就是假门禁：相对路径必须带出解析后的绝对路径（否则缺陷仍在），
/// 绝对路径不得多出无用的解析注记（否则把「总是拼接注记」也当成合格）。</para>
/// </summary>
[TestClass]
public sealed class FileWriteToolPathEchoTests
{
    private string _tempDir = null!;
    private string _originalRepoRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pudding-echo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _originalRepoRoot = Environment.GetEnvironmentVariable("PUDDING_REPOSITORY_ROOT") ?? string.Empty;
        Environment.SetEnvironmentVariable("PUDDING_REPOSITORY_ROOT", _tempDir);
        HostFileToolPaths.InvalidateWorkspaceRootCache();
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("PUDDING_REPOSITORY_ROOT", _originalRepoRoot);
        HostFileToolPaths.InvalidateWorkspaceRootCache();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// 相对路径：回执必须含解析后的绝对路径。改回 <c>Wrote {args.Path}</c> 时本用例转红 ——
    /// 这正是它存在的理由（回显入参是个「看起来正常」的实现）。
    /// </summary>
    [TestMethod]
    public async Task FileWrite_RelativePath_ReportsResolvedAbsolutePath()
    {
        var result = await ExecuteAsync(new FileWriteTool(), new Dictionary<string, object?>
        {
            ["path"] = "sub/probe.txt",
            ["content"] = "hello",
        });

        Assert.IsTrue(result.Success, result.Error);

        var expected = Path.GetFullPath(Path.Combine(_tempDir, "sub", "probe.txt"));
        StringAssert.Contains(result.Output, expected,
            "回执必须报出实际落盘路径，否则相对路径写到哪里在回执里不可见");
        Assert.IsTrue(File.Exists(expected), "文件必须真的落在解析后的位置");
    }

    /// <summary>
    /// 绝对路径的正向对照：不得追加解析注记。
    /// 没有这一条，一个「无条件拼接 (resolved from ...)」的实现也能通过上面那条用例。
    /// </summary>
    [TestMethod]
    public async Task FileWrite_AbsolutePath_DoesNotAddResolutionNote()
    {
        var target = Path.Combine(_tempDir, "plain.txt");
        var result = await ExecuteAsync(new FileWriteTool(), new Dictionary<string, object?>
        {
            ["path"] = target,
            ["content"] = "hello",
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, target, result.Output);
        Assert.IsFalse(result.Output.Contains("resolved from", StringComparison.Ordinal),
            "调用方已经给了绝对路径时不应再追加解析注记");
    }

    private static Task<ToolExecutionResult> ExecuteAsync<TArgs>(
        PuddingToolBase<TArgs> tool, IReadOnlyDictionary<string, object?> parameters)
        where TArgs : class
    {
        return tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-echo-1",
            ArgumentsJson = JsonSerializer.Serialize(parameters),
            Context = new ToolExecutionContext
            {
                AgentInstanceId = "agent",
                WorkspaceId = "workspace",
                SessionId = "session",
            },
        });
    }
}
