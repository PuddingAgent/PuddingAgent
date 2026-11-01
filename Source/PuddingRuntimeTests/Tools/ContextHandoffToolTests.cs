using System.Text.Json;
using PuddingCode.Configuration;
using PuddingCode.Tools;
using PuddingCode.Runtime;
using PuddingCode.Models;
using PuddingRuntime.Services;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class ContextHandoffToolTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pudding-handoff-tool-tests", Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private Task<ToolExecutionResult> Call(object args, string session = "s") =>
        new ContextHandoffTool(PuddingDataPaths.FromRoot(_root)).ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "host-call",
            ArgumentsJson = JsonSerializer.Serialize(args),
            Context = new ToolExecutionContext
            {
                WorkspaceId = "w", SessionId = session, AgentInstanceId = "a", ToolCallId = "host-call",
                ExecutionIdentity = new RuntimeExecutionIdentity
                {
                    Kind = RuntimeExecutionKind.ConversationTurn, ConversationId = "c", RunId = "r",
                    TraceId = null, TurnId = "host-turn",
                },
            },
        });

    [TestMethod]
    public async Task SaveAcknowledgesOnlyLocator_AndReadRestoresExactHistoricalNoteAfterRestart()
    {
        const string summary = "要求：不部署。已完成：测试。下一步：等评审。证据：Docs/report.md。";
        var saved = await Call(new { action = "save", summary });
        Assert.IsTrue(saved.Success, saved.Error);
        var json = JsonDocument.Parse(saved.Output).RootElement;
        Assert.AreEqual("saved", json.GetProperty("status").GetString());
        Assert.AreEqual("host-turn", json.GetProperty("turn_id").GetString());
        Assert.IsFalse(saved.Output.Contains(summary, StringComparison.Ordinal));
        var id = json.GetProperty("handoff_id").GetString();
        await Call(new { action = "save", summary = "后续用户已批准；尚未执行部署。" });
        var restored = await Call(new { action = "read", handoff_id = id });
        var record = JsonDocument.Parse(restored.Output).RootElement.GetProperty("note");
        Assert.AreEqual(summary, record.GetProperty("summary").GetString());
        Assert.AreEqual("host-call", record.GetProperty("toolCallId").GetString());
        var other = await Call(new { action = "read", handoff_id = id }, "other");
        Assert.AreEqual("not_found", JsonDocument.Parse(other.Output).RootElement.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task InvalidSave_NeverReportsPersistenceOrOverwritesLatest()
    {
        await Call(new { action = "save", summary = "last good note" });
        foreach (var args in new object[]
        {
            new { action = "save", summary = new string('中', 2001) },
            new { action = "save", summary = " " },
            new { action = "save", summary = "bad overwrite", handoff_id = "existing" },
            new { action = "drop" },
            new { action = "read", handoff_id = "../other" },
        }) Assert.IsFalse((await Call(args)).Success);
        var result = await Call(new { action = "read" });
        Assert.AreEqual("last good note", JsonDocument.Parse(result.Output).RootElement.GetProperty("note").GetProperty("summary").GetString());
    }

    [TestMethod]
    public async Task StorageFailure_IsExplicitAndDoesNotLeakContent()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "runtime"), "path is a file");
        var result = await Call(new { action = "save", summary = "sensitive note" });
        Assert.IsFalse(result.Success);
        Assert.IsFalse((result.Error ?? "").Contains("sensitive note"));
        StringAssert.Contains(result.Error!, "history is unchanged");
    }

    [TestMethod]
    public async Task MaximumChineseNote_ReadsInlineWithoutEscapingExpansionOrSpill()
    {
        var summary = new string('中', 2000);
        Assert.IsTrue((await Call(new { action = "save", summary })).Success);
        var read = await Call(new { action = "read" });
        var payload = "✅ Tool 'context_handoff' succeeded (exit=0):\n" + read.Output;
        var materialized = await ToolResultContextPolicy.MaterializeAsync(payload, _root, "s", "context_handoff", "read-call",
            NullLogger.Instance, CancellationToken.None);
        Assert.AreEqual(payload, materialized, "The complete saved note must fit the real model-history tool budget.");
        StringAssert.Contains(materialized, summary);
    }

    [TestMethod]
    public async Task SavedNoteAndHostLocator_ReachSelectedSpanCompactionWithLaterCorrection()
    {
        const string summary = "已完成审查；下一步部署。证据：review.md。";
        var saved = await Call(new { action = "save", summary });
        var id = JsonDocument.Parse(saved.Output).RootElement.GetProperty("handoff_id").GetString()!;
        var parts = BoundedCompactionSummary.CreateParts([
            new ChatMessage(ChatRole.Assistant, null, ToolCalls: [new ToolCall("host-call", "context_handoff", JsonSerializer.Serialize(new { action = "save", summary }))]),
            new ChatMessage(ChatRole.Tool, saved.Output, ToolCallId: "host-call"),
            new ChatMessage(ChatRole.User, "纠正：不要部署，先等评审。"),
        ]);
        var serialized = string.Join("\n", parts.SelectMany(p => p).Skip(1).Select(m => m.Content));
        StringAssert.Contains(serialized, "context_handoff");
        StringAssert.Contains(serialized, id);
        StringAssert.Contains(serialized, "不要部署");
        StringAssert.Contains(parts[0][0].Content!, "not as verified evidence");
    }
}
