using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Skills;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// SkillHubTool（SKILL Hub 进程内直连版）Runtime 单元测试。
/// <para>覆盖：feature flag（SkillHub:Enabled=false → capability/503 语义）与「关闭时不触达任何依赖」，
/// 以及 flag 的优先级高于 action 分发（未知 action 同样先被 flag 拦下）。</para>
/// <para>背景：本工具的契约实现（ISkillHubService）是 Scoped、内含 scoped PlatformDbContext，
/// 工具（Singleton）经 <see cref="IServiceScopeFactory"/> 每次调用建 scope 解析。开关关闭时必须
/// <b>完全不创建 scope</b>，否则等于在"未启用"状态下仍然触达数据库 —— 本测试用会抛异常的
/// scope 工厂把这条不变量钉死。</para>
/// <para>说明：本层只测 Runtime 工具行为（薄适配器）；Hub 的业务语义由 SkILL Hub 服务层测试负责，
/// 此处不需要任何 fake 服务实现，因为开关分支在解析服务之前就返回。</para>
/// </summary>
[TestClass]
public sealed class SkillHubToolTests
{
    private const string WorkspaceId = "ws-1";
    private const string AgentId = "agent-1";
    private const string SessionId = "session-1";

    // ─────────────────────────────────────────────────────────────
    // 构造与运行帮助
    // ─────────────────────────────────────────────────────────────

    private static ToolExecutionContext Context() => new()
    {
        WorkspaceId = WorkspaceId,
        SessionId = SessionId,
        AgentInstanceId = AgentId,
    };

    private static SkillHubTool Tool(IServiceScopeFactory scopeFactory, TempDataRoot temp, bool enabled) =>
        new(scopeFactory, Options.Create(new SkillHubFeatureOptions { Enabled = enabled }), new AgentSkillFileService(temp.Paths));

    private static async Task<ToolExecutionResult> RunAsync(SkillHubTool tool, string argsJson)
        => await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-" + Guid.NewGuid().ToString("N")[..8],
            ArgumentsJson = argsJson,
            Context = Context(),
        });

    /// <summary>兼容两种错误体形状：直接根对象，或 {"error": {...}} 包装。</summary>
    private static JsonElement ParseError(ToolExecutionResult result)
    {
        Assert.IsFalse(result.Success, "expected failure but succeeded");
        Assert.IsNotNull(result.Error);
        var root = JsonDocument.Parse(result.Error!).RootElement;
        return root.TryGetProperty("error", out var inner) ? inner : root;
    }

    // ─────────────────────────────────────────────────────────────
    // 错误分支：feature flag
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Disabled_ReturnsCapabilityMissing_AndDoesNotCreateScope()
    {
        using var temp = new TempDataRoot();
        var scopeFactory = new ThrowingScopeFactory();
        var tool = Tool(scopeFactory, temp, enabled: false);

        var result = await RunAsync(tool, """{"action":"stats"}""");

        Assert.IsFalse(result.Success, "特性开关关闭时必须返回失败，而不是空结果");
        Assert.AreEqual(0, scopeFactory.Calls, "特性开关关闭时不得创建 scope（不得触达任何依赖）");

        var error = ParseError(result);
        Assert.AreEqual("error", error.GetProperty("status").GetString());
        Assert.AreEqual(503, error.GetProperty("statusCode").GetInt32());
        StringAssert.Contains(
            error.GetProperty("message").GetString(),
            "SkillHub:Enabled=false",
            "错误信息应明确指出是哪个配置键导致能力缺失");
    }

    [TestMethod]
    public async Task Disabled_BlocksEveryAction_IncludingUnknown()
    {
        using var temp = new TempDataRoot();
        var scopeFactory = new ThrowingScopeFactory();
        var tool = Tool(scopeFactory, temp, enabled: false);

        // 开关判定必须先于 action 分发：连未知 action 也应被 flag 拦下，而不是走到分发/解析服务。
        var result = await RunAsync(tool, """{"action":"definitely-not-an-action"}""");

        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, scopeFactory.Calls);
        Assert.AreEqual(503, ParseError(result).GetProperty("statusCode").GetInt32());
    }

    // ─────────────────────────────────────────────────────────────
    // 测试替身
    // ─────────────────────────────────────────────────────────────

    /// <summary>任何一次 CreateScope 都视为违例：用于证明开关关闭时"不执行任何操作"。</summary>
    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public int Calls { get; private set; }

        public IServiceScope CreateScope()
        {
            Calls++;
            throw new InvalidOperationException("特性开关关闭时不应创建 scope。");
        }
    }

    private sealed class TempDataRoot : IDisposable
    {
        public TempDataRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-skill-hub-tool-tests", Guid.NewGuid().ToString("N"));
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
