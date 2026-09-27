using System.Text.Json;
using PuddingApproval;

namespace PuddingApprovalTests;

public sealed class OperationTests
{
    private static ApprovalOperation Operation => new("write_file", "{\"path\":\"a.cs\",\"text\":\"你好\",\"flags\":[1,2]}",
        "{\"version\":1,\"schema\":{\"type\":\"object\"}}", Path.GetTempPath());

    [Fact]
    public void JsonFormattingKeyOrderAndEscapesDoNotChangeIdentity()
    {
        var equivalent = Operation with { ArgumentsJson = " { \"flags\": [1, 2], \"text\": \"\\u4f60\\u597d\", \"path\": \"a.cs\" } ",
            ToolDefinitionJson = "{\"schema\":{\"type\":\"object\"},\"version\":1}" };
        Assert.Equal(Operation.Fingerprint(), equivalent.Fingerprint());
        Assert.StartsWith("sha256:v1:", Operation.Fingerprint());
    }

    [Fact]
    public void OperationChangesInvalidateIdentityWithoutBroadeningPermission()
    {
        foreach (var changed in new[] {
            Operation with { ToolId = "delete_file" },
            Operation with { ArgumentsJson = "{\"path\":\"b.cs\",\"text\":\"你好\",\"flags\":[1,2]}" },
            Operation with { ArgumentsJson = "{\"path\":\"a.cs\",\"text\":\"你好\",\"flags\":[2,1]}" },
            Operation with { ToolDefinitionJson = "{\"version\":2,\"schema\":{\"type\":\"object\"}}" },
            Operation with { ExecutionRoot = Path.Combine(Path.GetTempPath(), "other") } })
            Assert.NotEqual(Operation.Fingerprint(), changed.Fingerprint());
        // Conservative numeric spelling: do not accidentally merge values through double rounding.
        Assert.NotEqual((Operation with { ArgumentsJson = "{\"x\":1}" }).Fingerprint(),
            (Operation with { ArgumentsJson = "{\"x\":1.0}" }).Fingerprint());
        Assert.NotEqual((Operation with { ArgumentsJson = "{\"x\":9007199254740992}" }).Fingerprint(),
            (Operation with { ArgumentsJson = "{\"x\":9007199254740993}" }).Fingerprint());
    }

    [Theory]
    [InlineData("{\"x\":1,\"x\":2}")]
    [InlineData("{\"nested\":{\"x\":1,\"\\u0078\":2}}")]
    [InlineData("{\"array\":[{\"x\":1,\"x\":2}]}")]
    [InlineData("[]")]
    [InlineData("null")]
    public void AmbiguousOrNonObjectArgumentsCannotBeApproved(string arguments)
        => Assert.Throws<ArgumentException>(() => (Operation with { ArgumentsJson = arguments }).Fingerprint());

    [Fact]
    public void InvalidRootsOversizedAndMalformedJsonAreRejected()
    {
        Assert.Throws<ArgumentException>(() => (Operation with { ExecutionRoot = "relative" }).Fingerprint());
        Assert.Throws<ArgumentException>(() => (Operation with { ArgumentsJson = new string(' ', ApprovalOperation.MaxJsonCharacters + 1) }).Fingerprint());
        Assert.ThrowsAny<JsonException>(() => (Operation with { ArgumentsJson = "{broken}" }).Fingerprint());
        Assert.Throws<ArgumentException>(() => (Operation with { ToolDefinitionJson = "{\"v\":1,\"v\":2}" }).Fingerprint());
    }

    [Fact]
    public void SnapshotSurvivesSerializationAndRejectsTampering()
    {
        var record = new ApprovalRecord("id", new("w", "a", "s", "r", "t", "i", Operation.Fingerprint(), "policy"),
            DateTimeOffset.UtcNow.AddMinutes(1), Operation);
        var restored = JsonSerializer.Deserialize<ApprovalRecord>(JsonSerializer.Serialize(record))!;
        Assert.Equal(record, restored); restored.ValidateOperation();
        Assert.Throws<ArgumentException>(() => (restored with { Operation = Operation with { ArgumentsJson = "{}" } }).ValidateOperation());
        Assert.Throws<ArgumentException>(() => (restored with { Operation = null! }).ValidateOperation());
    }
}
