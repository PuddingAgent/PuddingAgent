using System.Text.Json;
using PuddingCode.Serialization;

namespace PuddingCoreTests.Serialization;

[TestClass]
public sealed class PuddingJsonContractsTests
{
    [TestMethod]
    public void JsonLines_Writes_CamelCase_Single_Line_Json()
    {
        var json = JsonSerializer.Serialize(new
        {
            ParentSessionId = "parent-1",
            Payload = new { Line = "one\ntwo" },
        }, PuddingJsonContracts.JsonLines);

        Assert.IsFalse(json.Contains('\r'));
        Assert.IsFalse(json.Contains('\n'));
        StringAssert.Contains(json, "\"parentSessionId\"");
        StringAssert.Contains(json, "\"payload\"");
        StringAssert.Contains(json, "\\n");
    }

    [TestMethod]
    public void PrettyJson_Writes_CamelCase_Indented_Json()
    {
        var json = JsonSerializer.Serialize(new
        {
            ParentSessionId = "parent-1",
            Payload = new { Value = 42 },
        }, PuddingJsonContracts.PrettyJson);

        Assert.IsTrue(json.Contains('\n'));
        StringAssert.Contains(json, "\"parentSessionId\"");
        StringAssert.Contains(json, "  \"payload\"");
    }
}
