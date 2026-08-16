using PuddingCode.Models;
using PuddingCode.Tasks;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatformTests.Services.Tasks;

/// <summary>
/// TB-05: TaskInstructionEnvelope / TaskDispatchIds 纯契约测试（无 DB）。
/// 覆盖 ADR-072 §9.1（From=system/task-orchestrator、ContentType=task_instruction、metadata）
/// 与 §5.3 幂等键派生（≤64 字符、确定性）。
/// </summary>
[TestClass]
public sealed class TaskInstructionEnvelopeTests
{
    [TestMethod]
    public void BuildMessageId_IsDeterministicAndWithin64Chars()
    {
        var key = TaskDispatchIds.BuildIdempotencyKey("t-12345678901234567890123456789012", "a-abcdef");
        var first = TaskDispatchIds.BuildMessageId(key);
        var second = TaskDispatchIds.BuildMessageId(key);

        Assert.AreEqual(first, second);
        Assert.IsTrue(first.Length <= 64);
        Assert.IsTrue(first.StartsWith("task-", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildMessageId_DifferentKeysProduceDifferentIds()
    {
        var keyA = TaskDispatchIds.BuildIdempotencyKey("task-a", "assign-a");
        var keyB = TaskDispatchIds.BuildIdempotencyKey("task-a", "assign-b");
        Assert.AreNotEqual(TaskDispatchIds.BuildMessageId(keyA), TaskDispatchIds.BuildMessageId(keyB));
    }

    [TestMethod]
    public void ToMessageEnvelope_UsesAuthoritativeFormat()
    {
        var envelope = new TaskInstructionEnvelope
        {
            IdempotencyKey = "task:t:assign:a",
            WorkspaceId = "default",
            TaskId = "t",
            AssignmentId = "a",
            AgentId = "agent-1",
            Origin = TaskInstructionEnvelope.OriginTaskManual,
            Priority = "p0",
            ExecutionWindow = "anytime",
            Title = "标题",
            Description = "描述",
            AcceptanceCriteria = "验收",
        };

        var message = envelope.ToMessageEnvelope();

        Assert.AreEqual(envelope.MessageId, message.MessageId);
        Assert.AreEqual(MessageEndpointKinds.System, message.From.Kind);
        Assert.AreEqual("task-orchestrator", message.From.Id);
        Assert.AreEqual(TaskInstructionEnvelope.ContentTypeTaskInstruction, message.ContentType);
        Assert.AreEqual(MessageAudiences.Direct, message.Audience);
        Assert.AreEqual(MessageVisibilities.System, message.Visibility);
        Assert.AreEqual(1, message.To.Count);
        Assert.AreEqual(MessageEndpointKinds.Agent, message.To[0].Kind);
        Assert.AreEqual("agent-1", message.To[0].Id);

        // metadata 不可被 Prompt 改写（§9.1）
        Assert.AreEqual("task.manual", message.Metadata["origin"]);
        Assert.AreEqual("t", message.Metadata["task_id"]);
        Assert.AreEqual("a", message.Metadata["assignment_id"]);
        Assert.AreEqual("p0", message.Metadata["priority"]);
        Assert.AreEqual("anytime", message.Metadata["execution_window"]);
        Assert.AreEqual(envelope.IdempotencyKey, message.Metadata["dispatch_idempotency_key"]);
        Assert.IsTrue(message.Content.Contains("标题"));
        Assert.IsTrue(message.Content.Contains("验收标准"));
    }

    [TestMethod]
    public void Serialization_RoundTrips()
    {
        var envelope = new TaskInstructionEnvelope
        {
            IdempotencyKey = "task:t:assign:a",
            WorkspaceId = "default",
            TaskId = "t",
            AssignmentId = "a",
            AgentId = "agent-1",
            Origin = "task.manual",
            Priority = "p1",
            ExecutionWindow = "off_peak_only",
            Title = "Title",
            Description = null,
            AcceptanceCriteria = "AC",
        };

        var json = TaskDispatchSerialization.Serialize(envelope);
        var roundTripped = TaskDispatchSerialization.Deserialize(json);

        Assert.AreEqual(envelope.IdempotencyKey, roundTripped.IdempotencyKey);
        Assert.AreEqual(envelope.TaskId, roundTripped.TaskId);
        Assert.AreEqual(envelope.AssignmentId, roundTripped.AssignmentId);
        Assert.AreEqual(envelope.AgentId, roundTripped.AgentId);
        Assert.AreEqual(envelope.Priority, roundTripped.Priority);
        Assert.AreEqual(envelope.ExecutionWindow, roundTripped.ExecutionWindow);
        Assert.AreEqual(envelope.Title, roundTripped.Title);
        Assert.AreEqual(envelope.MessageId, roundTripped.MessageId);
    }
}
