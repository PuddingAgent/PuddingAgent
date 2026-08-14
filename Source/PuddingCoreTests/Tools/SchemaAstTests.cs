using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Tools.Definitions;

namespace PuddingCoreTests.Tools;

/// <summary>
/// T05 P0-B-1：Core 不可变 Schema AST 节点契约单测。
/// 契约来源：Docs/deepseek-harness-tool-system-alignment-2026-08-14.md §6.1（Schema 能力清单）。
/// 覆盖：各类型节点构造、ToolParameterSchema↔JsonSchema 兼容转换、基础验证（类型/required/enum/pattern/min-max）、
/// 以及 <c>$.field: ...</c> 路径错误格式。
/// </summary>
[TestClass]
public sealed class SchemaAstTests
{
    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // ── 节点构造 ──────────────────────────────────────────────────────────

    [TestMethod]
    public void String_Node_Constructs_With_Constraints()
    {
        var schema = JsonSchema.String(
            description: "a name",
            minLength: 1,
            maxLength: 10,
            pattern: "^[a-z]+$");

        Assert.AreEqual(JsonSchemaType.String, schema.Type);
        Assert.AreEqual("a name", schema.Description);
        Assert.AreEqual(1, schema.MinLength);
        Assert.AreEqual(10, schema.MaxLength);
        Assert.AreEqual("^[a-z]+$", schema.Pattern);
    }

    [TestMethod]
    public void Integer_Node_Constructs_With_Range()
    {
        var schema = JsonSchema.Integer(minimum: 0, maximum: 600);

        Assert.AreEqual(JsonSchemaType.Integer, schema.Type);
        Assert.AreEqual(0d, schema.Minimum);
        Assert.AreEqual(600d, schema.Maximum);
    }

    [TestMethod]
    public void Number_Node_Constructs()
    {
        var schema = JsonSchema.Number(minimum: 0.5, maximum: 10.5);

        Assert.AreEqual(JsonSchemaType.Number, schema.Type);
        Assert.AreEqual(0.5, schema.Minimum);
        Assert.AreEqual(10.5, schema.Maximum);
    }

    [TestMethod]
    public void Boolean_Node_Constructs()
    {
        var schema = JsonSchema.Boolean();

        Assert.AreEqual(JsonSchemaType.Boolean, schema.Type);
    }

    [TestMethod]
    public void Array_Node_Constructs_With_Nested_Items()
    {
        var schema = JsonSchema.Array(JsonSchema.String());

        Assert.AreEqual(JsonSchemaType.Array, schema.Type);
        Assert.IsNotNull(schema.Items);
        Assert.AreEqual(JsonSchemaType.String, schema.Items!.Type);
    }

    [TestMethod]
    public void Object_Node_Constructs_With_Properties_And_Required()
    {
        var schema = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema>
            {
                ["name"] = JsonSchema.String(),
                ["count"] = JsonSchema.Integer(),
            },
            required: new[] { "name" });

        Assert.AreEqual(JsonSchemaType.Object, schema.Type);
        Assert.AreEqual(2, schema.Properties!.Count);
        Assert.AreEqual(JsonSchemaType.String, schema.Properties["name"].Type);
        Assert.AreEqual(1, schema.Required!.Count);
        Assert.AreEqual("name", schema.Required[0]);
    }

    [TestMethod]
    public void Enum_And_Const_Are_Stored_On_Node()
    {
        var schema = JsonSchema.String(
            @enum: new[] { Json("\"a\""), Json("\"b\"") },
            @const: Json("\"a\""));

        Assert.AreEqual(2, schema.Enum!.Count);
        Assert.AreEqual("a", schema.Const!.Value.GetString());
    }

    // ── ToolParameterSchema ↔ JsonSchema 兼容转换 ──────────────────────────

    [TestMethod]
    public void FromToolParameterSchema_Converts_Properties_Required_And_Description()
    {
        var source = new ToolParameterSchema(
            Properties:
            [
                new ToolParameter("name", "string", "display name"),
                new ToolParameter("count", "integer", "repeat count"),
            ],
            Required: ["name"]);

        var schema = JsonSchema.FromToolParameterSchema(source);

        Assert.AreEqual(JsonSchemaType.Object, schema.Type);
        Assert.AreEqual(2, schema.Properties!.Count);

        var nameNode = schema.Properties["name"];
        Assert.AreEqual(JsonSchemaType.String, nameNode.Type);
        Assert.AreEqual("display name", nameNode.Description);

        var countNode = schema.Properties["count"];
        Assert.AreEqual(JsonSchemaType.Integer, countNode.Type);
        Assert.AreEqual("repeat count", countNode.Description);

        Assert.AreEqual(1, schema.Required!.Count);
        Assert.AreEqual("name", schema.Required[0]);
    }

    [TestMethod]
    public void FromToolParameterSchema_Maps_All_Type_Strings()
    {
        var source = new ToolParameterSchema(
            Properties:
            [
                new ToolParameter("s", "string", ""),
                new ToolParameter("i", "integer", ""),
                new ToolParameter("n", "number", ""),
                new ToolParameter("b", "boolean", ""),
                new ToolParameter("a", "array", ""),
                new ToolParameter("o", "object", ""),
            ],
            Required: []);

        var schema = JsonSchema.FromToolParameterSchema(source);

        Assert.AreEqual(JsonSchemaType.String, schema.Properties!["s"].Type);
        Assert.AreEqual(JsonSchemaType.Integer, schema.Properties!["i"].Type);
        Assert.AreEqual(JsonSchemaType.Number, schema.Properties!["n"].Type);
        Assert.AreEqual(JsonSchemaType.Boolean, schema.Properties!["b"].Type);
        Assert.AreEqual(JsonSchemaType.Array, schema.Properties!["a"].Type);
        Assert.AreEqual(JsonSchemaType.Object, schema.Properties!["o"].Type);
    }

    [TestMethod]
    public void FromToolParameterSchema_Unknown_Type_Falls_Back_To_Object()
    {
        var source = new ToolParameterSchema(
            Properties: [new ToolParameter("weird", "uuid", "unknown kind")],
            Required: []);

        var schema = JsonSchema.FromToolParameterSchema(source);

        Assert.AreEqual(JsonSchemaType.Object, schema.Properties!["weird"].Type);
    }

    [TestMethod]
    public void FromToolParameterSchema_Empty_Required_Becomes_Null()
    {
        var source = new ToolParameterSchema(
            Properties: [new ToolParameter("name", "string", "")],
            Required: []);

        var schema = JsonSchema.FromToolParameterSchema(source);

        Assert.IsNull(schema.Required);
    }

    // ── 基础验证 ──────────────────────────────────────────────────────────

    [TestMethod]
    public void Validate_TypeMismatch_Returns_Path_Error()
    {
        var schema = JsonSchema.Integer();

        var errors = schema.Validate(Json("\"abc\""), "$.count");

        CollectionAssert.AreEqual(
            new[] { "$.count: expected integer, got string" },
            errors.ToArray());
    }

    [TestMethod]
    public void Validate_RequiredMissing_Returns_Path_Error()
    {
        var schema = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema> { ["name"] = JsonSchema.String() },
            required: new[] { "name" });

        var errors = schema.Validate(Json("{}"));

        CollectionAssert.AreEqual(
            new[] { "$.name: required property is missing" },
            errors.ToArray());
    }

    [TestMethod]
    public void Validate_EnumOutOfRange_Returns_Error()
    {
        var schema = JsonSchema.String(@enum: new[] { Json("\"red\""), Json("\"green\"") });

        var errors = schema.Validate(Json("\"blue\""), "$.color");

        CollectionAssert.AreEqual(
            new[] { "$.color: value is not one of the allowed enum values" },
            errors.ToArray());
    }

    [TestMethod]
    public void Validate_PatternMismatch_Returns_Error()
    {
        var schema = JsonSchema.String(pattern: "^[a-z]+$");

        var errors = schema.Validate(Json("\"ABC123\""), "$.slug");

        CollectionAssert.AreEqual(
            new[] { "$.slug: value does not match pattern '^[a-z]+$'" },
            errors.ToArray());
    }

    [TestMethod]
    public void Validate_Minimum_Violation_Returns_Error()
    {
        var schema = JsonSchema.Integer(minimum: 5);

        var errors = schema.Validate(Json("3"), "$.retries");

        CollectionAssert.AreEqual(
            new[] { "$.retries: value must be >= 5" },
            errors.ToArray());
    }

    [TestMethod]
    public void Validate_Maximum_Violation_Returns_Error()
    {
        var schema = JsonSchema.Number(maximum: 10.5);

        var errors = schema.Validate(Json("11"), "$.timeout_seconds");

        CollectionAssert.AreEqual(
            new[] { "$.timeout_seconds: value must be <= 10.5" },
            errors.ToArray());
    }

    [TestMethod]
    public void Validate_Valid_Value_Returns_No_Errors()
    {
        var schema = JsonSchema.Integer(minimum: 1, maximum: 10);

        var errors = schema.Validate(Json("5"));

        Assert.AreEqual(0, errors.Count);
    }

    [TestMethod]
    public void Validate_Recurses_Into_Nested_Array_With_Index_Path()
    {
        var schema = JsonSchema.Array(JsonSchema.Integer(minimum: 0));

        var errors = schema.Validate(Json("[1, -2, 3]"), "$.values");

        CollectionAssert.AreEqual(
            new[] { "$.values[1]: value must be >= 0" },
            errors.ToArray());
    }

    [TestMethod]
    public void Validate_Recurses_Into_Nested_Object_With_Property_Path()
    {
        var schema = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema>
            {
                ["payload"] = JsonSchema.Object(
                    properties: new Dictionary<string, JsonSchema>
                    {
                        ["mode"] = JsonSchema.String(@enum: new[] { Json("\"fast\""), Json("\"slow\"") }),
                    }),
            });

        var errors = schema.Validate(Json("{\"payload\":{\"mode\":\"turbo\"}}"), "$");

        CollectionAssert.AreEqual(
            new[] { "$.payload.mode: value is not one of the allowed enum values" },
            errors.ToArray());
    }

    [TestMethod]
    public void Validate_AdditionalProperties_False_Rejects_Unexpected_Property()
    {
        var schema = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema> { ["name"] = JsonSchema.String() },
            additionalProperties: false);

        var errors = schema.Validate(Json("{\"name\":\"ok\",\"extra\":1}"), "$");

        CollectionAssert.AreEqual(
            new[] { "$.extra: unexpected property (additionalProperties is false)" },
            errors.ToArray());
    }

    [TestMethod]
    public void Validate_OneOf_Matches_Exactly_One_Branch()
    {
        var schema = new JsonSchema
        {
            OneOf = new JsonSchema[]
            {
                JsonSchema.String(),
                JsonSchema.Integer(),
            },
        };

        Assert.AreEqual(0, schema.Validate(Json("\"text\"")).Count);
        Assert.AreEqual(0, schema.Validate(Json("7")).Count);
        Assert.AreEqual(1, schema.Validate(Json("true"), "$.value").Count);
    }
}
