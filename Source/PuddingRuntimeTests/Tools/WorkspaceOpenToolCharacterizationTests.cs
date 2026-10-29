using System.Text.Json;
using PuddingCodeIndex.Contracts.Retrieval;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Tools;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services.Retrieval;
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
            // 2026-10-30：relations 已实现（WorkspaceOpenRelationsTests 守护），不再属于本用例。
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

    /// <summary>
    /// ref 请求在**没有事实源**时必须显式拒绝，而不是回落到 path。
    /// <para>
    /// 回落是最坏的一类错误：调用方以为自己拿到的是那个符号，实际拿到的是另一个文件的内容，
    /// 而且结果看起来完全正常。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task Ref_WithoutRefSource_IsRejectedNotFallingBackToPath()
    {
        WriteRows("Source/A.cs", 30);

        var result = await RunAsync(NewTool, ("ref", ValidRefToken()), ("view", "excerpt"));

        Assert.IsFalse(result.Success, "没有事实源时 ref 必须失败。");
        StringAssert.Contains(result.Error, "尚未接线");
        Assert.IsFalse(
            (result.Output ?? string.Empty).Contains("ROW-01", StringComparison.Ordinal),
            "绝不能静默回落到 path 把文件内容返回。");
    }

    /// <summary>
    /// <b>本轮修的真实缺陷的守卫</b>（2026-10-30）：工具层曾无条件拒绝 ref，而服务层早已支持，
    /// 于是适配器与三态判定全部就绪、二进制里也在，线上却一条 ref 都走不通。
    /// 本用例走工具自己的 ExecuteCoreAsync，断言 ref 真的被拿去打开了。
    /// </summary>
    [TestMethod]
    public async Task Ref_Fresh_OpensExactlyTheRefRange()
    {
        WriteRows("Source/A.cs", 30);

        // ref 记的是 10..20；Fresh 判定通过后必须**只**返回这一段。
        var result = await RunAsync(
            root => NewToolWithRef(root, new FixedRefSource("abc123", ("Source/A.cs", 10, 20))),
            ("ref", ValidRefToken()),
            ("view", "excerpt"));

        Assert.IsTrue(result.Success, result.Error);

        using var doc = JsonDocument.Parse(result.Output!);
        var excerpt = doc.RootElement.GetProperty("excerpt");
        Assert.AreEqual(10, excerpt.GetProperty("start_line").GetInt32());
        Assert.AreEqual(11, excerpt.GetProperty("line_count").GetInt32());

        var lines = excerpt.GetProperty("lines").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.AreEqual("ROW-10", lines[0], "ref 的首行必须是 ref 记录的起始行，不是文件第一行。");
        Assert.AreEqual("ROW-20", lines[^1]);
    }

    /// <summary>
    /// STALE 一律报 <c>stale_ref</c>，即使重定位成功也不代替打开（§8.3）。
    /// 断言里必须同时出现「stale”与候选提示，否则调用方无法决定下一步。
    /// </summary>
    [TestMethod]
    public async Task Ref_Stale_ReturnsStaleRefWithRelocationHint()
    {
        WriteRows("Source/A.cs", 60);

        var result = await RunAsync(
            root => NewToolWithRef(root, new FixedRefSource("changed-hash", ("Source/A.cs", 40, 42))),
            ("ref", ValidRefToken()),
            ("view", "excerpt"));

        Assert.IsFalse(result.Success, "STALE 的 ref 不得直接打开。");
        StringAssert.Contains(result.Error, "stale_ref");
        StringAssert.Contains(result.Error, "候选:", "有唯一候选时必须给出重定位提示。");
        Assert.IsFalse(
            (result.Output ?? string.Empty).Contains("ROW-40", StringComparison.Ordinal),
            "候选仅供参考，不能代替打开。");
    }

    private void WriteRows(string relativePath, int count)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllLines(full, Enumerable.Range(1, count).Select(i => $"ROW-{i:00}"));
    }

    private WorkspaceOpenTool NewToolWithRef(string root, ISymbolRefSource refSource) =>
        new(outlinerRegistry: null, resolver: new FixedScopeResolver(root), refSource: refSource);

    /// <summary>只回答两个事实的测试替身：当前 hash 与候选位置。</summary>
    private sealed class FixedRefSource(
        string? currentHash,
        params (string FilePath, int Start, int End)[] candidates) : ISymbolRefSource
    {
        public Task<string?> GetFileContentHashAsync(
            string workspaceId, string projectId, string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(currentHash);

        public Task<IReadOnlyList<SymbolRefRelocation>> FindSymbolCandidatesAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SymbolRefRelocation>>(
                candidates.Select(c => new SymbolRefRelocation(c.FilePath, c.Start, c.End)).ToList());
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
