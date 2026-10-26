using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PuddingDesktop.Guardian;

/// <summary>One serialized authority for build, stop, deployment, health acceptance and rollback.</summary>
public sealed class GuardianController
{
    private readonly GuardianSettings _settings;
    private readonly GuardianStore _store;
    private readonly IGuardianRuntime _runtime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;
    private readonly Queue<DateTimeOffset> _recoveries = new();
    private GuardianState _state;
    private int _failures;
    private DateTimeOffset _startupDeadline;
    private readonly bool _hasPreviousState;

    public GuardianController(GuardianSettings settings, GuardianStore store, IGuardianRuntime runtime, TimeProvider? time = null)
    {
        settings.Validate();
        _settings = settings; _store = store; _runtime = runtime; _time = time ?? TimeProvider.System;
        _hasPreviousState = File.Exists(store.StatePath);
        _state = store.Load();
        if (!File.Exists(store.StatePath) && !settings.AutoStartCore) _state = _state with { Phase = "stopped", Monitoring = false };
        _startupDeadline = _time.GetUtcNow().AddSeconds(settings.StartupTimeoutSeconds);
    }
    public GuardianState State => Volatile.Read(ref _state);
    public bool Busy => _gate.CurrentCount == 0;
    public Task? RunningOperation { get; private set; }

    public string Submit(GuardianCommand command, CancellationToken lifetime)
    {
        command.Validate();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, GuardianJson.Options))));
        if (State.RequestId == command.RequestId)
            return State.RequestFingerprint == fingerprint ? "duplicate" : "conflict";
        if (!_gate.Wait(0)) return "busy";
        try
        {
            if (State.Pending is not null && command.Action != "rollback") throw new InvalidOperationException("An interrupted deployment must be recovered first.");
            Write(State with { RequestId = command.RequestId, RequestFingerprint = fingerprint, Phase = "accepted", Error = null });
            RunningOperation = RunAcceptedAsync(command, lifetime);
            return "accepted";
        }
        catch { _gate.Release(); throw; }
    }

    private async Task RunAcceptedAsync(GuardianCommand command, CancellationToken ct)
    {
        try
        {
            // Let the HTTP 202 and the Agent's tool result leave Core before it is stopped.
            await Task.Delay(TimeSpan.FromSeconds(2), _time, ct);
            await ExecuteUnderLockAsync(command, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Write(State with { Phase = "interrupted", Error = "Guardian stopped; pending deployment will roll back on next start." });
        }
        catch (Exception ex)
        {
            var failure = ex.GetType().Name; // No credentials, command output or exception payload in status.
            if (State.Pending is not null)
            {
                try
                {
                    using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.StartupTimeoutSeconds + 3 * _settings.ShutdownTimeoutSeconds + 30));
                    await RollbackPendingAsync(recovery.Token);
                    Write(State with { Phase = "rolled-back", Error = "Deployment rejected: " + failure });
                }
                catch (Exception rollback)
                {
                    Write(State with { Phase = "faulted", Monitoring = false, Error = "Rollback failed: " + rollback.GetType().Name });
                }
            }
            else Write(State with { Phase = "failed", Error = failure });
        }
        finally { _gate.Release(); }
    }

    private async Task ExecuteUnderLockAsync(GuardianCommand command, CancellationToken ct)
    {
        if (command.Action == "stop")
        {
            Write(State with { Monitoring = false, Phase = "stopping" });
            await _runtime.StopAsync(command.Target, ct);
            Write(State with { Phase = "stopped" });
            return;
        }
        if (command.Action == "rollback")
        {
            if (State.Pending is not null) await RollbackPendingAsync(ct);
            else await RollbackFallbackAsync(ct);
            return;
        }
        var restartOnly = command.Action is "restart" or "start" || command.DeploymentMode == "restart-only";
        if (!restartOnly)
        {
            Write(State with { Phase = "building" });
            var directory = await _runtime.PrepareAsync(command, ct);
            var candidate = _store.Inspect(directory, command.Target);
            _store.Verify(candidate);
            Write(State with { PreparedDirectory = directory });
            if (command.Action == "build") { Write(State with { Phase = "built" }); return; }
            // A broken current version must never overwrite an existing known-good fallback.
            var before = await _runtime.ProbeAsync(ct);
            if (!before.Healthy) throw new InvalidOperationException("Current product is unhealthy; recover it before replacing assemblies.");
            var backup = _store.Capture(command.Target);
            Write(State with { Pending = new(backup, candidate), Phase = "deploying", Monitoring = true });
            await _runtime.StopAsync(command.Target, ct);
            _store.Deploy(State.Pending!);
        }
        else if (command.Action != "start") await _runtime.StopAsync(command.Target, ct);

        Write(State with { Phase = "starting", Monitoring = true });
        await _runtime.StartAsync(command.Target, command.Yolo, ct);
        Write(State); // Persist the replacement PID/start time before readiness polling.
        await WaitHealthyAsync(ct);
        Write(State with { Fallback = State.Pending?.Backup ?? State.Fallback, Pending = null, Phase = "healthy", Error = null });
        _store.Prune(State);
        _failures = 0;
    }

    public async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (State.Pending is not null) await RollbackPendingAsync(ct);
            else if (_hasPreviousState && State.Phase is "accepted" or "building" or "starting" or "interrupted")
                Write(State with { Phase = "interrupted", Error = "Previous command interrupted; no deployment is assumed complete." });
        }
        catch (Exception ex)
        {
            Write(State with { Phase = "faulted", Monitoring = false, Error = "Recovery failed: " + ex.GetType().Name });
        }
        finally { _gate.Release(); }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        if (!_gate.Wait(0)) return;
        try
        {
            if (!State.Monitoring)
            {
                // A deliberate stop stays stopped. An explicit UI start or fresh Desktop launch can re-arm supervision.
                if (State.Phase == "stopped" && (await _runtime.ProbeAsync(ct)).Healthy)
                    Write(State with { Monitoring = true, Phase = "healthy" });
                return;
            }
            var health = await _runtime.ProbeAsync(ct);
            if (health.IntentionalStop)
            {
                Write(State with { Phase = "stopped", Monitoring = false });
                return;
            }
            if (health.Healthy)
            {
                _failures = 0;
                if (State.Fallback is null)
                {
                    await WaitHealthyAsync(ct);
                    Write(State with { Fallback = _store.Capture("desktop"), Phase = "healthy" });
                }
                else if (State.Desktop != _runtime.Desktop || State.Core != _runtime.Core) Write(State);
                return;
            }
            if (_time.GetUtcNow() < _startupDeadline || ++_failures < _settings.FailureThreshold) return;
            var now = _time.GetUtcNow();
            while (_recoveries.TryPeek(out var prior) && now - prior > TimeSpan.FromMinutes(5)) _recoveries.Dequeue();
            if (_recoveries.Count >= 3)
            {
                Write(State with { Phase = "faulted", Monitoring = false, Error = "Recovery limit reached; manual repair required." });
                return;
            }
            _recoveries.Enqueue(now);
            if (State.Fallback is not null) await RollbackFallbackAsync(ct);
            else
            {
                Write(State with { Phase = "recovering", Error = "No healthy backup exists; restarting current binaries." });
                await _runtime.StopAsync("desktop", ct);
                await _runtime.StartAsync("desktop", false, ct);
                await WaitHealthyAsync(ct);
                Write(State with { Phase = "healthy", Fallback = _store.Capture("desktop"), Error = null });
            }
            _failures = 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Write(State with { Phase = "faulted", Monitoring = false, Error = "Health recovery failed: " + ex.GetType().Name });
        }
        finally { _gate.Release(); }
    }

    private async Task RollbackFallbackAsync(CancellationToken ct)
    {
        var backup = State.Fallback ?? throw new InvalidOperationException("No known-good backup exists.");
        var current = _store.Inspect(_settings.TargetDirectory(backup.Target), backup.Target);
        Write(State with { Pending = new(backup, current), Phase = "rolling-back", Monitoring = true });
        await RollbackPendingAsync(ct);
    }

    private async Task RollbackPendingAsync(CancellationToken ct)
    {
        var pending = State.Pending ?? throw new InvalidOperationException("Missing rollback transaction.");
        _store.Verify(pending.Backup);
        Write(State with { Phase = "rolling-back" });
        // A failed shell may also be holding Core; restarting the whole pair makes recovery unambiguous.
        await _runtime.StopAsync("desktop", ct);
        _store.Restore(pending);
        await _runtime.StartAsync("desktop", false, ct);
        Write(State);
        await WaitHealthyAsync(ct);
        Write(State with { Phase = "rolled-back", Pending = null, Fallback = pending.Backup, Monitoring = true });
        _store.Prune(State);
    }

    private async Task WaitHealthyAsync(CancellationToken ct)
    {
        var deadline = _time.GetUtcNow().AddSeconds(_settings.StartupTimeoutSeconds);
        DateTimeOffset? since = null;
        while (_time.GetUtcNow() < deadline)
        {
            var health = await _runtime.ProbeAsync(ct);
            if (health.IntentionalStop) throw new InvalidOperationException("Product deliberately stopped during acceptance.");
            var now = _time.GetUtcNow();
            if (health.Healthy)
            {
                since ??= now;
                if (now - since.Value >= TimeSpan.FromSeconds(_settings.StableSeconds)) return;
            }
            else since = null;
            await Task.Delay(TimeSpan.FromSeconds(_settings.PollSeconds), _time, ct);
        }
        throw new TimeoutException("Product did not pass the stable health window.");
    }

    private void Write(GuardianState state)
    {
        state = state with { Desktop = _runtime.Desktop, Core = _runtime.Core, UpdatedAt = _time.GetUtcNow() };
        _store.Save(state);
        Volatile.Write(ref _state, state);
    }
}
