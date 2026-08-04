using PuddingCode.Configuration;
using PuddingFullTextIndex.Contracts;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class AgentLogRecallServiceTests
{
    [TestMethod]
    public async Task RecallAsync_Searches_Message_And_Daily_Summary_Windows()
    {
        using var temp = new TempDataRoot();
        var now = new DateTimeOffset(2026, 6, 16, 10, 0, 0, TimeSpan.Zero);
        var engine = new FakeFullTextSearchEngine();
        var service = new AgentLogRecallService(temp.Paths, engine, () => now);
        var messageRoot = temp.Paths.AgentInstanceMessageLogsRoot("agent-1");
        var dailyRoot = temp.Paths.AgentInstanceDailySummaryRoot("agent-1");
        Directory.CreateDirectory(Path.Combine(messageRoot, "2026-06-15"));
        Directory.CreateDirectory(Path.Combine(messageRoot, "2026-06-01"));
        Directory.CreateDirectory(Path.Combine(dailyRoot));
        engine.Results[messageRoot] =
        [
            new FullTextSearchMatch(Path.Combine(messageRoot, "2026-06-15", "s1.md"), 3, "needle recent message"),
            new FullTextSearchMatch(Path.Combine(messageRoot, "2026-06-01", "s2.md"), 4, "needle older message"),
            new FullTextSearchMatch(Path.Combine(messageRoot, "2025-12-01", "s3.md"), 5, "needle out of range"),
        ];
        engine.Results[dailyRoot] =
        [
            new FullTextSearchMatch(Path.Combine(dailyRoot, "2026-06-10.md"), 1, "needle summary"),
            new FullTextSearchMatch(Path.Combine(dailyRoot, "2025-01-01.md"), 1, "needle old summary"),
        ];

        var result = await service.RecallAsync(new AgentLogRecallRequest(
            "agent-1",
            "needle",
            RecentFiveDaysMessageLimit: 20,
            RecentThirtyDaysMessageLimit: 10,
            RecentDailySummaryLimit: 10));

        Assert.AreEqual(1, result.RecentFiveDaysMessages.Count);
        Assert.AreEqual("2026-06-15", result.RecentFiveDaysMessages[0].Day);
        Assert.AreEqual(1, result.RecentThirtyDaysMessages.Count);
        Assert.IsTrue(result.RecentThirtyDaysMessages.Any(x => x.Day == "2026-06-01"));
        Assert.AreEqual(1, result.RecentDailySummaries.Count);
        Assert.AreEqual("2026-06-10", result.RecentDailySummaries[0].Day);
        CollectionAssert.Contains(engine.BuildDirectories, messageRoot);
        CollectionAssert.Contains(engine.BuildDirectories, dailyRoot);
        Assert.AreEqual(2, engine.SearchCalls.Count);
        Assert.IsTrue(engine.SearchCalls.All(x => x.Query == "needle"));
    }

    [TestMethod]
    public async Task RecallAsync_ThirtyDay_Window_Excludes_FiveDay_Window_Hits()
    {
        // Arrange: now = 2026-06-16
        //   5-day window covers 2026-06-12 through 2026-06-16 (earliest = today - 4)
        //   30-day window covers 2026-05-18 through 2026-06-16 (earliest = today - 29)
        //   Anything before 2026-05-18 is out of both windows.
        using var temp = new TempDataRoot();
        var now = new DateTimeOffset(2026, 6, 16, 10, 0, 0, TimeSpan.Zero);
        var engine = new FakeFullTextSearchEngine();
        var service = new AgentLogRecallService(temp.Paths, engine, () => now);
        var messageRoot = temp.Paths.AgentInstanceMessageLogsRoot("agent-1");
        Directory.CreateDirectory(messageRoot);

        engine.Results[messageRoot] =
        [
            // Day 1 ago ─ within 5-day window ─ should land in fiveDay, NOT thirtyDay
            new FullTextSearchMatch(Path.Combine(messageRoot, "2026-06-15", "d1.md"), 1, "hit within 5 days #1"),
            // Day 4 ago ─ within 5-day window
            new FullTextSearchMatch(Path.Combine(messageRoot, "2026-06-12", "d4.md"), 1, "hit within 5 days #2"),
            // Day 6 ago ─ within 30-day, outside 5-day
            new FullTextSearchMatch(Path.Combine(messageRoot, "2026-06-10", "d6.md"), 1, "hit only in 30 days #1"),
            // Day 15 ago ─ within 30-day, outside 5-day
            new FullTextSearchMatch(Path.Combine(messageRoot, "2026-06-01", "d15.md"), 1, "hit only in 30 days #2"),
            // Day 29 ago ─ within 30-day, outside 5-day (edge case)
            new FullTextSearchMatch(Path.Combine(messageRoot, "2026-05-18", "d29.md"), 1, "hit only in 30 days #3 edge"),
            // Beyond 30-day window ─ should appear in neither list
            new FullTextSearchMatch(Path.Combine(messageRoot, "2026-05-17", "d30.md"), 1, "out of range"),
        ];

        var request = new AgentLogRecallRequest(
            "agent-1",
            "hit",
            RecentFiveDaysMessageLimit: 20,
            RecentThirtyDaysMessageLimit: 10,
            RecentDailySummaryLimit: 0);

        // Act
        var result = await service.RecallAsync(request);

        // Assert ─ no overlap between the two lists
        var fiveDayDays = result.RecentFiveDaysMessages.Select(m => m.Day).ToHashSet();
        var thirtyDayDays = result.RecentThirtyDaysMessages.Select(m => m.Day).ToHashSet();

        Assert.AreEqual(2, result.RecentFiveDaysMessages.Count,
            "Two hits within 5 days should be in the five-day list.");
        CollectionAssert.Contains(fiveDayDays.ToList(), "2026-06-15");
        CollectionAssert.Contains(fiveDayDays.ToList(), "2026-06-12");

        Assert.AreEqual(3, result.RecentThirtyDaysMessages.Count,
            "Three hits in days 6-30 should be in the thirty-day list.");
        CollectionAssert.Contains(thirtyDayDays.ToList(), "2026-06-10");
        CollectionAssert.Contains(thirtyDayDays.ToList(), "2026-06-01");
        CollectionAssert.Contains(thirtyDayDays.ToList(), "2026-05-18");

        // No overlap
        Assert.IsFalse(fiveDayDays.Overlaps(thirtyDayDays),
            "Thirty-day list must not contain any entry whose Day also appears in the five-day list.");

        // No out-of-range entries
        Assert.IsFalse(fiveDayDays.Contains("2026-05-17"));
        Assert.IsFalse(thirtyDayDays.Contains("2026-05-17"));

        // Total distinct hits = union, nothing lost
        var allRecallDays = fiveDayDays.Concat(thirtyDayDays).ToHashSet();
        Assert.AreEqual(5, allRecallDays.Count,
            "Total distinct recall days across both windows must match the union of valid hits.");

        // Five-day entries all strictly within 5 days
        Assert.IsTrue(result.RecentFiveDaysMessages.All(m =>
            m.Day == "2026-06-15" || m.Day == "2026-06-12" || m.Day == "2026-06-16" || m.Day == "2026-06-13" || m.Day == "2026-06-14" || m.Day == "2026-06-12" || m.Day == "2026-06-15"));
    }

    [TestMethod]
    public async Task RecallAsync_Returns_Empty_When_Request_Is_Not_Searchable()
    {
        using var temp = new TempDataRoot();
        var engine = new FakeFullTextSearchEngine();
        var service = new AgentLogRecallService(temp.Paths, engine);

        var result = await service.RecallAsync(new AgentLogRecallRequest("agent-1", "   "));

        Assert.AreEqual(0, result.RecentFiveDaysMessages.Count);
        Assert.AreEqual(0, result.RecentThirtyDaysMessages.Count);
        Assert.AreEqual(0, result.RecentDailySummaries.Count);
        Assert.AreEqual(0, engine.SearchCalls.Count);
    }

    private sealed class FakeFullTextSearchEngine : IFullTextSearchEngine
    {
        public Dictionary<string, IReadOnlyList<FullTextSearchMatch>> Results { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> BuildDirectories { get; } = [];
        public List<(string Query, string Directory, int MaxResults)> SearchCalls { get; } = [];

        public bool HasIndex(string directoryPath) => false;

        public Task<FullTextSearchResult> SearchAsync(
            string query,
            string directoryPath,
            int maxResults = 30,
            string? fileExtensionFilter = null,
            string? subDirectoryFilter = null,
            CancellationToken ct = default)
        {
            SearchCalls.Add((query, directoryPath, maxResults));
            var matches = Results.TryGetValue(directoryPath, out var value)
                ? value.Take(maxResults).ToList()
                : [];
            return Task.FromResult(new FullTextSearchResult(true, matches, null, matches.Count, 1));
        }

        public Task<FullTextIndexResult> BuildIndexAsync(
            string directoryPath,
            string? filePatterns = null,
            CancellationToken ct = default)
        {
            BuildDirectories.Add(directoryPath);
            return Task.FromResult(new FullTextIndexResult(true, 1, 1, 1, null));
        }

        public bool RemoveIndex(string directoryPath) => true;
    }

    private sealed class TempDataRoot : IDisposable
    {
        public TempDataRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-agent-log-recall-tests", Guid.NewGuid().ToString("N"));
            Paths = PuddingDataPaths.FromRoot(Root);
        }

        public string Root { get; }
        public PuddingDataPaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
