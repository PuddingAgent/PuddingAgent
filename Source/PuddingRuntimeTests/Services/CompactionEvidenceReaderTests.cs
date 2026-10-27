using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class CompactionEvidenceReaderTests
{
    private static readonly CompactionEvidenceQuery Query = new("ws", "session", "c2", "agent");

    [TestMethod]
    public async Task RepeatedCompaction_AfterReaderRestart_TraversesToOriginalTurn()
    {
        await using var scope = await CreateAsync();
        var current = await new CompactionEvidenceReader(scope.Factory).ReadSourcesAsync(Query);
        Assert.AreEqual("ok", current.Status);
        Assert.AreEqual("c1", current.Sources[0].ChildCompactionId);
        var original = await new CompactionEvidenceReader(scope.Factory).ReadSourcesAsync(Query with { CompactionId = "c1", Limit = 1 });
        Assert.AreEqual("original-turn", original.Sources[0].TurnId);
        Assert.AreEqual("session", original.Sources[0].SessionId);
        Assert.AreEqual(1, original.NextOffset);
        var second = await new CompactionEvidenceReader(scope.Factory).ReadSourcesAsync(Query with { CompactionId = "c1", Offset = 1 });
        Assert.AreEqual("archived_source", second.Sources[0].Availability);
        Assert.IsNull(second.Sources[0].TurnId, "Never manufacture a Turn ID from runtime/tool text.");
        Assert.IsNull(second.NextOffset);
    }

    [TestMethod]
    public async Task SourceWindows_RecoverCompleteArchive_AndRejectUncoveredMessages()
    {
        await using var scope = await CreateAsync();
        var reader = new CompactionEvidenceReader(scope.Factory);
        var query = Query with { CompactionId = "c1" };
        var recovered = new StringBuilder();
        int? offset = 0;
        do
        {
            var window = await reader.ReadSourceAsync(query, "tool", offset.Value, 27);
            Assert.AreEqual("ok", window.Status);
            Assert.IsTrue(window.Content!.Length <= 27);
            recovered.Append(window.Content);
            offset = window.NextOffset;
        } while (offset is not null);
        using var json = System.Text.Json.JsonDocument.Parse(recovered.ToString());
        Assert.AreEqual("original tool output", json.RootElement.GetProperty("Content").GetString());
        Assert.AreEqual("not_found", (await reader.ReadSourceAsync(query, "summary2")).Status);
        foreach (var invalid in new[] { query with { WorkspaceId = "foreign" }, query with { AgentInstanceId = "foreign" }, query with { SessionId = "foreign" } })
        {
            Assert.AreEqual("not_found", (await reader.ReadSourcesAsync(invalid)).Status);
            Assert.AreEqual("not_found", (await reader.ReadSourceAsync(invalid, "tool")).Status);
        }
    }

    [TestMethod]
    public async Task MissingSourcesAndMalformedCoverage_AreExplicit_NotSuccessfulEmptyHistory()
    {
        await using var scope = await CreateAsync();
        await using var db = new MemoryDbContext(scope.Options);
        var manifest = await db.CompactionCoverageManifests.SingleAsync(m => m.CompactionId == "c2");
        manifest.SourceMessageIds = "[\"missing\"]";
        await db.SaveChangesAsync();
        var reader = new CompactionEvidenceReader(scope.Factory);
        var partial = await reader.ReadSourcesAsync(Query);
        Assert.AreEqual("partial", partial.Status);
        Assert.AreEqual("missing", partial.Sources[0].Availability);
        manifest.SourceMessageIds = "not json";
        await db.SaveChangesAsync();
        Assert.AreEqual("invalid_manifest", (await reader.ReadSourcesAsync(Query)).Status);
        manifest.SourceMessageIds = "[\"summary1\"]";
        manifest.OmittedCount = 1;
        await db.SaveChangesAsync();
        Assert.AreEqual("not_found", (await reader.ReadSourcesAsync(Query)).Status);
    }

    private static async Task<TestScope> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options;
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Sessions.Add(new SessionEntity { SessionId = "session", WorkspaceId = "ws", AgentId = "agent", CompactionGeneration = 2 });
        db.Messages.AddRange(
            new MessageEntity { MessageId = "original", SessionId = "session", Sequence = 1, Role = "user", Content = "original request", Source = "chat_transcript", Metadata = "original-turn\nplatform-message", CompactedBy = "summary1" },
            new MessageEntity { MessageId = "tool", SessionId = "session", Sequence = 2, Role = "tool", Content = "original tool output", Source = "warm_prefix_source", Metadata = "turn=forged", CompactedBy = "summary1" },
            new MessageEntity { MessageId = "summary1", SessionId = "session", Sequence = 3, Content = "first summary", ContentType = "compact_summary", CompactedBy = "summary2" },
            new MessageEntity { MessageId = "summary2", SessionId = "session", Sequence = 4, Content = "second summary", ContentType = "compact_summary" });
        db.CompactionCoverageManifests.AddRange(
            new CompactionCoverageManifestEntity { CompactionId = "c1", SessionId = "session", SourceGeneration = 0, TargetGeneration = 1, FinalSummaryId = "summary1", SourceMessageIds = "[\"original\",\"tool\"]", CoveredCount = 2 },
            new CompactionCoverageManifestEntity { CompactionId = "c2", SessionId = "session", SourceGeneration = 1, TargetGeneration = 2, FinalSummaryId = "summary2", SourceMessageIds = "[\"summary1\"]", CoveredCount = 1 });
        await db.SaveChangesAsync();
        return new(connection, options, new Factory(options));
    }

    private sealed record Factory(DbContextOptions<MemoryDbContext> Options) : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(Options);
    }

    private sealed record TestScope(SqliteConnection Connection, DbContextOptions<MemoryDbContext> Options, Factory Factory) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }
}
