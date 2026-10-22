using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingRuntime.Services.Diagnostics;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class CompactionDiagnosticJournalTests
{
    [TestMethod]
    public void RequestShape_RecordsRoleTransitionWithoutExposingBodies()
    {
        ChatMessage[] messages = [new(ChatRole.User, "PRIVATE USER BODY"),
            ChatMessage.ContextSummary("PRIVATE SUMMARY BODY"),
            new(ChatRole.Assistant, null, ReasoningContent: "PRIVATE REASONING BODY",
                ContinuationState: new("responses", ["{\"type\":\"reasoning\"}"]))];
        var json = JsonSerializer.Serialize(CompactionDiagnosticJournal.DescribeRequest(messages));
        Assert.IsFalse(json.Contains("PRIVATE"));
        using var document = JsonDocument.Parse(json);
        var checkpoint = document.RootElement[1];
        Assert.AreEqual("Assistant", checkpoint.GetProperty("role").GetString());
        Assert.AreEqual("User", checkpoint.GetProperty("providerRole").GetString());
        Assert.AreEqual(1, document.RootElement[2].GetProperty("continuationItems").GetInt32());
        Assert.AreEqual(64, checkpoint.GetProperty("identity").GetString()!.Length);
    }

    [TestMethod]
    public async Task SourceAndAttemptSurviveRestart_WithSecretsRemovedAndStableHashes()
    {
        var root = TestRoot();
        var journal = Create(root);
        var name = await journal.BeginAsync("../../unsafe-compaction", new { traceId = "trace", generation = 3 },
            [new ChatMessage(ChatRole.User, "constraint PRIVATE_VAULT_VALUE"),
             new ChatMessage(ChatRole.Tool, "api_key=unregistered-value"),
             new ChatMessage(ChatRole.User, "https://example.test/path?auth=private")], CancellationToken.None);
        Assert.IsNotNull(name);
        Assert.IsTrue(await journal.AppendAsync(name, "attempt", new
        { call = 1, finishReason = "length", completionTokens = 4005, content = "unfinished summary", apiKey = "other-secret" }, CancellationToken.None));
        var lines = await File.ReadAllLinesAsync(Path.Combine(root, "logs", "diagnostics", "compaction", name));
        Assert.AreEqual(2, lines.Length);
        var all = string.Join("", lines);
        foreach (var secret in new[] { "PRIVATE_VAULT_VALUE", "unregistered-value", "auth=private", "other-secret" })
            Assert.IsFalse(all.Contains(secret), secret);
        using var attempt = JsonDocument.Parse(lines[1]);
        Assert.AreEqual(4005, attempt.RootElement.GetProperty("detail").GetProperty("completionTokens").GetInt32());
        StringAssert.Contains(all, "identity");
        StringAssert.Contains(all, "unfinished summary");
        // Fresh writer can append after restart without rewriting the preserved plan.
        Assert.IsTrue(await Create(root).AppendAsync(name, "outcome", new { historyPreserved = true }, CancellationToken.None));
        Assert.AreEqual(3, File.ReadAllLines(Path.Combine(root, "logs", "diagnostics", "compaction", name)).Length);
    }

    [TestMethod]
    public async Task OversizedCaptureIsExplicit_AndRetentionIsBounded()
    {
        var root = TestRoot();
        var journal = Create(root);
        var name = await journal.BeginAsync("case", new { }, [], CancellationToken.None);
        Assert.IsFalse(await journal.AppendAsync(name, "attempt", new { content = new string('x', (int)CompactionDiagnosticJournal.MaxCaseBytes) }, CancellationToken.None));
        var path = Path.Combine(root, "logs", "diagnostics", "compaction", name!);
        Assert.IsTrue(new FileInfo(path).Length < CompactionDiagnosticJournal.MaxCaseBytes);
        StringAssert.Contains(await File.ReadAllTextAsync(path), "capture_limit");
        for (var i = 0; i < CompactionDiagnosticJournal.MaxCases + 2; i++)
            Assert.IsNotNull(await journal.BeginAsync("case-" + i, new { }, [], CancellationToken.None));
        Assert.AreEqual(CompactionDiagnosticJournal.MaxCases, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
    }

    [TestMethod]
    public async Task WriteOrRedactionFailure_DoesNotEscapeOrWriteRawContent()
    {
        var root = TestRoot();
        var vault = new Mock<IKeyVaultService>();
        vault.Setup(v => v.StripAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("unavailable"));
        var journal = new CompactionDiagnosticJournal(new PuddingDataPaths { DataRoot = root }, vault.Object,
            NullLogger<CompactionDiagnosticJournal>.Instance);
        Assert.IsNull(await journal.BeginAsync("case", new { }, [new ChatMessage(ChatRole.User, "SECRET")], CancellationToken.None));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "logs")));
        var file = Path.Combine(root, "blocked");
        await File.WriteAllTextAsync(file, "existing");
        Assert.IsNull(await Create(file).BeginAsync("case", new { }, [], CancellationToken.None));
    }

    private static string TestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-compaction-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    private static CompactionDiagnosticJournal Create(string root)
    {
        var vault = new Mock<IKeyVaultService>();
        vault.Setup(v => v.StripAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string text, CancellationToken _) => text.Replace("PRIVATE_VAULT_VALUE", "{{vault:known}}"));
        return new(new PuddingDataPaths { DataRoot = root }, vault.Object, NullLogger<CompactionDiagnosticJournal>.Instance);
    }
}
