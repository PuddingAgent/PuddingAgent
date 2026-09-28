using System.Reflection;
using PuddingDesktop.Composition;
using PuddingDesktop.Foundation;

namespace PuddingDesktop.CompositionTests;

/// <summary>
/// D1 startup evidence against a real in-process Core and an isolated DataRoot: the artifact the
/// Desktop writes must contain the phases and milestones a startup report depends on. This test never
/// touches the product DataRoot and never starts the Desktop shell.
/// </summary>
public sealed class StartupEvidenceCompositionTests
{
    [Fact]
    public async Task RealHostStartRecordsOrderedPhasesAndExecutionMilestone()
    {
        var root = Path.Combine(Path.GetTempPath(), "Pudding-startup-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var key = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(root, "config", "system.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Jwt = new { Key = key, Issuer = "startup-evidence", Audience = "startup-evidence" } }));
        await using var kernel = new InProcessKernel(new DesktopKernelFactory(new Desktop()));
        var attempt = StartupAttempts.Begin(root);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await kernel.StartAsync(root, timeout.Token, attempt);
            Assert.Equal(DesktopKernelState.Ready, kernel.Snapshot.State);
            attempt.Milestone(StartupMilestone.DirectoryReadable, "test fixture");
            var evidence = attempt.Complete();

            var names = evidence.Phases.Select(phase => phase.Name).ToList();
            // Both the kernel phases and the host phases must land in one attempt: that is what makes
            // a slow start attributable instead of a single opaque "startup took N seconds".
            Assert.Contains(StartupPhases.KernelGate, names);
            Assert.Contains(StartupPhases.KernelCoreStart, names);
            Assert.Contains(StartupPhases.HostDataRootLease, names);
            Assert.Contains(StartupPhases.HostBuilder, names);
            Assert.Contains(StartupPhases.HostBuild, names);
            Assert.Contains(StartupPhases.HostInitialize, names);
            Assert.Contains(StartupPhases.HostStart, names);
            Assert.Contains(StartupPhases.HostPlatformSchemaStepPrefix + "database", names);
            Assert.Contains(StartupPhases.HostMemoryDbCore, names);
            Assert.Contains(evidence.Milestones.Select(item => item.Milestone), item => item == StartupMilestone.ExecutionReady);
            Assert.Contains(evidence.Milestones.Select(item => item.Milestone), item => item == StartupMilestone.DirectoryReadable);
            Assert.Equal(StartupAttemptOutcome.Succeeded, evidence.Outcome);
            Assert.All(evidence.Phases, phase => Assert.True(phase.DurationMs >= 0, phase.Name));
            Assert.Empty(evidence.Violations);
            Assert.True(evidence.TotalMs >= evidence.Phases.Max(phase => phase.DurationMs));
            Assert.Contains(evidence.Metrics, metric => metric.Name == StartupPhases.MetricSchemaStepCount && metric.Value >= 20);
            Assert.Contains(evidence.Metrics, metric => metric.Name == StartupPhases.MetricWorkspaceCount);

            // Round trip through the same file sink the Desktop uses for a real startup.
            var sink = new StartupEvidenceFileSink(Path.Combine(root, "startup"));
            Assert.True(sink.TryWrite(evidence, out var error), error);
            var restored = System.Text.Json.JsonSerializer.Deserialize<StartupEvidence>(
                (await File.ReadAllLinesAsync(sink.FilePath)).Single(), StartupEvidenceJson.Options);
            Assert.NotNull(restored);
            Assert.Equal(evidence.AttemptId, restored.AttemptId);
            Assert.Equal(evidence.Phases.Count, restored.Phases.Count);

            // Opt-in only: keeps a real artifact for a report without making the test write outside
            // its own isolated root during normal runs.
            if (Environment.GetEnvironmentVariable("PUDDING_STARTUP_EVIDENCE_COPY") is { Length: > 0 } copyTo)
            {
                Directory.CreateDirectory(copyTo);
                File.Copy(sink.FilePath, Path.Combine(copyTo, $"startup-evidence-{evidence.AttemptId[..8]}.jsonl"), true);
            }
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HostPhaseVocabularyIsPartOfTheReportedNames()
    {
        // The host cannot reference the Desktop assembly, so the two vocabularies are separate copies.
        // This is the guard that fails when the host grows a name the report cannot place.
        var reported = ConstantStrings(typeof(StartupPhases));
        foreach (var name in ConstantStrings(typeof(PuddingHost.Hosting.StartupPhaseNames))
                     .Concat(ConstantStrings(typeof(PuddingHost.Hosting.StartupMetrics))))
            Assert.Contains(name, reported);
    }

    private static HashSet<string> ConstantStrings(Type type) => type
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToHashSet(StringComparer.Ordinal);

    private sealed class Desktop : IDesktopServices
    {
        public Task ShowAsync(ShellPage page, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task OpenDocumentAsync(WorkspaceDocument document, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
