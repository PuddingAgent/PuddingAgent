using System.Text.Json;
using PuddingCode.Tools.Definitions;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 前端改进 #1：工具展示投影（presentation）管线 —— 工具侧 Present 声明 + wire 词表。
/// <para>
/// 契约来源：<c>PuddingCore/Tools/Definitions/ToolDefinition.cs</c> §14（tool-owned presentation）。
/// 覆盖：四个已声明工具的 kind/meta 事实、无声明 ⇒ generic、presenter 抛异常 ⇒ generic（fail-open）、
/// meta 只写确有事实（拿不到就省略，绝不猜值）。
/// </para>
/// </summary>
[TestClass]
public sealed class ToolPresentationProjectorTests
{
    private static IToolPresentationProjector Projector => ToolPresentationProjector.Default;

    [TestMethod]
    public void TerminalStart_CallPhase_UsesArgumentsAsCommandFacts()
    {
        var wire = Projector.Project(
            "terminal_start",
            """{"command":"dotnet build Source/PuddingRuntime/PuddingRuntime.csproj","cwd":"E:\\repo"}""",
            null);

        Assert.AreEqual("terminal", WireKind(wire));
        Assert.AreEqual("dotnet build Source/PuddingRuntime/PuddingRuntime.csproj", MetaString(wire, "command"));
        Assert.AreEqual("E:\\repo", MetaString(wire, "cwd"));
        Assert.IsNull(MetaValue(wire, "job_id"), "启动阶段还没有 job_id 事实，不得凭空补。");
        Assert.IsNull(MetaValue(wire, "exit_code"), "启动阶段 job 仍在运行，不得凭空补 exit_code。");
    }

    [TestMethod]
    public void TerminalStart_ResultPhase_AddsJobSnapshotFacts()
    {
        var result = """
            {"job":{"job_id":"8cd5358a774f","process_id":"8cd5358a774f","session_id":"s-1",
            "command":"dotnet test --filter X","cwd":"E:\\repo","status":"Running","exit_code":null},
            "output":null,"next_action":"Call terminal_wait once with job_id."}
            """;

        var wire = Projector.Project("terminal_start", """{"command":"dotnet test --filter X"}""", result);

        Assert.AreEqual("terminal", WireKind(wire));
        Assert.AreEqual("dotnet test --filter X", MetaString(wire, "command"));
        Assert.AreEqual("E:\\repo", MetaString(wire, "cwd"));
        Assert.AreEqual("8cd5358a774f", MetaString(wire, "job_id"));
        Assert.IsNull(MetaValue(wire, "exit_code"), "job 快照里 exit_code 为 null ⇒ 省略该键（不得填 0/默认值）。");
    }

    [TestMethod]
    public void TerminalWait_CallPhase_OnlyReportsJobId_NeverFabricatesCommand()
    {
        var wire = Projector.Project("terminal_wait", """{"job_id":"8cd5358a774f","wait_seconds":300}""", null);

        Assert.AreEqual("terminal", WireKind(wire));
        Assert.AreEqual("8cd5358a774f", MetaString(wire, "job_id"));
        Assert.IsNull(
            MetaValue(wire, "command"),
            "terminal_wait 的参数里没有 command 事实，参数阶段不得凭空补（命令只在结果 job 快照里）。");
    }

    [TestMethod]
    public void TerminalWait_ResultPhase_ReadsCommandAndExitCodeFromJobSnapshot()
    {
        var result = """
            {"result":{"job":{"job_id":"8cd5358a774f","process_id":"8cd5358a774f","session_id":"s-1",
            "command":"dotnet test","cwd":"E:\\repo","status":"Exited","exit_code":0},
            "offset":0,"next_offset":0,"total_lines":0,"truncated":false,"command_failed":false,
            "output":"ok","lines":[],"handle":null,"recovery":null},
            "next_action":"Job is no longer running."}
            """;

        var wire = Projector.Project("terminal_wait", """{"job_id":"8cd5358a774f"}""", result);

        Assert.AreEqual("terminal", WireKind(wire));
        Assert.AreEqual("dotnet test", MetaString(wire, "command"));
        Assert.AreEqual("E:\\repo", MetaString(wire, "cwd"));
        Assert.AreEqual("8cd5358a774f", MetaString(wire, "job_id"));
        Assert.AreEqual(0, MetaInt(wire, "exit_code"));
    }

    [TestMethod]
    public void TerminalStart_CamelCaseArguments_ResolveToSameFacts()
    {
        var wire = Projector.Project("terminal_start", """{"Command":"dotnet build","Cwd":"E:\\repo"}""", null);

        Assert.AreEqual("terminal", WireKind(wire));
        Assert.AreEqual("dotnet build", MetaString(wire, "command"));
        Assert.AreEqual("E:\\repo", MetaString(wire, "cwd"));
    }

    [TestMethod]
    public void FilePatch_ReportsSingleFilePath()
    {
        var wire = Projector.Project(
            "file_patch",
            """{"path":"Source/A.cs","operations":[{"type":"replace","old_text":"a","new_text":"b"}]}""",
            null);

        Assert.AreEqual("diff", WireKind(wire));
        Assert.AreEqual("Source/A.cs", MetaString(wire, "path"));
    }

    [TestMethod]
    public void FilePatch_BatchWithOneDistinctPath_ReportsThatPath()
    {
        var wire = Projector.Project(
            "file_patch",
            """{"patches":[{"path":"Source/A.cs","operations":[]},{"path":"Source/A.cs","operations":[]}]}""",
            null);

        Assert.AreEqual("diff", WireKind(wire));
        Assert.AreEqual("Source/A.cs", MetaString(wire, "path"));
    }

    [TestMethod]
    public void FilePatch_BatchWithDistinctPaths_OmitsPathAndMeta()
    {
        var wire = Projector.Project(
            "file_patch",
            """{"patches":[{"path":"Source/A.cs","operations":[]},{"path":"Source/B.cs","operations":[]}]}""",
            null);

        Assert.AreEqual("diff", WireKind(wire));
        Assert.AreEqual(
            """{"kind":"diff"}""",
            wire.GetRawText(),
            "路径不唯一 ⇒ 既不能只报一个（误导），也不能造一个 ⇒ meta 整键省略。");
    }

    [TestMethod]
    public void FilePatch_PatchTextOnly_OmitsPath()
    {
        var wire = Projector.Project("file_patch", """{"patch_text":"--- a/x\n+++ b/x\n@@ -1 +1 @@\n-a\n+b\n"}""", null);

        Assert.AreEqual("diff", WireKind(wire));
        Assert.AreEqual("""{"kind":"diff"}""", wire.GetRawText(),
            "patch_text 形态的参数里没有路径事实（路径在 diff 头里）⇒ 不得猜，省略 meta。");
    }

    [TestMethod]
    public void SearchGrep_ReportsQueryFacts()
    {
        var wire = Projector.Project(
            "search_grep",
            """{"query":"ToolPresentationIntent","pattern":"*.cs","directory":"Source/PuddingRuntime"}""",
            null);

        Assert.AreEqual("search", WireKind(wire));
        Assert.AreEqual("ToolPresentationIntent", MetaString(wire, "query"));
        Assert.AreEqual("*.cs", MetaString(wire, "pattern"));
        Assert.AreEqual("Source/PuddingRuntime", MetaString(wire, "directory"));
        Assert.IsNull(
            MetaValue(wire, "count"),
            "search_grep 输出是自由文本且带截断语义，没有结构化命中数事实 ⇒ 不得写计数（渲染器自行回落）。");
    }

    [TestMethod]
    public void SearchGrep_ResultPhase_DoesNotInventCounts()
    {
        var wire = Projector.Project(
            "search_grep",
            """{"query":"Tool","pattern":"*.cs","directory":"Source"}""",
            "Source/A.cs:12: Tool\nSource/B.cs:3: Tool\n(coverage: partial — scanned 10/2000 files)");

        Assert.AreEqual("search", WireKind(wire));
        Assert.IsNull(MetaValue(wire, "count"));
        Assert.IsNull(MetaValue(wire, "total"), "结果不是 JSON 时不得从自由文本推计数。");
    }

    [TestMethod]
    public void UnknownTool_FallsBackToGeneric()
    {
        var wire = Projector.Project("list_dir", """{"path":"."}""", null);

        Assert.AreEqual("generic", WireKind(wire));
        Assert.AreEqual("""{"kind":"generic"}""", wire.GetRawText());
    }

    [TestMethod]
    public void PresenterReturningNull_FallsBackToGeneric()
    {
        var projector = new ToolPresentationProjector(_ => _ => null);

        var wire = projector.Project("terminal_start", """{"command":"dotnet build"}""", null);

        Assert.AreEqual("generic", WireKind(wire));
        Assert.AreEqual("""{"kind":"generic"}""", wire.GetRawText());
    }

    [TestMethod]
    public void PresenterThrowing_FallsBackToGeneric_AndNeverThrows()
    {
        var projector = new ToolPresentationProjector(
            _ => _ => throw new InvalidOperationException("presenter blew up"));

        var wire = projector.Project("terminal_start", """{"command":"dotnet build"}""", null);

        Assert.AreEqual("generic", WireKind(wire), "presenter 异常必须降级 generic（fail-open）。");
        Assert.AreEqual("""{"kind":"generic"}""", wire.GetRawText());
    }

    [TestMethod]
    public void MalformedArgumentsJson_StillProjectsToolOwnedKind()
    {
        var wire = Projector.Project("terminal_start", "{not json", null);

        Assert.AreEqual("terminal", WireKind(wire), "参数不可解析不得丢掉工具自有 kind（仅丢 meta 事实）。");
        Assert.AreEqual("""{"kind":"terminal"}""", wire.GetRawText());
    }

    [TestMethod]
    public void BlankToolId_FallsBackToGeneric()
    {
        Assert.AreEqual("""{"kind":"generic"}""", Projector.Project(null, "{}", null).GetRawText());
        Assert.AreEqual("""{"kind":"generic"}""", Projector.Project("   ", "{}", null).GetRawText());
    }

    [TestMethod]
    public void WireKindVocabulary_MatchesFrontendEightKinds()
    {
        var expected = new Dictionary<ToolPresentationIntentKind, string>
        {
            [ToolPresentationIntentKind.Generic] = "generic",
            [ToolPresentationIntentKind.Terminal] = "terminal",
            [ToolPresentationIntentKind.Diff] = "diff",
            [ToolPresentationIntentKind.Search] = "search",
            [ToolPresentationIntentKind.Read] = "read",
            [ToolPresentationIntentKind.Web] = "web",
            [ToolPresentationIntentKind.Delegation] = "delegation",
            [ToolPresentationIntentKind.Job] = "job",
        };

        foreach (var (kind, wire) in expected)
            Assert.AreEqual(wire, ToolPresentationProjector.ToWireKind(kind), kind.ToString());

        Assert.AreEqual(
            "generic",
            ToolPresentationProjector.ToWireKind((ToolPresentationIntentKind)99),
            "未知枚举值必须降级 generic，绝不外泄 .NET 枚举名。");
    }

    private static string WireKind(JsonElement wire)
    {
        Assert.AreEqual(JsonValueKind.Object, wire.ValueKind);
        return wire.GetProperty("kind").GetString()!;
    }

    private static string? MetaString(JsonElement wire, string key)
        => MetaValue(wire, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static int? MetaInt(JsonElement wire, string key)
        => MetaValue(wire, key) is { ValueKind: JsonValueKind.Number } value ? value.GetInt32() : null;

    private static JsonElement? MetaValue(JsonElement wire, string key)
        => wire.TryGetProperty("meta", out var meta)
           && meta.ValueKind == JsonValueKind.Object
           && meta.TryGetProperty(key, out var value)
            ? value
            : null;
}
