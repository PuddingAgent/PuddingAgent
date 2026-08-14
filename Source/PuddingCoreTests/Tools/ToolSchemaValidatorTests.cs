using System.Text.Json;
using PuddingCode.Tools.Definitions;

namespace PuddingCoreTests.Tools;

/// <summary>
/// T05 P0-B-3：input/output schema validator 单测。
/// 契约来源：Docs/deepseek-harness-tool-system-alignment-2026-08-14.md §6 约束 3。
/// 覆盖：通过/失败正反例（required 缺失、类型错误、enum 不匹配、嵌套属性路径错误、output schema 校验失败）、
/// IsValidInput/IsValidOutput、null 防御、schema 缺失时的明确错误。
/// </summary>
[TestClass]
public sealed class ToolSchemaValidatorTests
{
    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static ToolDefinition Build(JsonSchema? inputSchema = null, JsonSchema? outputSchema = null) => new()
    {
        Id = "test",
        Description = "test tool",
        InputSchema = inputSchema ?? JsonSchema.Object(),
        Output = new ToolOutputDefinition
        {
            Schema = outputSchema ?? JsonSchema.Object(),
            Render = (_, result) => ToolContent.FromCanonical(result),
        },
    };

    // ── 通过：空列表 ───────────────────────────────────────────────────────

    [TestMethod]
    public void ValidateInput_Returns_Empty_For_Valid_Arguments()
    {
        var input = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema> { ["q"] = JsonSchema.String() },
            required: new[] { "q" });

        var errors = ToolSchemaValidator.ValidateInput(Build(input), Json("{\"q\":\"hi\"}"));

        Assert.IsEmpty(errors);
    }

    [TestMethod]
    public void ValidateOutput_Returns_Empty_For_Valid_Result()
    {
        var output = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema> { ["ok"] = JsonSchema.Boolean() },
            required: new[] { "ok" });

        var errors = ToolSchemaValidator.ValidateOutput(Build(outputSchema: output), Json("{\"ok\":true}"));

        Assert.IsEmpty(errors);
    }

    // ── 失败：required 缺失 ────────────────────────────────────────────────

    [TestMethod]
    public void ValidateInput_Reports_Missing_Required_Property()
    {
        var input = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema> { ["q"] = JsonSchema.String() },
            required: new[] { "q" });

        var errors = ToolSchemaValidator.ValidateInput(Build(input), Json("{}"));

        CollectionAssert.AreEqual(new[] { "$.q: required property is missing" }, errors.ToArray());
    }

    // ── 失败：类型错误 ─────────────────────────────────────────────────────

    [TestMethod]
    public void ValidateInput_Reports_Type_Mismatch()
    {
        var input = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema> { ["count"] = JsonSchema.Integer() });

        var errors = ToolSchemaValidator.ValidateInput(Build(input), Json("{\"count\":\"abc\"}"));

        CollectionAssert.AreEqual(new[] { "$.count: expected integer, got string" }, errors.ToArray());
    }

    // ── 失败：enum 不匹配 ──────────────────────────────────────────────────

    [TestMethod]
    public void ValidateInput_Reports_Enum_Mismatch()
    {
        var input = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema>
            {
                ["mode"] = JsonSchema.String(@enum: new[] { Json("\"a\""), Json("\"b\"") }),
            });

        var errors = ToolSchemaValidator.ValidateInput(Build(input), Json("{\"mode\":\"c\"}"));

        CollectionAssert.AreEqual(new[] { "$.mode: value is not one of the allowed enum values" }, errors.ToArray());
    }

    // ── 失败：嵌套属性路径错误 ─────────────────────────────────────────────

    [TestMethod]
    public void ValidateInput_Reports_Nested_Property_Path_Error()
    {
        var input = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema>
            {
                ["a"] = JsonSchema.Object(
                    properties: new Dictionary<string, JsonSchema> { ["b"] = JsonSchema.Integer() }),
            });

        var errors = ToolSchemaValidator.ValidateInput(Build(input), Json("{\"a\":{\"b\":\"x\"}}"));

        CollectionAssert.AreEqual(new[] { "$.a.b: expected integer, got string" }, errors.ToArray());
    }

    // ── output schema 校验失败 ─────────────────────────────────────────────

    [TestMethod]
    public void ValidateOutput_Reports_Missing_Required_Property()
    {
        var output = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema> { ["ok"] = JsonSchema.Boolean() },
            required: new[] { "ok" });

        var errors = ToolSchemaValidator.ValidateOutput(Build(outputSchema: output), Json("{}"));

        CollectionAssert.AreEqual(new[] { "$.ok: required property is missing" }, errors.ToArray());
    }

    [TestMethod]
    public void ValidateOutput_Reports_Type_Mismatch()
    {
        var output = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema> { ["ok"] = JsonSchema.Boolean() });

        var errors = ToolSchemaValidator.ValidateOutput(Build(outputSchema: output), Json("{\"ok\":1}"));

        CollectionAssert.AreEqual(new[] { "$.ok: expected boolean, got number" }, errors.ToArray());
    }

    // ── IsValidInput / IsValidOutput ──────────────────────────────────────

    [TestMethod]
    public void IsValidInput_And_IsValidOutput_Reflect_Validation()
    {
        var input = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema> { ["q"] = JsonSchema.String() },
            required: new[] { "q" });
        var output = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema> { ["ok"] = JsonSchema.Boolean() },
            required: new[] { "ok" });
        var def = Build(input, output);

        Assert.IsTrue(ToolSchemaValidator.IsValidInput(def, Json("{\"q\":\"hi\"}")));
        Assert.IsFalse(ToolSchemaValidator.IsValidInput(def, Json("{}")));

        Assert.IsTrue(ToolSchemaValidator.IsValidOutput(def, Json("{\"ok\":true}")));
        Assert.IsFalse(ToolSchemaValidator.IsValidOutput(def, Json("{}")));
    }

    // ── null 防御 ─────────────────────────────────────────────────────────

    [TestMethod]
    public void ValidateInput_Throws_On_Null_Definition()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => ToolSchemaValidator.ValidateInput(null!, Json("{}")));
    }

    [TestMethod]
    public void ValidateOutput_Throws_On_Null_Definition()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => ToolSchemaValidator.ValidateOutput(null!, Json("{}")));
    }

    [TestMethod]
    public void IsValidInput_Throws_On_Null_Definition()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => ToolSchemaValidator.IsValidInput(null!, Json("{}")));
    }

    [TestMethod]
    public void IsValidOutput_Throws_On_Null_Definition()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => ToolSchemaValidator.IsValidOutput(null!, Json("{}")));
    }

    // ── schema 缺失：返回明确错误，不抛 NullReferenceException ────────────────

    [TestMethod]
    public void ValidateInput_Reports_Missing_Input_Schema()
    {
        var def = new ToolDefinition
        {
            Id = "test",
            Description = "test tool",
            InputSchema = null!,
            Output = new ToolOutputDefinition
            {
                Schema = JsonSchema.Object(),
                Render = (_, result) => ToolContent.FromCanonical(result),
            },
        };

        var errors = ToolSchemaValidator.ValidateInput(def, Json("{}"));

        CollectionAssert.AreEqual(new[] { "$.input_schema: required property is missing" }, errors.ToArray());
    }

    [TestMethod]
    public void ValidateOutput_Reports_Missing_Output_Schema()
    {
        var def = new ToolDefinition
        {
            Id = "test",
            Description = "test tool",
            InputSchema = JsonSchema.Object(),
            Output = new ToolOutputDefinition
            {
                Schema = null!,
                Render = (_, result) => ToolContent.FromCanonical(result),
            },
        };

        var errors = ToolSchemaValidator.ValidateOutput(def, Json("{}"));

        CollectionAssert.AreEqual(new[] { "$.output.schema: required property is missing" }, errors.ToArray());
    }
}
