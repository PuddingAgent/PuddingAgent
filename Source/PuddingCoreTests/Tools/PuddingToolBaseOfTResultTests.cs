using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Tools;
using PuddingCode.Tools.Definitions;

namespace PuddingCoreTests.Tools;

/// <summary>
/// P0-B-4：双泛型强类型结果基类 PuddingToolBase&lt;TArgs, TResult&gt; 单测。
/// 覆盖：snake_case 参数反序列化归一化、TResult 被 materialize 为 canonical JSON、
/// JsonException→Fail、业务异常→Fail、OperationCanceledException 透传、
/// Definition 为 null 时直接 materialize 序列化，以及 Definition 非 null 时的 render / output schema 校验。
/// </summary>
[TestClass]
public sealed class PuddingToolBaseOfTResultTests
{
    private static ToolExecutionRequest Request(string argumentsJson) => new()
    {
        ToolCallId = "call-1",
        ArgumentsJson = argumentsJson,
        Context = new ToolExecutionContext
        {
            WorkspaceId = "ws-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-1",
        },
    };

    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // ── snake_case 参数反序列化归一化 ──────────────────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_Deserializes_SnakeCase_Arguments_Into_PascalCase_Properties()
    {
        SampleArgs? captured = null;
        var tool = new SampleTool(args =>
        {
            captured = args;
            return Task.FromResult(new SampleResult { Echo = "ok" });
        });

        var result = await tool.ExecuteAsync(Request("{\"query\":\"hello world\",\"max_results\":5}"));

        Assert.IsTrue(result.Success);
        Assert.AreEqual("hello world", captured!.Query);
        Assert.AreEqual(5, captured!.MaxResults);
    }

    // ── TResult 被 materialize 为 canonical JSON ──────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_Materializes_TResult_Into_Canonical_Json_String()
    {
        var tool = new SampleTool(_ => Task.FromResult(new SampleResult { Echo = "world", MaxResults = 7 }));

        var result = await tool.ExecuteAsync(Request("{\"query\":\"hi\"}"));

        Assert.IsTrue(result.Success);
        var root = Json(result.Output);
        Assert.AreEqual(JsonValueKind.Object, root.ValueKind);
        Assert.AreEqual("world", root.GetProperty("echo").GetString());
        Assert.AreEqual(7, root.GetProperty("max_results").GetInt32());
    }

    // ── JsonException → Fail ──────────────────────────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_Invalid_Arguments_Json_Returns_Fail()
    {
        var tool = new SampleTool(_ => Task.FromResult(new SampleResult { Echo = "ok" }));

        var result = await tool.ExecuteAsync(Request("{\"query\": "));

        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.Error);
        StringAssert.Contains(result.Error!, "Tool arguments must be valid JSON object");
    }

    // ── 业务异常 → Fail ───────────────────────────────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_Business_Exception_Returns_Fail()
    {
        var tool = new SampleTool(_ => throw new InvalidOperationException("boom"));

        var result = await tool.ExecuteAsync(Request("{\"query\":\"hi\"}"));

        Assert.IsFalse(result.Success);
        Assert.AreEqual("boom", result.Error);
    }

    // ── OperationCanceledException 透传 ───────────────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_OperationCanceledException_Is_Rethrown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var tool = new SampleTool(_ => throw new OperationCanceledException(cts.Token));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => tool.ExecuteAsync(Request("{\"query\":\"hi\"}"), cts.Token));
    }

    // ── Definition 为 null 时直接 materialize 序列化 ───────────────────────

    [TestMethod]
    public async Task ExecuteAsync_Definition_Null_Materializes_Result_Directly()
    {
        // Definition 默认 null：跳过 output schema 校验与 render，直接输出 canonical JSON。
        var tool = new SampleTool(_ => Task.FromResult(new SampleResult { Echo = "raw", MaxResults = null }));

        var result = await tool.ExecuteAsync(Request("{\"query\":\"x\"}"));

        Assert.IsTrue(result.Success);
        Assert.AreEqual("{\"echo\":\"raw\",\"max_results\":null}", result.Output);
    }

    // ── Definition 非 null：render 产出模型可见内容 ─────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_Definition_Render_Is_Applied_To_Canonical_Result()
    {
        var tool = new DefinedTool(new ToolDefinition
        {
            Id = "defined_tool",
            Description = "defined tool",
            InputSchema = JsonSchema.Object(),
            Output = new ToolOutputDefinition
            {
                Schema = JsonSchema.Object(),
                Render = (_, result) => ToolContent.FromText("rendered: " + result.GetProperty("echo").GetString()),
            },
        });

        var result = await tool.ExecuteAsync(Request("{\"query\":\"hi\"}"));

        Assert.IsTrue(result.Success);
        Assert.AreEqual("rendered: hello", result.Output);
    }

    // ── Definition 非 null：output schema 校验失败 → Fail ──────────────────

    [TestMethod]
    public async Task ExecuteAsync_Definition_Output_Schema_Violation_Returns_Fail()
    {
        var tool = new DefinedTool(new ToolDefinition
        {
            Id = "defined_tool",
            Description = "defined tool",
            InputSchema = JsonSchema.Object(),
            Output = new ToolOutputDefinition
            {
                Schema = JsonSchema.Object(required: new[] { "missing_field" }),
                Render = (_, result) => ToolContent.FromText(result.GetRawText()),
            },
        });

        var result = await tool.ExecuteAsync(Request("{\"query\":\"hi\"}"));

        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.Error);
        StringAssert.Contains(result.Error!, "Tool output invalid: ");
        StringAssert.Contains(result.Error!, "$.missing_field: required property is missing");
    }

    // ── 测试工具与类型 ────────────────────────────────────────────────────

    [Tool(id: "sample_tool", name: "Sample tool", description: "Sample tool for tests", category: ToolCategory.Query)]
    private sealed class SampleTool : PuddingToolBase<SampleArgs, SampleResult>
    {
        private readonly Func<SampleArgs, Task<SampleResult>> _handler;

        public SampleTool(Func<SampleArgs, Task<SampleResult>> handler) => _handler = handler;

        protected override Task<SampleResult> ExecuteCoreAsync(
            SampleArgs args, ToolExecutionContext context, CancellationToken ct)
            => _handler(args);
    }

    [Tool(id: "defined_tool", name: "Defined tool", description: "Defined tool for tests", category: ToolCategory.Query)]
    private sealed class DefinedTool : PuddingToolBase<SampleArgs, SampleResult>
    {
        private readonly ToolDefinition? _definition;

        public DefinedTool(ToolDefinition? definition) => _definition = definition;

        protected override ToolDefinition? Definition => _definition;

        protected override Task<SampleResult> ExecuteCoreAsync(
            SampleArgs args, ToolExecutionContext context, CancellationToken ct)
            => Task.FromResult(new SampleResult { Echo = "hello", MaxResults = 1 });
    }

    private sealed record SampleArgs
    {
        [ToolParam("The query text.")]
        public required string Query { get; init; }

        [ToolParam("Optional maximum result count.")]
        public int? MaxResults { get; init; }
    }

    private sealed record SampleResult
    {
        [JsonPropertyName("echo")]
        public required string Echo { get; init; }

        [JsonPropertyName("max_results")]
        public int? MaxResults { get; init; }
    }
}
