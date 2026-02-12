using System;
using Avalonia.Threading;

namespace PuddingAssistantDesktop.Heartbeat;

/// <summary>
/// Central three-tier heartbeat coordinator that drives the pudding spirit's lifecycle.
/// Replaces scattered DispatcherTimers with a unified pulse architecture.
/// <para>
/// <b>Tier 1 — Physics Beat (60 Hz):</b> Rendering, Verlet integration, squash/stretch.<br/>
/// <b>Tier 2 — Perception Beat (2 Hz):</b> Environment scanning, idle detection, window metadata.<br/>
/// <b>Tier 3 — Consciousness Beat (60 s):</b> Day-period, memory-driven intent, autonomous behavior.
/// </para>
/// </summary>
public sealed class HeartbeatCoordinator : IDisposable
{
    private readonly DispatcherTimer _physicsTicker;
    private readonly DispatcherTimer _perceptionTicker;
    private readonly DispatcherTimer _consciousnessTicker;
    private bool _disposed;

    /// <summary>Running beat counter for the physics tier (wraps at 2π for animation phase).</summary>
    public double PhysicsPhase { get; private set; }

    /// <summary>Running beat counter for the perception tier.</summary>
    public long PerceptionBeatCount { get; private set; }

    /// <summary>Running beat counter for the consciousness tier.</summary>
    public long ConsciousnessBeatCount { get; private set; }

    /// <summary>Whether the heartbeat is currently running.</summary>
    public bool IsAlive { get; private set; }

    // ── Tier 1: Physics Beat (60 Hz) ──

    /// <summary>
    /// Fires ~60 times per second. Drives rendering, physics integration, and animation phase.
    /// <para>Parameter: delta time in seconds (typically 1/60).</para>
    /// </summary>
    public event Action<double>? PhysicsBeat;

    // ── Tier 2: Perception Beat (2 Hz) ──

    /// <summary>
    /// Fires ~2 times per second. Drives environment scanning, idle checks, and window sensing.
    /// </summary>
    public event Action? PerceptionBeat;

    // ── Tier 3: Consciousness Beat (60 s) ──

    /// <summary>
    /// Fires every ~60 seconds. Drives day-period updates, memory archival, and autonomous intent.
    /// </summary>
    public event Action? ConsciousnessBeat;

    public HeartbeatCoordinator()
    {
        _physicsTicker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _physicsTicker.Tick += OnPhysicsTick;

        _perceptionTicker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _perceptionTicker.Tick += OnPerceptionTick;

        _consciousnessTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _consciousnessTicker.Tick += OnConsciousnessTick;
    }

    /// <summary>Starts all three heartbeat tiers.</summary>
    public void Start()
    {
        if (IsAlive) return;
        IsAlive = true;
        _physicsTicker.Start();
        _perceptionTicker.Start();
        _consciousnessTicker.Start();

        // Fire an immediate consciousness beat on start (day-period, greeting, etc.)
        ConsciousnessBeat?.Invoke();
    }

    /// <summary>Stops all heartbeat tiers (hibernation).</summary>
    public void Stop()
    {
        IsAlive = false;
        _physicsTicker.Stop();
        _perceptionTicker.Stop();
        _consciousnessTicker.Stop();
    }

    /// <summary>
    /// Enters low-power mode: physics drops to 30 Hz, perception to 0.5 Hz, consciousness unchanged.
    /// </summary>
    public void EnterLowPowerMode()
    {
        _physicsTicker.Interval = TimeSpan.FromMilliseconds(33); // ~30fps
        _perceptionTicker.Interval = TimeSpan.FromSeconds(2);     // 0.5 Hz
    }

    /// <summary>Restores normal heartbeat frequencies.</summary>
    public void RestoreNormalMode()
    {
        _physicsTicker.Interval = TimeSpan.FromMilliseconds(16); // ~60fps
        _perceptionTicker.Interval = TimeSpan.FromMilliseconds(500); // 2 Hz
    }

    // ── Tick handlers ──

    private void OnPhysicsTick(object? sender, EventArgs e)
    {
        var dt = _physicsTicker.Interval.TotalSeconds;

        PhysicsPhase += 0.05; // ~3 rad/sec at 60fps
        if (PhysicsPhase > Math.PI * 2)
            PhysicsPhase -= Math.PI * 2;

        PhysicsBeat?.Invoke(dt);
    }

    private void OnPerceptionTick(object? sender, EventArgs e)
    {
        PerceptionBeatCount++;
        PerceptionBeat?.Invoke();
    }

    private void OnConsciousnessTick(object? sender, EventArgs e)
    {
        ConsciousnessBeatCount++;
        ConsciousnessBeat?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
