using Lucene.Net.Analysis.Standard;
using Lucene.Net.Util;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingFullTextIndex.Infrastructure.Text;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

public sealed partial class RawSessionLogServiceTests
{
    [TestMethod]
    public async Task Fts_FiltersWorkspaceSessionAndDayBeforeLimiting()
    {
        await using var db = await CreateScopeAsync();
        using var files = new FtsFiles();
        db.Db.ConversationCatalogs.AddRange(
            new ConversationCatalogEntity { ConversationId = "mine", WorkspaceId = "ws", AgentId = "agent" },
            new ConversationCatalogEntity { ConversationId = "foreign", WorkspaceId = "other", AgentId = "agent" });
        await db.Db.SaveChangesAsync();
        files.Write("2026-09-14", "mine", "needle current evidence");
        files.Write("2026-09-14", "foreign", string.Join('\n', Enumerable.Repeat("needle", 50)));
        files.Write("2026-09-13", "mine", "needle old day");
        var service = new RawSessionLogService(db.Factory, files.Engine, files.Paths);
        var result = await service.GrepFtsAsync(FtsRequest() with { Limit = 1, SessionId = "mine" });
        Assert.AreEqual("ok", result.Status);
        Assert.AreEqual(1, result.Matches.Count);
        Assert.AreEqual("mine", result.Matches[0].SessionId);
        Assert.AreEqual("2026-09-14", result.Matches[0].Day);
        Assert.IsFalse(result.HasMore, "Foreign and older hits must not distort scoped hit count.");
        Assert.AreEqual("markdown_line", result.Matches[0].EventType);
        Assert.IsFalse(files.Engine.HasIndex(Path.GetDirectoryName(files.File("2026-09-13", "mine"))!));
    }

    [TestMethod]
    public async Task Fts_RefreshesExistingShard_AndBoundsSnippet()
    {
        await using var db = await CreateScopeAsync();
        using var files = new FtsFiles();
        db.Db.ConversationCatalogs.Add(new ConversationCatalogEntity { ConversationId = "mine", WorkspaceId = "ws", AgentId = "agent" });
        await db.Db.SaveChangesAsync();
        files.Write("2026-09-14", "mine", "needle oldline");
        var service = new RawSessionLogService(db.Factory, files.Engine, files.Paths);
        Assert.AreEqual(1, (await service.GrepFtsAsync(FtsRequest())).Matches.Count);
        files.Write("2026-09-14", "mine", "needle replacement " + new string('x', 20000));
        System.IO.File.SetLastWriteTimeUtc(files.File("2026-09-14", "mine"), DateTime.UtcNow.AddSeconds(1));
        var updated = await service.GrepFtsAsync(FtsRequest());
        Assert.AreEqual("ok", updated.Status);
        Assert.IsTrue(updated.Matches.Single().Snippet.Contains("replacement"));
        Assert.IsLessThanOrEqualTo(322, updated.Matches.Single().Snippet.Length);
        Assert.IsNull(updated.Matches.Single().FullContent);
    }

    [TestMethod]
    [DataRow("2026-02-30", null, null)]
    [DataRow(null, "2026-01-01", "2026-09-14")]
    [DataRow("2026-09-14", "2026-09-13", null)]
    [DataRow(null, "2026-09-14", "2026-09-13")]
    public async Task Fts_RejectsInvalidOrOversizedDates(string? day, string? from, string? to)
    {
        await using var db = await CreateScopeAsync();
        var service = new RawSessionLogService(db.Factory);
        var result = await service.GrepFtsAsync(FtsRequest() with { Day = day, FromDay = from, ToDay = to });
        Assert.AreEqual("contract_error", result.Status);
    }

    [TestMethod]
    public async Task Fts_UnavailableIsNotNoMatch_AndCancellationPropagates()
    {
        await using var db = await CreateScopeAsync();
        var service = new RawSessionLogService(db.Factory);
        Assert.AreEqual("unavailable", (await service.GrepFtsAsync(FtsRequest())).Status);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.GrepFtsAsync(FtsRequest(), cancelled.Token));
    }

    [TestMethod]
    public async Task Fts_IndexFailureReturnsDiagnostic_NotEmptySuccess()
    {
        await using var db = await CreateScopeAsync();
        using var files = new FtsFiles();
        db.Db.ConversationCatalogs.Add(new ConversationCatalogEntity { ConversationId = "mine", WorkspaceId = "ws", AgentId = "agent" });
        await db.Db.SaveChangesAsync();
        files.Write("2026-09-14", "mine", "needle");
        var service = new RawSessionLogService(db.Factory, new FailingIndex(), files.Paths);
        var result = await service.GrepFtsAsync(FtsRequest());
        Assert.AreEqual("unavailable", result.Status);
        Assert.AreEqual("index unavailable", result.Error);
        Assert.AreEqual("partial", result.Coverage);
    }

    private static RawSessionLogSearchRequest FtsRequest() => new()
    { WorkspaceId = "ws", AgentInstanceId = "agent", Query = "needle", Day = "2026-09-14" };

    private sealed class FailingIndex : IFullTextSearchEngine
    {
        public bool HasIndex(string directoryPath) => true;
        public bool RemoveIndex(string directoryPath) => throw new NotSupportedException();
        public Task<FullTextIndexResult> BuildIndexAsync(string directoryPath, string? filePatterns = null, CancellationToken ct = default)
            => Task.FromResult(new FullTextIndexResult(false, 0, 0, 0, "index unavailable"));
        public Task<FullTextSearchResult> SearchAsync(string query, string directoryPath, int maxResults = 30,
            string? fileExtensionFilter = null, string? subDirectoryFilter = null, CancellationToken ct = default, FullTextSearchScope? scope = null)
            => throw new AssertFailedException("Must not search after refresh failure.");
    }

    private sealed class FtsFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pudding-history-fts-" + Guid.NewGuid().ToString("N"));
        public PuddingDataPaths Paths { get; }
        public LuceneSearchEngine Engine { get; }
        public FtsFiles()
        {
            Paths = PuddingDataPaths.FromRoot(_root);
            Engine = new LuceneSearchEngine(new FullTextIndexOptions { IndexRootDirectory = Path.Combine(_root, "index") },
                new StandardAnalyzer(LuceneVersion.LUCENE_48), [new PlainTextExtractor()]);
        }
        public string File(string day, string session) => Path.Combine(Paths.AgentInstanceMessageLogsRoot("agent"), day, session + ".md");
        public void Write(string day, string session, string text)
        {
            var path = File(day, session);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, text);
        }
        public void Dispose()
        {
            Engine.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
