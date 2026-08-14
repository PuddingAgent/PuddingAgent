using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// RetentionArchiveWriter 归档写入器行为验证：
/// 1) 完整字段序列化：每行 = 实体全部列 + 归档元数据（archived_at / retention_cutoff / table_name）
/// 2) append-only 幂等：同一批重复归档追加多行，每行元数据齐全、可审计
/// 3) 按天分片：归档文件落在 retention-archive/{yyyy-MM-dd}/{tableName}.jsonl
/// </summary>
[TestClass]
public sealed class RetentionArchiveWriterTests
{
    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "pudding-retention-writer-tests", Guid.NewGuid().ToString("N"));

    private static SessionEventLogEntity MakeSessionEvent(long id) => new()
    {
        Id = id,
        SessionId = "s-1",
        WorkspaceId = "default",
        AgentInstanceId = "agent-1",
        AgentTemplateId = "tpl-1",
        SequenceNum = 7,
        EventType = "delta",
        Data = "{\"x\":1}",
        RecordedAt = "2026-08-01T00:00:00.0000000+00:00",
        TraceId = "trace-1",
        CorrelationId = "corr-1",
        ExecutionId = "exec-1",
        ParentExecutionId = "parent-1",
        SubAgentId = "sub-1",
        Component = "comp-1",
        Operation = "op-1",
    };

    private static ConversationEventEntity MakeConversationEvent(long id) => new()
    {
        Id = id,
        ConversationId = "conv-1",
        Sequence = 9,
        EventId = "evt-1",
        WorkspaceId = "default",
        TurnId = "turn-1",
        CommandId = "cmd-1",
        RunId = "run-1",
        MessageId = "msg-1",
        Type = "message",
        SchemaVersion = 3,
        Payload = "{\"y\":2}",
        OccurredAt = "2026-08-01T00:00:00.0000000+00:00",
        CommittedAt = "2026-08-01T00:00:00.0000000+00:00",
        CorrelationId = "corr-1",
        CausationId = "caus-1",
        ProducerEventId = "prod-1",
        AgentId = "agent-1",
        SourceKind = "test",
    };

    [TestMethod]
    public async Task ArchiveBatch_Serializes_All_SessionEvent_Columns_With_Metadata()
    {
        var root = CreateTempRoot();
        var writer = new RetentionArchiveWriter(
            PuddingDataPaths.FromRoot(root),
            NullLogger<RetentionArchiveWriter>.Instance);

        var cutoff = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        await writer.ArchiveBatchAsync("session_event_log", new[] { MakeSessionEvent(42) }, cutoff);

        var day = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd");
        var file = PuddingDataPaths.FromRoot(root).PlatformRetentionArchiveFile("session_event_log", day);
        var lines = await File.ReadAllLinesAsync(file);
        Assert.AreEqual(1, lines.Length);

        using var doc = JsonDocument.Parse(lines[0]);
        var r = doc.RootElement;

        // 全部 15 列
        Assert.AreEqual(42, r.GetProperty("Id").GetInt64());
        Assert.AreEqual("s-1", r.GetProperty("SessionId").GetString());
        Assert.AreEqual("default", r.GetProperty("WorkspaceId").GetString());
        Assert.AreEqual("agent-1", r.GetProperty("AgentInstanceId").GetString());
        Assert.AreEqual("tpl-1", r.GetProperty("AgentTemplateId").GetString());
        Assert.AreEqual(7, r.GetProperty("SequenceNum").GetInt64());
        Assert.AreEqual("delta", r.GetProperty("EventType").GetString());
        Assert.AreEqual("{\"x\":1}", r.GetProperty("Data").GetString());
        Assert.AreEqual("2026-08-01T00:00:00.0000000+00:00", r.GetProperty("RecordedAt").GetString());
        Assert.AreEqual("trace-1", r.GetProperty("TraceId").GetString());
        Assert.AreEqual("corr-1", r.GetProperty("CorrelationId").GetString());
        Assert.AreEqual("exec-1", r.GetProperty("ExecutionId").GetString());
        Assert.AreEqual("parent-1", r.GetProperty("ParentExecutionId").GetString());
        Assert.AreEqual("sub-1", r.GetProperty("SubAgentId").GetString());
        Assert.AreEqual("comp-1", r.GetProperty("Component").GetString());
        Assert.AreEqual("op-1", r.GetProperty("Operation").GetString());

        // 归档元数据
        Assert.AreEqual("session_event_log", r.GetProperty("table_name").GetString());
        Assert.AreEqual(cutoff.ToString("O"), r.GetProperty("retention_cutoff").GetString());
        Assert.IsTrue(r.TryGetProperty("archived_at", out _));
    }

    [TestMethod]
    public async Task ArchiveBatch_Serializes_All_ConversationEvent_Columns_With_Metadata()
    {
        var root = CreateTempRoot();
        var writer = new RetentionArchiveWriter(
            PuddingDataPaths.FromRoot(root),
            NullLogger<RetentionArchiveWriter>.Instance);

        var cutoff = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        await writer.ArchiveBatchAsync("conversation_events", new[] { MakeConversationEvent(99) }, cutoff);

        var day = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd");
        var file = PuddingDataPaths.FromRoot(root).PlatformRetentionArchiveFile("conversation_events", day);
        var lines = await File.ReadAllLinesAsync(file);
        Assert.AreEqual(1, lines.Length);

        using var doc = JsonDocument.Parse(lines[0]);
        var r = doc.RootElement;

        // 全部 19 列
        Assert.AreEqual(99, r.GetProperty("Id").GetInt64());
        Assert.AreEqual("conv-1", r.GetProperty("ConversationId").GetString());
        Assert.AreEqual(9, r.GetProperty("Sequence").GetInt64());
        Assert.AreEqual("evt-1", r.GetProperty("EventId").GetString());
        Assert.AreEqual("default", r.GetProperty("WorkspaceId").GetString());
        Assert.AreEqual("turn-1", r.GetProperty("TurnId").GetString());
        Assert.AreEqual("cmd-1", r.GetProperty("CommandId").GetString());
        Assert.AreEqual("run-1", r.GetProperty("RunId").GetString());
        Assert.AreEqual("msg-1", r.GetProperty("MessageId").GetString());
        Assert.AreEqual("message", r.GetProperty("Type").GetString());
        Assert.AreEqual(3, r.GetProperty("SchemaVersion").GetInt32());
        Assert.AreEqual("{\"y\":2}", r.GetProperty("Payload").GetString());
        Assert.AreEqual("2026-08-01T00:00:00.0000000+00:00", r.GetProperty("OccurredAt").GetString());
        Assert.AreEqual("2026-08-01T00:00:00.0000000+00:00", r.GetProperty("CommittedAt").GetString());
        Assert.AreEqual("corr-1", r.GetProperty("CorrelationId").GetString());
        Assert.AreEqual("caus-1", r.GetProperty("CausationId").GetString());
        Assert.AreEqual("prod-1", r.GetProperty("ProducerEventId").GetString());
        Assert.AreEqual("agent-1", r.GetProperty("AgentId").GetString());
        Assert.AreEqual("test", r.GetProperty("SourceKind").GetString());

        Assert.AreEqual("conversation_events", r.GetProperty("table_name").GetString());
        Assert.AreEqual(cutoff.ToString("O"), r.GetProperty("retention_cutoff").GetString());
        Assert.IsTrue(r.TryGetProperty("archived_at", out _));
    }

    [TestMethod]
    public async Task ArchiveBatch_Is_AppendOnly_And_Idempotent()
    {
        var root = CreateTempRoot();
        var writer = new RetentionArchiveWriter(
            PuddingDataPaths.FromRoot(root),
            NullLogger<RetentionArchiveWriter>.Instance);

        var cutoff = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        // 模拟「归档成功但 DELETE 失败」后的重跑：同一批再归档一次
        await writer.ArchiveBatchAsync("session_event_log", new[] { MakeSessionEvent(1) }, cutoff);
        await writer.ArchiveBatchAsync("session_event_log", new[] { MakeSessionEvent(1) }, cutoff);

        var day = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd");
        var file = PuddingDataPaths.FromRoot(root).PlatformRetentionArchiveFile("session_event_log", day);
        var lines = await File.ReadAllLinesAsync(file);

        // 追加式：两行都在（可容忍的重复），每行都带 table_name / retention_cutoff / archived_at 便于审计
        Assert.AreEqual(2, lines.Length);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            Assert.AreEqual("session_event_log", r.GetProperty("table_name").GetString());
            Assert.AreEqual(cutoff.ToString("O"), r.GetProperty("retention_cutoff").GetString());
            Assert.IsTrue(r.TryGetProperty("archived_at", out _));
        }
    }
}
