using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Serialization;
using PuddingCode.SubAgents;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class FileSubAgentRunStoreTests
{
    [TestMethod]
    public async Task RunArchive_Writes_Expected_File_Formats_And_Terminal_State_Is_Idempotent()
    {
        using var temp = TemporaryDirectory.Create();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var dbPath = Path.Combine(temp.Path, "platform.db");
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var store = new FileSubAgentRunStore(
            paths,
            NullLogger<FileSubAgentRunStore>.Instance,
            new TestDbContextFactory(options));

        var handle = await store.CreateRunAsync(new SubAgentRunCreateRequest
        {
            ParentSessionId = "parent-session",
            SubSessionId = "parent-session/sub/sub-agent",
            WorkspaceId = "default",
            AgentInstanceId = "default.researcher-001",
            TemplateId = "researcher",
            Task = "Research the current architecture",
        });

        var runJsonPath = Path.Combine(handle.ArchivePath, "run.json");
        var runJson = await File.ReadAllTextAsync(runJsonPath);
        Assert.IsTrue(runJson.Contains('\n'));
        StringAssert.Contains(runJson, "\"parentSessionId\"");

        await store.AppendEventAsync(handle.RunId, "subagent.run.started", new
        {
            ParentSessionId = "parent-session",
            Detail = "line one\nline two",
        });

        await store.AppendToolAuditAsync(handle.RunId, new SubAgentToolAuditEntry
        {
            ToolCallId = "tool-1",
            ToolName = "read_file",
            ArgsHash = "sha256:abc",
            Success = true,
            DurationMs = 17,
            OutputLength = 128,
        });

        var eventsLines = await File.ReadAllLinesAsync(Path.Combine(handle.ArchivePath, "events.jsonl"));
        Assert.AreEqual(1, eventsLines.Length);
        Assert.IsFalse(eventsLines[0].Contains('\r'));
        Assert.IsFalse(eventsLines[0].Contains('\n'));
        StringAssert.Contains(eventsLines[0], "\\n");

        var applied = await store.CompleteRunAsync(handle.RunId, new SubAgentRunCompletion
        {
            Status = "completed",
            Output = "final output",
            TotalRounds = 3,
            TotalToolCalls = 1,
            TotalDurationMs = 250,
        });
        var alreadyTerminal = await store.CompleteRunAsync(handle.RunId, new SubAgentRunCompletion
        {
            Status = "failed",
            ErrorMessage = "late duplicate completion",
        });

        Assert.AreEqual(SubAgentRunTerminalWriteResult.Applied, applied);
        Assert.AreEqual(SubAgentRunTerminalWriteResult.AlreadyTerminal, alreadyTerminal);

        var archive = await store.GetRunArchiveAsync(handle.RunId);
        Assert.IsNotNull(archive);
        Assert.AreEqual("completed", archive.Manifest.Status);
        Assert.AreEqual(1, archive.Events.Count);
        Assert.AreEqual(1, archive.Tools.Count);
        Assert.AreEqual("final output", archive.Output);

        await using var verifyDb = new PlatformDbContext(options);
        var index = await verifyDb.SubAgentRuns.SingleAsync(r => r.RunId == handle.RunId);
        Assert.AreEqual("completed", index.Status);
        Assert.AreEqual(3, index.TotalRounds);
        Assert.AreEqual(1, index.TotalToolCalls);
        Assert.AreEqual(250, index.TotalDurationMs);

        var manifest = JsonSerializer.Deserialize<SubAgentRunManifest>(
            await File.ReadAllTextAsync(runJsonPath),
            PuddingJsonContracts.PrettyJson);
        Assert.IsNotNull(manifest);
        Assert.AreEqual("completed", manifest.Status);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-platform-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
