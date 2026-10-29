using System.Text.Json;
using PuddingCodeIndex.Contracts.Retrieval;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Tools;
using PuddingCodeIndex.Contracts;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <c>workspace_open</c> **工具外壳**的特征测试（S5 接线，2026-10-29）。
/// <para>
/// 服务层的策略（边界、MaxBytes、outline 透传）已由 <see cref="WorkspaceOpenServiceTests"/> 守护；
/// 本文件只钉<b>外壳自己新增的那一层</b>：字符串视图 → 枚举的翻译、JSON 投影、以及把工具接进
/// <c>IWorkspaceOpenService</c> 后参数确实流到了服务。
/// </para>
/// <para>
/// 重点用例是 <see cref="UnknownView_IsRejectedNotSilentlyDowngraded"/>：不认识视图时若按 excerpt
/// 返回，调用方会拿到「声称是 A、内容却是 B」的结果 —— 那比失败更难发现（ADR-089 §8.3）。
/// </para>
/// </summary>
[TestClass]
public sealed class WorkspaceOpenToolCharacterizationTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ws-open-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响断言结论。
        }
    }

    [TestMethod]
    public async Task UnknownView_IsRejectedNotSilentlyDowngraded()
    {
        File.WriteAllLines(Path.Combine(_root, "a.txt"), ["line1", "line2"]);

        // "summary" 不在这里用：规范 §8.3 已定义它，见 DefinedButUnimplementedView_*。
        // 本用例只用**拼错的名字**，它必须落到「不支持的 view」。
        var result = await RunAsync(root => NewTool(root), ("path", "a.txt"), ("view", "summery"));

        Assert.IsFalse(result.Success, "未支持的视图必须失败，不能静默降级成 excerpt。");
        StringAssert.Contains(result.Error, "不支持的 view");
        StringAssert.Contains(result.Error, "summery");
    }

    /// <summary>
    /// 规范 §8.3 已定义、v1 未实现的视图，报错必须与「拼错的名字」区分开，并给出可执行的替代。
    /// <para>
    /// 若两者都报「不支持的 view」，调用方会以为自己写错了名字，于是反复换写法试错 ——
    /// 真实原因是能力缺口。§8.3 要求能力声明如实。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task DefinedButUnimplementedView_IsRejectedWithActionableHint()
    {
        File.WriteAllLines(Path.Combine(_root, "a.txt"), ["line1"]);

        var cases = new (string View, string Alternative)[]
        {
            ("summary", "code_summary"),
            ("relations", "code_callers"),
            ("status", "code_index_status"),
        };

        foreach (var (view, alternative) in cases)
        {
            var result = await RunAsync(root => NewTool(root), ("path", "a.txt"), ("view", view));

            Assert.IsFalse(result.Success, $"view={view} 未实现，必须失败。");
            StringAssert.Contains(result.Error, "已定义", $"view={view} 必须说明这是规范里已有的视图。");
            StringAssert.Contains(result.Error, "尚未实现", $"view={view} 必须说清是能力缺口，不是名字写错。");
            StringAssert.Contains(result.Error, alternative, $"view={view} 必须指向可用的替代工具。");
            Assert.IsFalse(
                result.Error.Contains("不支持的 view", StringComparison.Ordinal),
                $"view={view} 不能报成「不支持的 view」—— 那会让调用方以为是拼写错误。");
        }
    }

    [TestMethod]
    public async Task MissingPath_IsRejectedByArgumentBinding()
    {
        // Path 是 required：字段缺失在**参数绑定层**就被拦下，走不到工具内的判空。
        // 错误文案属于框架，不是我们写的 —— 但“没有 path 不能成功”这个结论必须成立。
        var result = await RunAsync(NewTool, ("view", "excerpt"));

        Assert.IsFalse(result.Success);
        // 不锁错误文案与 Output 形状：这条链路由参数绑定产生，其措辞与装法都不是本工具写的。
        // 唯一稳定的结论是“没有 path 不可能成功”。
        var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        Assert.IsFalse(string.IsNullOrWhiteSpace(detail), "失败必须给出可读的原因。");
    }

    [TestMethod]
    public async Task EmptyPath_IsRejectedByTool()
    {
        // 显式传空串不应被当成“打开工作区根”，也不应被参数绑定拦下 ⇒ 由请求形状校验报缺参数。
        // 2026-10-30：path 不再是 required（ref 与 path 二选一），故错误文案从
        // “path is required（v1 只支持按路径打开）”改为“必须给 ref 或 path 之一”。
        var result = await RunAsync(NewTool, ("path", ""), ("view", "excerpt"));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "ref 或 path");
    }

    [TestMethod]
    public async Task Excerpt_ReturnsSliceInJsonProjection()
    {
        File.WriteAllLines(Path.Combine(_root, "lines.txt"), ["line1", "line2", "line3", "line4"]);

        var result = await RunAsync(NewTool, ("path", "lines.txt"), ("view", "excerpt"),
            ("start_line", 2), ("line_count", 2));

        Assert.IsTrue(result.Success, result.Error);
        using var doc = JsonDocument.Parse(result.Output!);
        var root = doc.RootElement;

        Assert.AreEqual("excerpt", root.GetProperty("view").GetString());
        Assert.IsFalse(root.GetProperty("truncated").GetBoolean());

        var excerpt = root.GetProperty("excerpt");
        Assert.AreEqual(2, excerpt.GetProperty("start_line").GetInt32());
        Assert.AreEqual(4, excerpt.GetProperty("total_line_count").GetInt32(), "总行数必须是文件真实行数。");

        var lines = excerpt.GetProperty("lines").EnumerateArray().Select(e => e.GetString()).ToArray();
        CollectionAssert.AreEqual(new[] { "line2", "line3" }, lines);
    }

    [TestMethod]
    public async Task PathOutsideWorkspace_IsRejectedByServiceThroughTool()
    {
        var outside = Path.Combine(Path.GetTempPath(), "outside-tool-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "secret");

        try
        {
            var result = await RunAsync(NewTool, ("path", outside), ("view", "excerpt"));

            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Error, "不在工作区");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    /// <summary>
    /// ref 与 path 同给必须报错，**不得静默择一**：择一执行会让调用方以为另一种写法生效
    /// （本仓已发生过同类：`line_count` 误写 `lines` 被绑定层静默丢弃、回落默认值 80）。
    /// </summary>
    [TestMethod]
    public async Task RefAndPathTogether_IsRejectedNotSilentlyChosen()
    {
        var result = await RunAsync(NewTool, ("path", "lines.txt"), ("ref", ValidRefToken()), ("view", "excerpt"));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "二选一");
    }

    /// <summary>
    /// ref 单独给出时 v1 尚未接线 ⇒ 必须**显式拒绝**并指明替代路径，
    /// 不能静默回落到 path（那会拿到另一个文件的内容却看起来正常）。
    /// </summary>
    [TestMethod]
    public async Task RefOnly_IsRejectedExplicitly_NotSilentlyIgnored()
    {
        var result = await RunAsync(NewTool, ("ref", ValidRefToken()), ("view", "excerpt"));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "尚未接线");
        StringAssert.Contains(result.Error, "workspace_search");
    }

    /// <summary>既没 ref 也没 path ⇒ 报缺参数，而不是拿空路径去拼一个根目录。</summary>
    [TestMethod]
    public async Task NeitherRefNorPath_IsRejected()
    {
        var result = await RunAsync(NewTool, ("view", "excerpt"));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "ref 或 path");
    }

    /// <summary>非法 ref（前缀/编码不对）必须逐个报原因，不能当成 path 用。</summary>
    [TestMethod]
    public async Task MalformedRef_IsRejectedWithReason()
    {
        var result = await RunAsync(NewTool, ("ref", "Source/A.cs:12"), ("view", "excerpt"));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "不是合法的符号引用");
        StringAssert.Contains(result.Error, SymbolRefV1.TokenPrefix);
    }

    private static string ValidRefToken() => SymbolRefV1
        .Create("proj-1", "Source/A.cs", "M:Ns.Type.Run", 10, 20, "abc123", "roslyn-v1")
        .ToToken();

    private WorkspaceOpenTool NewTool(string root) =>
        new(outlinerRegistry: null, resolver: new FixedScopeResolver(root));

    /// <summary>默认用 <see cref="_root"/> 构造工具，并允许改参数。</summary>
    private Task<ToolExecutionResult> RunAsync(
        Func<string, WorkspaceOpenTool> factory,
        params (string Key, object? Value)[] args)
    {
        var tool = factory(_root);
        var dict = args.ToDictionary(a => a.Key, a => a.Value);
        var arguments = JsonSerializer.Serialize(dict);

        return tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "workspace-open-characterization",
            ArgumentsJson = arguments,
            Context = new ToolExecutionContext
            {
                WorkspaceId = "ws-1",
                SessionId = "session",
                AgentInstanceId = "agent",
            },
        });
    }

    private sealed class FixedScopeResolver(string root) : ICodeIndexScopeResolver
    {
        public Task<ScopeResolution?> ResolveAsync(
            string workspaceId, string? filePath = null, string? scopePath = null,
            string? scopeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<ScopeResolution?>(Build(workspaceId));

        public Task<ScopeResolution> ResolveAndEnsureAsync(
            string workspaceId, string? filePath = null, string? scopePath = null,
            string? scopeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Build(workspaceId));

        private ScopeResolution Build(string workspaceId) => new(
            new CodeIndexScope(workspaceId, "proj-1", root, ScopeState.Active, ScopeSource.Auto),
            IsAutoDetected: true,
            IsNewlyCreated: false);
    }
}
