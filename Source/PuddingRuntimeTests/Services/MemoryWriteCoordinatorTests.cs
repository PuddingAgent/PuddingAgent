using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class MemoryWriteCoordinatorTests
{
    [TestMethod]
    public async Task CoordinateAsync_ShouldReturnRejectedEnvelope_WhenCommandIsInvalid()
    {
        var coordinator = new MemoryWriteCoordinator(new MemoryWriteCommandValidator());
        var command = ValidAppendCommand() with { Payload = null };

        var envelope = await coordinator.CoordinateAsync(command, CancellationToken.None);

        Assert.AreEqual(MemoryWriteResultStatuses.Rejected, envelope.Status);
        Assert.AreEqual(MemoryWriteExecutionModes.DryRun, envelope.Mode);
        Assert.IsTrue(envelope.ErrorCodes.Contains(MemoryWriteValidationErrors.MissingRequiredField));
    }

    [TestMethod]
    public async Task CoordinateAsync_ShouldReturnDryRunEnvelope_WithoutWritingMemory()
    {
        var coordinator = new MemoryWriteCoordinator(new MemoryWriteCommandValidator());

        var envelope = await coordinator.CoordinateAsync(ValidAppendCommand(), CancellationToken.None);

        Assert.AreEqual(MemoryWriteResultStatuses.DryRun, envelope.Status);
        Assert.AreEqual(MemoryWriteIntents.AppendNew, envelope.Intent);
        Assert.AreEqual("cmd-1", envelope.CommandId);
        Assert.IsTrue(envelope.Metadata.ContainsKey("source_kind"));
    }

    [TestMethod]
    public async Task CoordinateAsync_ShouldEmitMemoryWriteMetric()
    {
        var metrics = new RecordingTelemetryMetricSink();
        var activities = new RecordingRuntimeActivitySink();
        var coordinator = new MemoryWriteCoordinator(
            new MemoryWriteCommandValidator(),
            activities,
            metrics);

        await coordinator.CoordinateAsync(ValidAppendCommand(), CancellationToken.None);

        Assert.IsTrue(metrics.Metrics.Any(e => e.Name == "memory_write.command"));
        Assert.IsTrue(activities.Activities.Any(e => e.Operation == "memory_write.dry_run"));
    }

    private static MemoryWriteCommand ValidAppendCommand() =>
        new()
        {
            CommandId = "cmd-1",
            WorkspaceId = "workspace-1",
            Intent = MemoryWriteIntents.AppendNew,
            Mode = MemoryWriteExecutionModes.DryRun,
            Source = new MemoryWriteSource
            {
                SourceKind = MemoryWriteSourceKinds.RuntimeTool,
                SessionId = "session-1",
            },
            Payload = new MemoryWritePayload
            {
                Title = "Preference",
                Content = "User prefers concise engineering summaries.",
                Confidence = 0.82,
            },
        };

    private sealed class RecordingTelemetryMetricSink : ITelemetryMetricSink
    {
        public List<TelemetryMetric> Metrics { get; } = [];

        public Task RecordAsync(TelemetryMetric metric, CancellationToken ct = default)
        {
            Metrics.Add(metric);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRuntimeActivitySink : IRuntimeActivitySink
    {
        public List<RuntimeActivity> Activities { get; } = [];

        public Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default)
        {
            Activities.Add(activity);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RuntimeActivity>> QueryAsync(
            RuntimeActivityQuery query,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RuntimeActivity>>(Activities);
    }

}
