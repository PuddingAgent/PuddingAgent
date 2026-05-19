using System.Text.Json;
using PuddingCode.Serialization;

namespace PuddingCoreTests.Serialization;

[TestClass]
public class PuddingJsonContractsTests
{
    private record TestRecord(string Name, int Value);

    [TestMethod]
    public void JsonLines_Does_Not_Write_Indented_Multiline()
    {
        var obj = new TestRecord("hello", 42);
        var json = JsonSerializer.Serialize(obj, PuddingJsonContracts.JsonLines);

        // 断言：结果中不含换行符（单行 JSON）
        Assert.IsFalse(json.Contains('\n'), $"Expected single-line JSON but got: {json}");
    }

    [TestMethod]
    public void PrettyJson_Writes_Indented()
    {
        var obj = new TestRecord("hello", 42);
        var json = JsonSerializer.Serialize(obj, PuddingJsonContracts.PrettyJson);

        // 断言：结果包含换行缩进
        Assert.IsTrue(json.Contains('\n'), $"Expected indented JSON but got single-line: {json}");
    }

    [TestMethod]
    public void JsonLines_Deserialization_Roundtrip()
    {
        var original = new TestRecord("roundtrip-test", 99);
        var json = JsonSerializer.Serialize(original, PuddingJsonContracts.JsonLines);
        var deserialized = JsonSerializer.Deserialize<TestRecord>(json, PuddingJsonContracts.JsonLines);

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(original.Name, deserialized.Name);
        Assert.AreEqual(original.Value, deserialized.Value);
    }

    [TestMethod]
    public void PrettyJson_Deserialization_Roundtrip()
    {
        var original = new TestRecord("pretty-test", 77);
        var json = JsonSerializer.Serialize(original, PuddingJsonContracts.PrettyJson);
        var deserialized = JsonSerializer.Deserialize<TestRecord>(json, PuddingJsonContracts.PrettyJson);

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(original.Name, deserialized.Name);
        Assert.AreEqual(original.Value, deserialized.Value);
    }
}
