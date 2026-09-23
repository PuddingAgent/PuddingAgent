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

    /// <summary>
    /// Regression guard for the original defect: when the durable copy cannot be written the
    /// policy used to return the raw payload, silently lifting the context bound exactly when
    /// the spill infrastructure was broken. The bound must hold on the failure path too, and
    /// the degradation must be machine-detectable.
    /// </summary>
    [TestMethod]
    public async Task MaterializeAsync_Keeps_Bound_And_Flags_Failure_When_Spill_Is_Impossible()
    {
        var content = new string('x', ToolResultContextPolicy.MaxInlineChars + 5_000);
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

        // The bound is the contract; it must not depend on the spill succeeding.
        Assert.IsLessThanOrEqualTo(ToolResultContextPolicy.MaxInlineChars, result.Length);
        Assert.IsLessThan(content.Length, result.Length);
        Assert.AreNotEqual(content, result);

        // The degradation is observable and self-describing.
        StringAssert.Contains(result, ToolResultContextPolicy.MaterializationFailureCode);
        StringAssert.Contains(result, "full_output_file=UNAVAILABLE");
        StringAssert.Contains(result, "content_sha256=sha256:");
        StringAssert.Contains(result, "tool=file_read");
        StringAssert.Contains(result, "call=call-1");
        StringAssert.Contains(result, "session=session-1");

        // No durable copy may be advertised or created.
        Assert.IsFalse(Directory.Exists(missingWorkspace));
    }

    [TestMethod]
    public void BuildUnmaterializedBound_Carries_Code_Identity_And_Hash()
    {
        var content = new string('z', 20_000);

        var result = ToolResultContextPolicy.BuildUnmaterializedBound(
            content,
            new IOException("disk full"),
            "session-9",
            "smart_explore",
            "call-9");

        Assert.IsLessThanOrEqualTo(ToolResultContextPolicy.MaxInlineChars, result.Length);
        StringAssert.Contains(result, $"error={ToolResultContextPolicy.MaterializationFailureCode}");
        StringAssert.Contains(result, "reason=IOException: disk full");
        StringAssert.Contains(result, "original_chars=20000");
        StringAssert.Contains(result, "content_sha256=sha256:");
        StringAssert.Contains(result, "tool=smart_explore");
        StringAssert.Contains(result, "call=call-9");
        StringAssert.Contains(result, "session=session-9");
    }

    [TestMethod]
    public void BuildUnmaterializedBound_Flattens_Failure_Reason_Into_One_Notice_Token()
    {
        var content = new string('q', 20_000);
        var noisy = new IOException("line one\r\nline two; [x] " + new string('m', 500));

        var result = ToolResultContextPolicy.BuildUnmaterializedBound(
            content,
            noisy,
            "session-1",
            "file_read",
            "call-1");

        Assert.IsLessThanOrEqualTo(ToolResultContextPolicy.MaxInlineChars, result.Length);
        Assert.IsFalse(result.Contains('\r'), "failure reason must not inject carriage returns");
        var noticeStart = result.IndexOf("[TOOL RESULT BOUNDED", StringComparison.Ordinal);
        Assert.IsGreaterThan(-1, noticeStart);
        var noticeEnd = result.IndexOf(']', noticeStart);
        Assert.IsGreaterThan(noticeStart, noticeEnd);
        var notice = result[noticeStart..(noticeEnd + 1)];
        Assert.AreEqual(1, notice.Split("reason=").Length - 1, "reason must appear exactly once");
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

    /// <summary>
    /// An oversized notice previously inflated the preview above the budget because the
    /// preview allowance was computed from the raw notice length.
    /// </summary>
    [TestMethod]
    public void BuildBoundedPreview_Stays_Within_Budget_For_Oversized_Notice()
    {
        var content = new string('y', 50_000);
        var notice = "[" + new string('n', ToolResultContextPolicy.MaxInlineChars * 2) + "]";

        var result = ToolResultContextPolicy.BuildBoundedPreview(content, notice);

        Assert.IsLessThanOrEqualTo(ToolResultContextPolicy.MaxInlineChars, result.Length);
    }
}
