using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services;
using PuddingCodeIndex.Services.CodeIndex;
using PuddingCodeIndexTests.Services;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// Shared harness for the change-pipeline driver tests: a real scheduler, the real SQLite store and a
/// fake change source, wired the same way the component wires them in production
/// (<c>CodeIndexMaintenanceService</c> as the single driver that pumps the scheduler).
/// </summary>
internal sealed class MaintenanceHarness : IDisposable
{
    public MaintenanceHarness(
        TimeSpan? pollInterval = null,
        TimeSpan? stopTimeout = null,
        int queueCapacity = CodeIndexChangeQueue.DefaultCapacity,
        ICodeIndexer? indexer = null,
        ILogger<CodeIndexMaintenanceService>? logger = null,
        int maxRemovalsPerRun = CodeIndexCalibrationService.DefaultMaxRemovalsPerRun)
    {
        Fixture = CodeIndexFixture.Create();
        Clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero));
        Indexer = new RecordingCodeIndexer();
        var effectiveIndexer = indexer ?? (ICodeIndexer)Indexer;
        var effectiveLogger = logger ?? NullLogger<CodeIndexMaintenanceService>.Instance;
        Scheduler = new CodeIndexScheduler(
            effectiveIndexer,
            new DefaultCodeWorkspaceResolver(Fixture.Store),
            Fixture.Store,
            NullLogger<CodeIndexScheduler>.Instance);
        WatcherFactory = new FakeCodeIndexWatcherFactory();
        Calibration = new CodeIndexCalibrationService(
            Fixture.Store,
            Clock,
            maxRemovalsPerRun: maxRemovalsPerRun,
            logger: effectiveLogger);
        Service = new CodeIndexMaintenanceService(
            Scheduler,
            WatcherFactory,
            Fixture.Store,
            effectiveIndexer,
            new DefaultCodeWorkspaceResolver(Fixture.Store),
            effectiveLogger,
            Clock,
            queueCapacity: queueCapacity,
            pollInterval: pollInterval ?? TimeSpan.FromHours(1),
            stopTimeout: stopTimeout ?? CodeIndexMaintenanceService.DefaultStopTimeout,
            calibration: Calibration);
    }

    public CodeIndexFixture Fixture { get; }

    public MutableTimeProvider Clock { get; }

    public RecordingCodeIndexer Indexer { get; }

    public CodeIndexScheduler Scheduler { get; }

    public FakeCodeIndexWatcherFactory WatcherFactory { get; }

    /// <summary>Calibration the driver under test drives (same store, same clock), exposed for direct use.</summary>
    public CodeIndexCalibrationService Calibration { get; }

    public CodeIndexMaintenanceService Service { get; }

    public string Root => Fixture.Root;

    /// <summary>Store the driver writes through — the same instance the indexer would persist to.</summary>
    public ICodeIndexStore Store => Fixture.Store;

    public FakeCodeIndexChangeWatcher Watcher => WatcherFactory.Watcher;

    /// <summary>Absolute path of a scope-relative file (<c>/</c> and <c>\</c> both accepted).</summary>
    public string Combine(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Attaches the single test scope and starts its (fake) change source.
    /// </summary>
    /// <param name="scopeRootPath">
    /// Root the scope is attached at; defaults to <see cref="Root"/>. Passing a path that does not exist
    /// simulates "the scope root is not mounted / was renamed away any more" (U3-C refused-sweep case)
    /// without moving the SQLite file of the fixture.
    /// </param>
    public async Task StartWithActiveScopeAsync(string? scopeRootPath = null)
    {
        var root = scopeRootPath ?? Root;

        await Fixture.Store.UpsertProjectAsync(
            new CodeProjectRecord(
                MaintenanceTestData.WorkspaceId,
                MaintenanceTestData.ScopeId,
                root,
                CodeProjectStatus.Active));

        await Service.StartAsync();
        Assert.IsTrue(Service.EnsureScope(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, root));
        Assert.IsTrue(Watcher.Started);
    }

    /// <summary>Publishes an observation the way the real watcher callback does.</summary>
    public bool PublishChange(string relativePath, IndexChangeKind kind, string? oldRelativePath = null)
    {
        var observedAt = Clock.GetUtcNow();
        var sequence = Watcher.State.NextSequence();
        Watcher.State.MarkObserved(observedAt);

        return Watcher.Queue.TryPublish(
            MaintenanceTestData.Change(Root, relativePath, kind, sequence, observedAt, oldRelativePath));
    }

    public void AdvancePastDebounce() => Clock.Advance(TimeSpan.FromSeconds(3));

    public CodeIndexMaintenanceScopeStatus Status() =>
        Service.GetScopeStatus(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId)
        ?? throw new InvalidOperationException("the scope is not attached");

    /// <summary>
    /// Persists a file record plus one symbol for it — the rows a real indexer run would have written —
    /// so a test can start from "this file is indexed" without running a language indexer.
    /// </summary>
    public Task SeedIndexedFileAsync(
        string relativePath,
        string symbolName,
        string language = "C#",
        CodeSymbolKind symbolKind = CodeSymbolKind.Class) =>
        SeedIndexedAbsoluteFileAsync(Combine(relativePath), symbolName, language, symbolKind);

    /// <summary>
    /// Same as <see cref="SeedIndexedFileAsync"/> but for an absolute path, so a test can seed rows for a
    /// scope whose root is not <see cref="Root"/> (for example a root that does not exist).
    /// </summary>
    public async Task SeedIndexedAbsoluteFileAsync(
        string filePath,
        string symbolName,
        string language = "C#",
        CodeSymbolKind symbolKind = CodeSymbolKind.Class)
    {
        await Fixture.Store.UpsertFilesAsync(
            MaintenanceTestData.WorkspaceId,
            MaintenanceTestData.ScopeId,
            [new CodeFileRecord(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, filePath, language, Clock.GetUtcNow())]);

        await Fixture.Store.UpsertSymbolsAsync(
            MaintenanceTestData.WorkspaceId,
            MaintenanceTestData.ScopeId,
            [
                new CodeSymbolRecord(
                    MaintenanceTestData.WorkspaceId,
                    MaintenanceTestData.ScopeId,
                    filePath,
                    $"seed:{filePath}:{symbolName}",
                    symbolName,
                    symbolKind,
                    1,
                    5,
                    $"{symbolKind} {symbolName}",
                    Container: null),
            ]);
    }

    /// <summary>Symbols the index answers for a name — the observable the retrieval check asserts on.</summary>
    public Task<IReadOnlyList<CodeSymbolRecord>> SearchSymbolsAsync(string query) =>
        Fixture.Store.SearchSymbolsAsync(
            new CodeSymbolSearchRequest(MaintenanceTestData.WorkspaceId, query, MaintenanceTestData.ScopeId));

    public void Dispose()
    {
        Service.Dispose();
        Scheduler.Dispose();
        Fixture.Dispose();
    }
}
