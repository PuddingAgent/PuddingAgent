using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingRuntime.Services.Background;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class SubconsciousRuntimeControlServiceTests
{
    [TestMethod]
    public async Task StopAsync_ShouldPauseRuntimeAndReturnQueueDebugSnapshot()
    {
        var queue = new FakeSubconsciousJobQueue
        {
            Stats = new SubconsciousJobQueueStats
            {
                Pending = 2,
                Processing = 1,
            },
        };
        var diagnostics = new RecordingSubconsciousDiagnosticLog();
        var service = new SubconsciousRuntimeControlService(
            queue,
            Options.Create(new SubconsciousOptions
            {
                Scheduling = new SubconsciousSchedulingOptions
                {
                    Enabled = true,
                    DryRun = true,
                    IdleCooldownSeconds = 15,
                },
            }),
            diagnostics);

        var snapshot = await service.StopAsync(
            new SubconsciousRuntimeControlRequest
            {
                Reason = "manual debug",
                RequestedBy = "tester",
            });

        Assert.IsTrue(service.IsPaused);
        Assert.IsTrue(snapshot.IsPaused);
        Assert.AreEqual(SubconsciousRuntimeStates.Paused, snapshot.State);
        Assert.AreEqual("manual debug", snapshot.Reason);
        Assert.AreEqual("tester", snapshot.RequestedBy);
        Assert.AreEqual("stop", snapshot.LastCommand);
        Assert.AreEqual(2, snapshot.QueueStats.Pending);
        Assert.AreEqual("true", snapshot.Scheduling["dryRun"]);
        Assert.AreEqual("15", snapshot.Scheduling["idleCooldownSeconds"]);
        Assert.AreEqual("subconscious.control.stop", diagnostics.Events.Single().Name);
    }

    [TestMethod]
    public async Task StartAsync_ShouldResumeRuntimeAndRecordDiagnosticEvent()
    {
        var diagnostics = new RecordingSubconsciousDiagnosticLog();
        var service = new SubconsciousRuntimeControlService(
            new FakeSubconsciousJobQueue(),
            Options.Create(new SubconsciousOptions()),
            diagnostics);

        await service.StopAsync(new SubconsciousRuntimeControlRequest { Reason = "pause" });
        var snapshot = await service.StartAsync(
            new SubconsciousRuntimeControlRequest
            {
                Reason = "resume debug",
                RequestedBy = "tester",
            });

        Assert.IsFalse(service.IsPaused);
        Assert.IsFalse(snapshot.IsPaused);
        Assert.AreEqual(SubconsciousRuntimeStates.Running, snapshot.State);
        Assert.AreEqual("start", snapshot.LastCommand);
        CollectionAssert.AreEqual(
            new[] { "subconscious.control.stop", "subconscious.control.start" },
            diagnostics.Events.Select(e => e.Name).ToArray());
    }

    [TestMethod]
    public async Task DiagnosticLog_ShouldWriteSeparateRotatingJsonlFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-subconscious-diagnostics", Guid.NewGuid().ToString("N"));
        try
        {
            var log = new SubconsciousDiagnosticLog(
                PuddingDataPaths.FromRoot(root),
                Options.Create(new SubconsciousDiagnosticLogOptions
                {
                    MaxFileSizeBytes = 1,
                    RetainedFileCountLimit = 10,
                }));

            log.Write(
                "subconscious.control.stop",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["state"] = "paused",
                    ["reason"] = "manual debug",
                });
            log.Write(
                "subconscious.control.start",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["state"] = "running",
                });

            var logDir = Path.Combine(root, "logs", "diagnostics", "subconscious");
            var files = Directory.GetFiles(logDir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal).ToArray();

            Assert.IsTrue(files.Length >= 2);
            StringAssert.Contains(File.ReadAllText(files[0]), "subconscious.control.stop");
            StringAssert.Contains(File.ReadAllText(files[^1]), "subconscious.control.start");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingSubconsciousDiagnosticLog : ISubconsciousDiagnosticLog
    {
        public List<(string Name, IReadOnlyDictionary<string, object?> Fields)> Events { get; } = [];
        public string? LogDirectory => null;

        public void Write(string name, IReadOnlyDictionary<string, object?> fields)
            => Events.Add((name, fields));
    }

    private sealed class FakeSubconsciousJobQueue : ISubconsciousJobQueue
    {
        public SubconsciousJobQueueStats Stats { get; init; } = new();

        public Task<SubconsciousJobQueueItem> EnqueueAsync(
            SubconsciousJobEnqueueRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SubconsciousJobQueueItem?> LeaseNextAsync(
            string leaseOwner,
            TimeSpan leaseDuration,
            SubconsciousJobLeaseQuery? query = null,
            CancellationToken ct = default)
            => Task.FromResult<SubconsciousJobQueueItem?>(null);

        public Task<SubconsciousJobQueueStats> GetStatsAsync(CancellationToken ct = default)
            => Task.FromResult(Stats);

        public Task<SubconsciousJobQueueItem?> FindLatestAsync(
            SubconsciousJobLookupQuery query,
            CancellationToken ct = default)
            => Task.FromResult<SubconsciousJobQueueItem?>(null);

        public Task<IReadOnlyDictionary<string, int>> GetWorkspaceLeaseCountsAsync(
            DateTimeOffset since,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>(StringComparer.Ordinal));

        public Task RecordSchedulingSkipAsync(
            SubconsciousSchedulingSkipRequest request,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RecordResultAsync(
            string jobId,
            string leaseOwner,
            SubconsciousJobResultEnvelope result,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<SubconsciousJobResultEnvelope?> GetResultAsync(
            string jobId,
            CancellationToken ct = default)
            => Task.FromResult<SubconsciousJobResultEnvelope?>(null);

        public Task CompleteAsync(
            string jobId,
            string leaseOwner,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> RetryAsync(
            string jobId,
            string leaseOwner,
            string error,
            TimeSpan? retryDelay = null,
            CancellationToken ct = default)
            => Task.FromResult("retrying");

        public Task DeadLetterAsync(
            string jobId,
            string leaseOwner,
            string error,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
