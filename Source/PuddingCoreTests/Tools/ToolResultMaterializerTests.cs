using System.Text.Json;
using PuddingCode.Tools.Definitions;

namespace PuddingCoreTests.Tools;

/// <summary>
/// T05 P0-B-3：lossless JSON materializer 单测。
/// 契约来源：Docs/deepseek-harness-tool-system-alignment-2026-08-14.md §6 约束 3。
/// 覆盖：JsonElement 透传、null→Null、string 合法 JSON（结构化保留）、string 非法 JSON（包装为 string 值不丢失原文）、
/// CLR 匿名对象/Dictionary/List 序列化、嵌套对象、数字精度（大整数/小数不丢精度）。
/// </summary>
[TestClass]
public sealed class ToolResultMaterializerTests
{
    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // ── 输入已是 JsonElement：直接透传 ─────────────────────────────────────

    [TestMethod]
    public void Materialize_JsonElement_Is_Passed_Through_Unchanged()
    {
        var source = Json("{\"a\":1,\"b\":[true,null]}");

        var result = ToolResultMaterializer.Materialize(source);

        Assert.AreEqual(JsonValueKind.Object, result.ValueKind);
        Assert.IsTrue(JsonElement.DeepEquals(source, result));
    }

    [TestMethod]
    public void Materialize_JsonElement_Array_Is_Passed_Through()
    {
        var source = Json("[1,2,3]");

        var result = ToolResultMaterializer.Materialize(source);

        Assert.AreEqual(JsonValueKind.Array, result.ValueKind);
        Assert.AreEqual(3, result.GetArrayLength());
    }

    // ── null → Null ───────────────────────────────────────────────────────

    [TestMethod]
    public void Materialize_Null_Returns_JsonNull()
    {
        var result = ToolResultMaterializer.Materialize(null);

        Assert.AreEqual(JsonValueKind.Null, result.ValueKind);
    }

    // ── string：合法 JSON 结构化保留 ───────────────────────────────────────

    [TestMethod]
    public void Materialize_String_Valid_Object_Json_Is_Structured()
    {
        var result = ToolResultMaterializer.Materialize("{\"name\":\"pudding\",\"count\":3}");

        Assert.AreEqual(JsonValueKind.Object, result.ValueKind);
        Assert.AreEqual("pudding", result.GetProperty("name").GetString());
        Assert.AreEqual(3, result.GetProperty("count").GetInt32());
    }

    [TestMethod]
    public void Materialize_String_Valid_Array_Json_Is_Structured()
    {
        var result = ToolResultMaterializer.Materialize("[1,2,3]");

        Assert.AreEqual(JsonValueKind.Array, result.ValueKind);
        Assert.AreEqual(3, result.GetArrayLength());
    }

    [TestMethod]
    public void Materialize_String_Valid_Scalar_Json_Is_Structured()
    {
        var number = ToolResultMaterializer.Materialize("123");
        Assert.AreEqual(JsonValueKind.Number, number.ValueKind);
        Assert.AreEqual(123, number.GetInt32());

        var boolean = ToolResultMaterializer.Materialize("true");
        Assert.AreEqual(JsonValueKind.True, boolean.ValueKind);
    }

    [TestMethod]
    public void Materialize_String_With_Surrounding_Whitespace_Is_Structured()
    {
        var result = ToolResultMaterializer.Materialize("   {\"a\":1}   ");

        Assert.AreEqual(JsonValueKind.Object, result.ValueKind);
        Assert.AreEqual(1, result.GetProperty("a").GetInt32());
    }

    // ── string：非法 JSON 包装为 string 值，不丢失原文 ───────────────────────

    [TestMethod]
    public void Materialize_String_Invalid_Json_Is_Wrapped_As_String()
    {
        var result = ToolResultMaterializer.Materialize("hello world");

        Assert.AreEqual(JsonValueKind.String, result.ValueKind);
        Assert.AreEqual("hello world", result.GetString());
    }

    [TestMethod]
    public void Materialize_String_Invalid_Json_Preserves_Original_Whitespace()
    {
        var original = "  hello  world  ";

        var result = ToolResultMaterializer.Materialize(original);

        Assert.AreEqual(JsonValueKind.String, result.ValueKind);
        Assert.AreEqual(original, result.GetString());
    }

    [TestMethod]
    public void Materialize_String_Empty_Is_Wrapped_As_Empty_String()
    {
        var result = ToolResultMaterializer.Materialize(string.Empty);

        Assert.AreEqual(JsonValueKind.String, result.ValueKind);
        Assert.AreEqual(string.Empty, result.GetString());
    }

    // ── CLR object：序列化 ────────────────────────────────────────────────

    [TestMethod]
    public void Materialize_Anonymous_Object_Is_Serialized()
    {
        var result = ToolResultMaterializer.Materialize(new { name = "pudding", enabled = true });

        Assert.AreEqual(JsonValueKind.Object, result.ValueKind);
        Assert.AreEqual("pudding", result.GetProperty("name").GetString());
        Assert.IsTrue(result.GetProperty("enabled").GetBoolean());
    }

    [TestMethod]
    public void Materialize_Dictionary_Is_Serialized()
    {
        var dict = new Dictionary<string, object?>
        {
            ["id"] = 42,
            ["label"] = "ok",
        };

        var result = ToolResultMaterializer.Materialize(dict);

        Assert.AreEqual(JsonValueKind.Object, result.ValueKind);
        Assert.AreEqual(42, result.GetProperty("id").GetInt32());
        Assert.AreEqual("ok", result.GetProperty("label").GetString());
    }

    [TestMethod]
    public void Materialize_List_Is_Serialized()
    {
        var list = new List<int> { 1, 2, 3 };

        var result = ToolResultMaterializer.Materialize(list);

        Assert.AreEqual(JsonValueKind.Array, result.ValueKind);
        Assert.AreEqual(3, result.GetArrayLength());
        Assert.AreEqual(2, result[1].GetInt32());
    }

    [TestMethod]
    public void Materialize_Nested_Object_Is_Serialized_Recursively()
    {
        var result = ToolResultMaterializer.Materialize(new
        {
            user = new { name = "pudding", tags = new[] { "a", "b" } },
            meta = new Dictionary<string, object?> { ["depth"] = 1 },
        });

        Assert.AreEqual(JsonValueKind.Object, result.ValueKind);
        Assert.AreEqual("pudding", result.GetProperty("user").GetProperty("name").GetString());
        Assert.AreEqual("b", result.GetProperty("user").GetProperty("tags")[1].GetString());
        Assert.AreEqual(1, result.GetProperty("meta").GetProperty("depth").GetInt32());
    }

    // ── 数字精度：不丢精度 ────────────────────────────────────────────────

    [TestMethod]
    public void Materialize_Preserves_Large_Integer_Precision()
    {
        const long big = 9223372036854775807L; // long.MaxValue

        var result = ToolResultMaterializer.Materialize(big);

        Assert.AreEqual(JsonValueKind.Number, result.ValueKind);
        Assert.AreEqual(big, result.GetInt64());
        Assert.AreEqual("9223372036854775807", result.GetRawText());
    }

    [TestMethod]
    public void Materialize_Preserves_Decimal_Precision()
    {
        const decimal precise = 123456789.123456789m;

        var result = ToolResultMaterializer.Materialize(precise);

        Assert.AreEqual(JsonValueKind.Number, result.ValueKind);
        Assert.AreEqual(precise, result.GetDecimal());
    }

    [TestMethod]
    public void Materialize_Preserves_Boolean_And_Double()
    {
        Assert.AreEqual(JsonValueKind.True, ToolResultMaterializer.Materialize(true).ValueKind);
        Assert.AreEqual(JsonValueKind.False, ToolResultMaterializer.Materialize(false).ValueKind);

        var dbl = ToolResultMaterializer.Materialize(3.5);
        Assert.AreEqual(JsonValueKind.Number, dbl.ValueKind);
        Assert.AreEqual(3.5, dbl.GetDouble());
    }
}
