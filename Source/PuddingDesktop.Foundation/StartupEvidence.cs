using System.Reflection;

namespace PuddingDesktop.Foundation;

/// <summary>
/// A startup fact the user can act on. Milestones are recorded at most once per attempt; a later
/// milestone never implies an earlier one, so a report shows what was actually reached instead of
/// printing an optimistic percentage.
/// </summary>
public enum StartupMilestone
{
    /// <summary>The shell window is activated: the user can see and navigate the product.</summary>
    ShellVisible = 1,

    /// <summary>The role directory answers: the sidebar can list real roles for the current data root.</summary>
    DirectoryReadable = 2,

    /// <summary>A conversation was read: history is on screen, not merely listed.</summary>
    ConversationReadable = 3,

    /// <summary>Core finished starting its hosted services: execution may be requested.</summary>
    ExecutionReady = 4,
}

public enum StartupAttemptOutcome { Running = 0, Succeeded = 1, Failed = 2, Cancelled = 3 }

public enum StartupPhaseOutcome
{
    Running = 0,

    /// <summary>The step reported completion.</summary>
    Completed = 1,

    /// <summary>The step ended without reporting completion: it threw, or the attempt was finalized first.</summary>
    Aborted = 2,

    /// <summary>The step failed or did not apply and the host deliberately continued without it.</summary>
    Skipped = 3,
}

/// <summary>
/// Phase names shared by Desktop, Composition and Host. A report reads these names, so they are
/// constants instead of literals repeated at each call site. Names never carry paths or content.
/// </summary>
public static class StartupPhases
{
    public const string DesktopShellVisible = "desktop.shell.visible";
    public const string DesktopSettingsLoad = "desktop.settings.load";
    public const string DesktopPanelsBuild = "desktop.panels.build";
    public const string DesktopDirectoryRead = "desktop.directory.read";
    public const string DesktopChatMount = "desktop.chat.mount";
    public const string DesktopConversationRead = "desktop.conversation.read";

    public const string KernelGate = "kernel.gate";
    public const string KernelCoreStart = "kernel.core-start";

    public const string HostDataRootLease = "host.data-root-lease";
    public const string HostBuilder = "host.builder";
    public const string HostBuild = "host.build";
    public const string HostBuildContainer = "host.build.container";
    public const string HostBuildEndpoints = "host.build.endpoints";
    public const string HostInitialize = "host.initialize";
    public const string HostStart = "host.start";

    public const string HostPlatformSchema = "host.initialize.platform-schema";
    public const string HostPlatformSchemaStepPrefix = "host.initialize.platform-schema.";
    public const string HostPlatformSchemaMarker = "host.initialize.platform-schema.marker";
    public const string HostPlatformSchemaStamp = "host.initialize.platform-schema.stamp";
    public const string HostEventStore = "host.initialize.event-store";
    public const string HostGoalReconcile = "host.initialize.goal-reconcile";
    public const string HostExternalApiConfig = "host.initialize.external-api-config";
    public const string HostMemoryDbCore = "host.initialize.memory-db.core";
    public const string HostMemoryDbLibrary = "host.initialize.memory-db.library";
    public const string HostWorkspaceCatalog = "host.initialize.workspace-catalog";
    public const string HostJiebaBackfill = "host.initialize.jieba-backfill";

    /// <summary>Data-scale metrics the report correlates with duration; never content.</summary>
    public const string MetricWorkspaceCount = "workspace.count";
    public const string MetricSchemaStepCount = "platform-schema.step-count";
    public const string MetricSchemaRevision = "platform-schema.revision";
    public const string MetricSchemaLadderSkipped = "platform-schema.ladder-skipped";

    /// <summary>1 when a conversation was readable inside the startup window, 0 when it was not.</summary>
    public const string MetricConversationFirstRead = "conversation.first-read";

    /// <summary>1 when the process start time could not be read, so the timeline anchor is the attempt.</summary>
    public const string MetricProcessStartUnknown = "clock.process-start-unknown";
}

public sealed record StartupPhaseRecord(
    string Name,
    int Sequence,
    int ThreadId,
    double AtMs,
    double DurationMs,
    StartupPhaseOutcome Outcome,
    string? Detail);

public sealed record StartupMilestoneRecord(StartupMilestone Milestone, double AtMs, string? Detail);

public sealed record StartupMetricRecord(string Name, long Value, double AtMs);

/// <summary>
/// Evidence for one startup attempt. <see cref="StartupPhaseRecord.AtMs"/> and
/// <see cref="StartupMilestoneRecord.AtMs"/> are relative to <see cref="StartedAtUtc"/>; add
/// <see cref="SinceProcessStartMs"/> to place a fact on the process timeline, which matters for a
/// fact observed before the attempt object existed (for example the first shell frame).
/// </summary>
public sealed record StartupEvidence(
    string AttemptId,
    int ProcessId,
    string BuildVersion,
    string DataRoot,
    DateTimeOffset ProcessStartedAtUtc,
    DateTimeOffset StartedAtUtc,
    double SinceProcessStartMs,
    double TotalMs,
    StartupAttemptOutcome Outcome,
    string? FailureType,
    IReadOnlyList<StartupPhaseRecord> Phases,
    IReadOnlyList<StartupMilestoneRecord> Milestones,
    IReadOnlyList<StartupMetricRecord> Metrics,
    IReadOnlyList<string> Violations);

/// <summary>One phase in flight. Settling it twice keeps the first outcome and records a violation.</summary>
public interface IStartupPhase : IDisposable
{
    void Complete(string? detail = null);
    void Skip(string reason);
}

/// <summary>
/// Records what the Desktop and the in-process Core actually did, in order, with durations. It holds
/// names, timings and counts only: never secrets, connection strings or session content.
/// </summary>
public interface IStartupAttempt
{
    string AttemptId { get; }

    /// <summary>Raised for every settled phase so the shell can show the current stage honestly.</summary>
    event EventHandler<StartupPhaseRecord>? PhaseRecorded;

    IStartupPhase Phase(string name);
    void Milestone(StartupMilestone milestone, string? detail = null, DateTimeOffset? occurredAt = null);
    void Metric(string name, long value);

    /// <summary>The frozen evidence, or null while the attempt is still running.</summary>
    StartupEvidence? Evidence { get; }

    StartupEvidence Complete();
    StartupEvidence Fail(string failureType);
    StartupEvidence Cancel();
}

/// <summary>Where an attempt came from. Explicit so tests do not depend on the running process.</summary>
public sealed record StartupAttemptOrigin(
    string AttemptId,
    int ProcessId,
    string BuildVersion,
    DateTimeOffset ProcessStartedAtUtc)
{
    /// <summary>
    /// Builds an origin for the running process. <paramref name="processStartedAtUtc"/> is required
    /// because reading the real process start time needs APIs this UI-free leaf does not reference:
    /// the Desktop supplies it, and a caller that cannot must not pass "now" as if it were measured.
    /// </summary>
    public static StartupAttemptOrigin Capture(DateTimeOffset processStartedAtUtc)
        => new(Guid.NewGuid().ToString("N"), Environment.ProcessId, RunningBuildVersion(), processStartedAtUtc);

    /// <summary>The running build, or an explicit "unknown" when the build wrote no version metadata.</summary>
    public static string RunningBuildVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(StartupAttemptOrigin).Assembly;
        return DesktopProductInfo.NormalizeVersion(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version);
    }
}

public static class StartupAttempts
{
    /// <summary>
    /// Starts an attempt. Without an explicit origin the process-start anchor is the attempt itself;
    /// production callers pass <see cref="StartupAttemptOrigin.Capture"/> with the real start time.
    /// </summary>
    public static IStartupAttempt Begin(string dataRoot, StartupAttemptOrigin? origin = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var clock = time ?? TimeProvider.System;
        return new StartupAttempt(dataRoot, origin ?? StartupAttemptOrigin.Capture(clock.GetUtcNow()), clock);
    }
}

internal sealed class StartupAttempt : IStartupAttempt
{
    // Evidence stays bounded: a runaway loop must not turn the artifact into an unbounded log.
    private const int MaxPhases = 512;
    private const int MaxMetrics = 64;
    private const int MaxViolations = 32;

    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private readonly StartupAttemptOrigin _origin;
    private readonly DateTimeOffset _startedAtUtc;
    private readonly List<StartupPhaseRecord> _phases = [];
    private readonly List<StartupMilestoneRecord> _milestones = [];
    private readonly List<StartupMetricRecord> _metrics = [];
    private readonly List<string> _violations = [];
    private readonly HashSet<StartupMilestone> _reached = [];
    private readonly List<PhaseScope> _open = [];
    private int _sequence;
    private StartupEvidence? _evidence;

    public StartupAttempt(string dataRoot, StartupAttemptOrigin origin, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin.AttemptId);
        DataRoot = dataRoot;
        _origin = origin;
        _time = time;
        _startedAtUtc = time.GetUtcNow();
    }

    public string AttemptId => _origin.AttemptId;
    public string DataRoot { get; }
    public event EventHandler<StartupPhaseRecord>? PhaseRecorded;
    public StartupEvidence? Evidence { get { lock (_sync) return _evidence; } }

    public IStartupPhase Phase(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_sync)
        {
            if (_evidence is not null)
            {
                Violate($"phase '{name}' arrived after the attempt was finalized; it was not recorded");
                return new IgnoredPhase();
            }
            var phase = new PhaseScope(this, name, _time.GetUtcNow(), _time.GetTimestamp());
            _open.Add(phase);
            return phase;
        }
    }

    public void Milestone(StartupMilestone milestone, string? detail = null, DateTimeOffset? occurredAt = null)
    {
        if (!Enum.IsDefined(milestone)) throw new ArgumentOutOfRangeException(nameof(milestone), milestone, "Unknown startup milestone.");
        lock (_sync)
        {
            if (_evidence is not null)
            {
                Violate($"milestone {milestone} arrived after the attempt was finalized; it was not recorded");
                return;
            }
            if (!_reached.Add(milestone))
            {
                Violate($"milestone {milestone} was reported more than once; the first report stands");
                return;
            }
            _milestones.Add(new StartupMilestoneRecord(milestone, Offset(occurredAt ?? _time.GetUtcNow()), detail));
        }
    }

    public void Metric(string name, long value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_sync)
        {
            if (_evidence is not null) { Violate($"metric '{name}' arrived after the attempt was finalized; it was not recorded"); return; }
            if (_metrics.Count >= MaxMetrics) { Violate($"metric '{name}' exceeded the {MaxMetrics} metric limit; it was not recorded"); return; }
            _metrics.Add(new StartupMetricRecord(name, value, Offset(_time.GetUtcNow())));
        }
    }

    public StartupEvidence Complete() => Finalize(StartupAttemptOutcome.Succeeded, null);
    public StartupEvidence Fail(string failureType) => Finalize(StartupAttemptOutcome.Failed, failureType);
    public StartupEvidence Cancel() => Finalize(StartupAttemptOutcome.Cancelled, null);

    private StartupEvidence Finalize(StartupAttemptOutcome outcome, string? failureType)
    {
        if (outcome == StartupAttemptOutcome.Running)
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A finalized attempt cannot be Running.");
        StartupEvidence evidence;
        lock (_sync)
        {
            if (_evidence is not null)
            {
                if (_evidence.Outcome != outcome)
                    Violate($"attempt was finalized as {_evidence.Outcome} and then as {outcome}; the first outcome stands");
                return _evidence;
            }
            var finalizedAt = _time.GetUtcNow();
            var finalizedTick = _time.GetTimestamp();
            // Whatever is still open died with the attempt: say so instead of dropping the phase.
            foreach (var open in _open.ToArray())
                Settle(open, finalizedAt, finalizedTick, StartupPhaseOutcome.Aborted, "阶段在完成前结束（异常或提前定稿）");
            _open.Clear();
            evidence = new StartupEvidence(
                _origin.AttemptId,
                _origin.ProcessId,
                _origin.BuildVersion,
                DataRoot,
                _origin.ProcessStartedAtUtc,
                _startedAtUtc,
                (_startedAtUtc - _origin.ProcessStartedAtUtc).TotalMilliseconds,
                (finalizedAt - _startedAtUtc).TotalMilliseconds,
                outcome,
                failureType,
                // Read-only live views: a fact reported after finalize (a late phase, a misuse) is
                // still visible to whoever reads the evidence, and no caller can mutate these lists.
                _phases.AsReadOnly(),
                _milestones.AsReadOnly(),
                _metrics.AsReadOnly(),
                _violations.AsReadOnly());
            _evidence = evidence;
        }
        return evidence;
    }

    /// <summary>Settles one phase. The lock is reentrant, so finalize may call it while holding it.</summary>
    private void Settle(PhaseScope phase, DateTimeOffset endUtc, long endTick, StartupPhaseOutcome outcome, string? detail)
    {
        StartupPhaseRecord? record = null;
        lock (_sync)
        {
            if (!_open.Remove(phase)) return;
            if (_phases.Count >= MaxPhases)
            {
                Violate($"phase '{phase.Name}' exceeded the {MaxPhases} phase limit; it was not recorded");
                return;
            }
            record = new StartupPhaseRecord(
                phase.Name,
                ++_sequence,
                Environment.CurrentManagedThreadId,
                Offset(phase.StartUtc),
                (endTick - phase.StartTick) * 1000.0 / _time.TimestampFrequency,
                outcome,
                detail);
            _phases.Add(record);
        }
        PhaseRecorded?.Invoke(this, record);
    }

    private void Violate(string violation)
    {
        if (_violations.Count >= MaxViolations) return;
        _violations.Add(violation);
    }

    private double Offset(DateTimeOffset atUtc) => (atUtc - _startedAtUtc).TotalMilliseconds;

    private sealed class PhaseScope(StartupAttempt owner, string name, DateTimeOffset startUtc, long startTick) : IStartupPhase
    {
        private int _settled;
        public string Name => name;
        public DateTimeOffset StartUtc => startUtc;
        public long StartTick => startTick;

        public void Complete(string? detail = null) => Settle(StartupPhaseOutcome.Completed, detail);
        public void Skip(string reason) => Settle(StartupPhaseOutcome.Skipped, reason);

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _settled, 2, 0) == 0)
                owner.Settle(this, owner._time.GetUtcNow(), owner._time.GetTimestamp(), StartupPhaseOutcome.Aborted,
                    "阶段在完成前结束（异常或提前返回）");
        }

        private void Settle(StartupPhaseOutcome outcome, string? detail)
        {
            var previous = Interlocked.CompareExchange(ref _settled, 1, 0);
            if (previous == 0)
            {
                owner.Settle(this, owner._time.GetUtcNow(), owner._time.GetTimestamp(), outcome, detail);
                return;
            }
            if (previous == 1) owner.Violate($"phase '{name}' was settled twice; the first outcome stands");
            else owner.Violate($"phase '{name}' was settled after it was disposed; the outcome was ignored");
        }
    }

    /// <summary>Returned once the attempt is finalized, so a late caller cannot mutate frozen evidence.</summary>
    private sealed class IgnoredPhase : IStartupPhase
    {
        public void Complete(string? detail = null) { }
        public void Skip(string reason) { }
        public void Dispose() { }
    }
}
