using System.Text.Json;
using PuddingCode.Runtime;

namespace PuddingCoreTests.Runtime;

/// <summary>
/// T00 最小子集：ToolCallId 值对象契约单测。
/// 契约来源：Docs/deepseek-harness-tool-system-alignment-2026-08-14.md B:227-254。
/// </summary>
[TestClass]
public sealed class ToolCallIdTests
{
    [TestMethod]
    public void NewToolCallId_Generates_NonEmpty_Unique_Ids()
    {
        var first = ToolCallId.NewToolCallId();
        var second = ToolCallId.NewToolCallId();

        Assert.IsFalse(first.IsEmpty);
        Assert.IsFalse(second.IsEmpty);
        Assert.AreNotEqual(first.Value, second.Value);
    }

    [TestMethod]
    public void NewToolCallId_Produces_32Char_LowerHex_Guid()
    {
        var id = ToolCallId.NewToolCallId();

        // Guid "N" 格式：32 位小写十六进制，无连字符。
        Assert.AreEqual(32, id.Value.Length);
        Assert.IsTrue(System.Guid.TryParseExact(id.Value, "N", out _));
    }

    [TestMethod]
    public void Parse_Returns_Value_For_NonEmpty_Input()
    {
        var id = ToolCallId.Parse("call-123");

        Assert.AreEqual("call-123", id.Value);
        Assert.AreEqual("call-123", id.ToString());
    }

    [TestMethod]
    public void Parse_Throws_For_Null_Empty_Or_Whitespace()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ToolCallId.Parse(null!));
        Assert.ThrowsExactly<ArgumentException>(() => ToolCallId.Parse(""));
        Assert.ThrowsExactly<ArgumentException>(() => ToolCallId.Parse("   "));
    }

    [TestMethod]
    public void TryParse_Returns_False_For_Null_Empty_Or_Whitespace()
    {
        Assert.IsFalse(ToolCallId.TryParse(null, out _));
        Assert.IsFalse(ToolCallId.TryParse("", out _));
        Assert.IsFalse(ToolCallId.TryParse("   ", out _));
    }

    [TestMethod]
    public void TryParse_Returns_True_For_NonEmpty_Input()
    {
        Assert.IsTrue(ToolCallId.TryParse("call-456", out var id));
        Assert.AreEqual("call-456", id.Value);
    }

    [TestMethod]
    public void Equal_ToolCallIds_Share_Same_Value()
    {
        var left = new ToolCallId("call-eq");
        var right = new ToolCallId("call-eq");

        Assert.AreEqual(left, right);
        Assert.IsTrue(left == right);
    }

    [TestMethod]
    public void Different_ToolCallIds_Are_Not_Equal()
    {
        var left = new ToolCallId("call-a");
        var right = new ToolCallId("call-b");

        Assert.AreNotEqual(left, right);
        Assert.IsFalse(left == right);
    }

    [TestMethod]
    public void Serializes_As_Bare_Json_String()
    {
        var id = new ToolCallId("call-json");

        var json = JsonSerializer.Serialize(id);

        Assert.AreEqual("\"call-json\"", json);
    }

    [TestMethod]
    public void Deserializes_From_Bare_Json_String()
    {
        var id = JsonSerializer.Deserialize<ToolCallId>("\"call-json\"");

        Assert.AreEqual("call-json", id.Value);
    }

    [TestMethod]
    public void RoundTrips_Inside_Object_Payload()
    {
        var payload = new { name = "search_tools", toolCallId = new ToolCallId("call-payload") };

        var json = JsonSerializer.Serialize(payload);
        var doc = JsonDocument.Parse(json);

        Assert.IsTrue(doc.RootElement.TryGetProperty("toolCallId", out var toolCallId));
        Assert.AreEqual("call-payload", toolCallId.GetString());
    }
}
