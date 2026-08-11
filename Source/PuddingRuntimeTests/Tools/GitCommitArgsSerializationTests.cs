using System.Text.Json;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class GitCommitArgsSerializationTests
{
    // Mirrors the options used by PuddingToolBase.DeserializeArgs (Web defaults,
    // case-insensitive property matching) so the test exercises the real pipeline.
    private static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static GitCommitArgs Deserialize(string json)
        => JsonSerializer.Deserialize<GitCommitArgs>(json, s_options)!;

    [TestMethod]
    public void Files_Accepts_Single_String()
    {
        // LLM callers sometimes emit a scalar where the schema declares an array.
        var args = Deserialize("""{"message":"fix","files":"file.cs"}""");

        CollectionAssert.AreEqual(new[] { "file.cs" }, args.Files);
    }

    [TestMethod]
    public void Files_Accepts_String_Array()
    {
        var args = Deserialize("""{"message":"fix","files":["a.cs","b.cs"]}""");

        CollectionAssert.AreEqual(new[] { "a.cs", "b.cs" }, args.Files);
    }

    [TestMethod]
    public void Files_Accepts_Empty_Array()
    {
        var args = Deserialize("""{"message":"fix","files":[]}""");

        Assert.IsNotNull(args.Files);
        Assert.AreEqual(0, args.Files!.Length);
    }

    [TestMethod]
    public void Files_Accepts_Null()
    {
        var args = Deserialize("""{"message":"fix","files":null}""");

        Assert.IsNull(args.Files);
    }

    [TestMethod]
    public void Files_Defaults_To_Null_When_Missing()
    {
        var args = Deserialize("""{"message":"fix"}""");

        Assert.IsNull(args.Files);
    }

    [TestMethod]
    public void Files_Rejects_Non_String_Array_Element()
    {
        Assert.ThrowsExactly<JsonException>(() =>
            Deserialize("""{"message":"fix","files":[42]}"""));
    }

    [TestMethod]
    public void Files_Rejects_Number()
    {
        Assert.ThrowsExactly<JsonException>(() =>
            Deserialize("""{"message":"fix","files":42}"""));
    }

    [TestMethod]
    public void Files_Works_With_Exact_Case_Property_Name_And_Default_Options()
    {
        // The converter is driven by the [JsonConverter] attribute, so it must
        // also work without case-insensitive options.
        var args = JsonSerializer.Deserialize<GitCommitArgs>(
            """{"Message":"fix","Files":"file.cs"}""")!;

        CollectionAssert.AreEqual(new[] { "file.cs" }, args.Files);
    }
}
