using System.Text.Json;
using PuddingCode.Tools.Definitions;

namespace PuddingCoreTests.Tools;

/// <summary>
/// T06 P0-B-2：工具定义合同 ToolDefinition 单测。
/// 契约来源：Docs/deepseek-harness-tool-system-alignment-2026-08-14.md §6 / §14。
/// 覆盖：ToolDefinition/ToolOutputDefinition 构造、权限事实默认值、renderer/meta 委托调用、
/// presentation 词汇表、Present 投影，以及 first-party 合同校验（Id/Description/input+output schema/render）。
/// </summary>
[TestClass]
public sealed class ToolDefinitionTests
{
    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static ToolOutputDefinition TextOutput(JsonSchema? schema = null) => new()
    {
        Schema = schema ?? JsonSchema.Object(),
        Render = (_, result) => ToolContent.FromText(result.GetRawText()),
    };

    private static ToolDefinition Build(
        string id = "search",
        string description = "search the index") => new()
    {
        Id = id,
        Description = description,
        InputSchema = JsonSchema.Object(),
        Output = TextOutput(),
    };

    // ── 构造 ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void ToolDefinition_Constructs_With_Required_Fields()
    {
        var input = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema> { ["q"] = JsonSchema.String() },
            required: new[] { "q" });

        var def = new ToolDefinition
        {
            Id = "search",
            Description = "search the index",
            InputSchema = input,
            Output = TextOutput(),
        };

        Assert.AreEqual("search", def.Id);
        Assert.AreEqual("search the index", def.Description);
        Assert.AreSame(input, def.InputSchema);
        Assert.IsNotNull(def.Output);
        Assert.IsNotNull(def.Output.Schema);
    }

    [TestMethod]
    public void ToolDefinition_Permission_Defaults_Are_Conservative()
    {
        var def = Build();

        Assert.AreEqual(ToolPermissionLevel.Medium, def.Permission.PermissionLevel);
        Assert.AreEqual(ToolSafetyFlags.None, def.Permission.Safety);
        Assert.AreEqual(SubAgentExposure.Default, def.Permission.SubAgentExposure);
        Assert.AreEqual(ToolCategory.General, def.Permission.Category);
        Assert.IsNull(def.DefaultTimeout);
        Assert.IsNull(def.IsConcurrencySafe);
        Assert.IsNull(def.Present);
    }

    [TestMethod]
    public void ToolDefinition_Carries_Host_Metadata_Timeout_And_Concurrency()
    {
        var def = new ToolDefinition
        {
            Id = "shell",
            Description = "run a command",
            InputSchema = JsonSchema.Object(),
            Output = TextOutput(),
            DefaultTimeout = TimeSpan.FromSeconds(30),
            IsConcurrencySafe = args => args.ValueKind == JsonValueKind.Object,
        };

        Assert.AreEqual(TimeSpan.FromSeconds(30), def.DefaultTimeout);
        Assert.IsNotNull(def.IsConcurrencySafe);
        Assert.IsTrue(def.IsConcurrencySafe!(Json("{}")));
        Assert.IsFalse(def.IsConcurrencySafe!(Json("[]")));
    }

    // ── ToolOutputDefinition ──────────────────────────────────────────────

    [TestMethod]
    public void ToolOutputDefinition_Render_Is_Invoked_With_Args_And_Result()
    {
        JsonElement? capturedArgs = null;
        JsonElement? capturedResult = null;

        var output = new ToolOutputDefinition
        {
            Schema = JsonSchema.Object(),
            Render = (args, result) =>
            {
                capturedArgs = args;
                capturedResult = result;
                return ToolContent.FromText("ok");
            },
        };

        var content = output.Render(Json("{\"q\":\"hi\"}"), Json("\"answer\""));

        Assert.AreEqual("ok", content.Text);
        Assert.AreEqual("hi", capturedArgs!.Value.GetProperty("q").GetString());
        Assert.AreEqual("answer", capturedResult!.Value.GetString());
    }

    [TestMethod]
    public void ToolOutputDefinition_BuildPresentationMeta_Is_Optional()
    {
        var withMeta = new ToolOutputDefinition
        {
            Schema = JsonSchema.Object(),
            Render = (_, _) => ToolContent.FromText(""),
            BuildPresentationMeta = (_, _) => Json("{\"kind\":\"terminal\"}"),
        };

        Assert.IsNotNull(withMeta.BuildPresentationMeta);
        var meta = withMeta.BuildPresentationMeta!(Json("{}"), Json("{}"));
        Assert.AreEqual("terminal", meta!.Value.GetProperty("kind").GetString());

        var withoutMeta = TextOutput();
        Assert.IsNull(withoutMeta.BuildPresentationMeta);
    }

    // ── ToolContent ───────────────────────────────────────────────────────

    [TestMethod]
    public void ToolContent_FromText_And_FromCanonical()
    {
        Assert.AreEqual("hello", ToolContent.FromText("hello").Text);
        Assert.IsNull(ToolContent.FromText("hello").Canonical);

        var canonical = ToolContent.FromCanonical(Json("{\"ok\":true}"));
        Assert.IsTrue(canonical.Canonical!.Value.GetProperty("ok").GetBoolean());
        Assert.IsNull(canonical.Text);
    }

    // ── presentation 词汇表与 Present ─────────────────────────────────────

    [TestMethod]
    public void Presentation_Intent_Kind_Covers_All_Vocabulary()
    {
        var values = Enum.GetValues<ToolPresentationIntentKind>();

        CollectionAssert.AreEqual(
            new[]
            {
                ToolPresentationIntentKind.Generic,
                ToolPresentationIntentKind.Terminal,
                ToolPresentationIntentKind.Diff,
                ToolPresentationIntentKind.Search,
                ToolPresentationIntentKind.Read,
                ToolPresentationIntentKind.Web,
                ToolPresentationIntentKind.Delegation,
                ToolPresentationIntentKind.Job,
            },
            values);
    }

    [TestMethod]
    public void ToolDefinition_Present_Returns_Intent_With_Meta()
    {
        var def = new ToolDefinition
        {
            Id = "shell",
            Description = "run a command",
            InputSchema = JsonSchema.Object(),
            Output = TextOutput(),
            Present = _ => new ToolPresentationIntent
            {
                Kind = ToolPresentationIntentKind.Terminal,
                Meta = Json("{\"jobId\":\"abc\"}"),
            },
        };

        var intent = def.Present!(new ToolPresentationInput
        {
            Arguments = Json("{\"command\":\"ls\"}"),
        });

        Assert.IsNotNull(intent);
        Assert.AreEqual(ToolPresentationIntentKind.Terminal, intent.Kind);
        Assert.AreEqual("abc", intent.Meta!.Value.GetProperty("jobId").GetString());
    }

    // ── first-party 合同校验 ──────────────────────────────────────────────

    [TestMethod]
    public void Validate_Rejects_Missing_Id_And_Description()
    {
        var def = Build(id: " ", description: "");

        var errors = def.Validate();

        CollectionAssert.AreEqual(
            new[]
            {
                "$.id: required property is missing",
                "$.description: required property is missing",
            },
            errors.ToArray());
    }

    [TestMethod]
    public void Validate_Rejects_Missing_Input_Schema()
    {
        var def = new ToolDefinition
        {
            Id = "search",
            Description = "search",
            InputSchema = null!,
            Output = TextOutput(),
        };

        var errors = def.Validate();

        CollectionAssert.AreEqual(new[] { "$.input_schema: required property is missing" }, errors.ToArray());
    }

    [TestMethod]
    public void Validate_Rejects_Missing_Output()
    {
        var def = new ToolDefinition
        {
            Id = "search",
            Description = "search",
            InputSchema = JsonSchema.Object(),
            Output = null!,
        };

        var errors = def.Validate();

        CollectionAssert.AreEqual(new[] { "$.output: required property is missing" }, errors.ToArray());
    }

    [TestMethod]
    public void Validate_Rejects_Missing_Output_Schema_Or_Render()
    {
        var def = new ToolDefinition
        {
            Id = "search",
            Description = "search",
            InputSchema = JsonSchema.Object(),
            Output = new ToolOutputDefinition
            {
                Schema = null!,
                Render = null!,
            },
        };

        var errors = def.Validate();

        CollectionAssert.AreEqual(
            new[]
            {
                "$.output.schema: required property is missing",
                "$.output.render: required property is missing",
            },
            errors.ToArray());
    }

    [TestMethod]
    public void Validate_Passes_For_Complete_Definition()
    {
        var def = Build();

        Assert.AreEqual(0, def.Validate().Count);
        Assert.IsTrue(def.IsValid);
    }
}
