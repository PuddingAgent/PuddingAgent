using Microsoft.Extensions.Logging.Abstractions;
using PuddingRuntime.Services;
using System.Text.Json;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class ToolResultContextPolicyTests
{
    [TestMethod]
    public async Task MaterializeAsync_Spills_Oversized_Result_And_Returns_Bounded_Preview()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"pudding-tool-result-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var content = "SECRET-ORIGINAL-VALUE\n" + new string('h', 9_000) + "TAIL-SENTINEL";

            var result = await ToolResultContextPolicy.MaterializeAsync(
                content,
                workspace,
                "session-1",
                "search_grep",
                "call-1",
                NullLogger.Instance,
                CancellationToken.None);

            Assert.IsTrue(result.Length <= ToolResultContextPolicy.MaxInlineChars);
            StringAssert.Contains(result, "TOOL RESULT BOUNDED");
            StringAssert.Contains(result, "SECRET-ORIGINAL-VALUE");
            StringAssert.Contains(result, "TAIL-SENTINEL");
            StringAssert.Contains(result, "content_sha256=sha256:");
            StringAssert.Contains(result, "original_utf8_bytes=");

            var spillPath = Path.Combine(
                workspace,
                ".pudding",
                "context-tool-results",
                "session-1",
                "call-1-search_grep.txt");
            Assert.IsTrue(File.Exists(spillPath));
            Assert.AreEqual(content, await File.ReadAllTextAsync(spillPath));

            var manifestPath = spillPath + ".artifact.json";
            Assert.IsTrue(File.Exists(manifestPath));
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            Assert.AreEqual("tool_result", manifest.RootElement.GetProperty("kind").GetString());
            Assert.AreEqual("workspace", manifest.RootElement.GetProperty("workspaceScope").GetString());
            Assert.AreEqual("session-1", manifest.RootElement.GetProperty("sessionId").GetString());
            Assert.AreEqual("search_grep", manifest.RootElement.GetProperty("toolName").GetString());
            Assert.AreEqual("call-1", manifest.RootElement.GetProperty("toolCallId").GetString());
            Assert.AreEqual(content.Length, manifest.RootElement.GetProperty("originalCharCount").GetInt32());
            Assert.AreEqual(2, manifest.RootElement.GetProperty("originalLineCount").GetInt32());
            StringAssert.StartsWith(manifest.RootElement.GetProperty("contentSha256").GetString(), "sha256:");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task MaterializeAsync_Fails_Open_When_Working_Directory_Does_Not_Exist()
    {
        var content = new string('x', ToolResultContextPolicy.MaxInlineChars + 1);
        var missingWorkspace = Path.Combine(
            Path.GetTempPath(),
            $"pudding-tool-result-policy-missing-{Guid.NewGuid():N}");

        var result = await ToolResultContextPolicy.MaterializeAsync(
            content,
            missingWorkspace,
            "session-1",
            "file_read",
            "call-1",
            NullLogger.Instance,
            CancellationToken.None);

        Assert.AreEqual(content, result);
    }

    [TestMethod]
    public void BuildBoundedPreview_Does_Not_Split_Utf16_Surrogate_Pairs()
    {
        var content = string.Concat(Enumerable.Repeat("😀", 5_000));

        var result = ToolResultContextPolicy.BuildBoundedPreview(content, "[bounded]");

        Assert.IsLessThanOrEqualTo(ToolResultContextPolicy.MaxInlineChars, result.Length);
        for (var index = 0; index < result.Length; index++)
        {
            if (char.IsHighSurrogate(result[index]))
                Assert.IsTrue(index + 1 < result.Length && char.IsLowSurrogate(result[index + 1]));
            if (char.IsLowSurrogate(result[index]))
                Assert.IsTrue(index > 0 && char.IsHighSurrogate(result[index - 1]));
        }
    }
}
