using System;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PuddingAssistantDesktop.Heartbeat;

namespace PuddingAssistantDesktop.ViewModels;

/// <summary>Visual states of the desktop pudding spirit.</summary>
public enum SpiritState
{
    /// <summary>Calm matcha green, gentle breathing.</summary>
    Idle,
    /// <summary>Amber glow with ripple rotation, LLM/Swarm is working.</summary>
    Thinking,
    /// <summary>Sakura pink with blush and bounce, task succeeded.</summary>
    Happy,
    /// <summary>Lilac purple, radar-eye mode, scanning screen.</summary>
    Observing,
    /// <summary>Coral orange with tremor, needs user confirmation.</summary>
    Warning,
    /// <summary>Dim and flat, Zzz bubbles, long idle timeout.</summary>
    Sleeping,
    /// <summary>Bright yellow with >_< eyes, poked or shaken.</summary>
    Startled,
    /// <summary>Elongated vertically, currently in free-fall.</summary>
    Falling
}

/// <summary>Time-of-day period that influences pudding behavior.</summary>
public enum DayPeriod
{
    /// <summary>08:00–09:00: Waking up, rubbing eyes, morning greeting.</summary>
    Morning,
    /// <summary>09:00–13:00: Working mode, focused, small glasses.</summary>
    Working,
    /// <summary>13:00–14:00: Post-lunch drowsiness, yawning.</summary>
    Afternoon,
    /// <summary>14:00–23:00: Normal active period.</summary>
    Active,
    /// <summary>23:00–08:00: Late night, dim purple tint, rest reminders.</summary>
    LateNight
}

/// <summary>
/// ViewModel driving the desktop pudding spirit's visual state,
/// color palette, animation phase, and mouse interaction.
/// </summary>
public partial class SpiritViewModel : ViewModelBase
{
    // ── State ──

    [ObservableProperty] private SpiritState _state = SpiritState.Idle;

    /// <summary>Current time-of-day period driving ambient behavior.</summary>
    [ObservableProperty] private DayPeriod _dayPeriod = DayPeriod.Active;

    // ── Animation ──

    /// <summary>Continuously ticking phase in radians (0 → 2π loop).</summary>
    [ObservableProperty] private double _animationPhase;

    /// <summary>Squash factor (1.0 = normal, &lt;1 = squished, &gt;1 = stretched).</summary>
    [ObservableProperty] private double _squashY = 1.0;

    /// <summary>Stretch factor (inverse of squash for volume conservation).</summary>
    [ObservableProperty] private double _stretchX = 1.0;

    /// <summary>Opacity of the spirit (0.15 quiet → 0.95 active).</summary>
    [ObservableProperty] private double _spiritOpacity = 0.85;

    /// <summary>Whether physics engine is overriding squash/stretch (landing impact).</summary>
    [ObservableProperty] private bool _isPhysicsSquashing;

    /// <summary>Heartbeat glow intensity (0–1), pulses with the perception beat.</summary>
    [ObservableProperty] private double _heartbeatGlow;

    // ── Bubble ──

    /// <summary>Text shown in the speech bubble above the spirit. Empty = hidden.</summary>
    [ObservableProperty] private string _bubbleText = string.Empty;

    /// <summary>Opacity of the speech bubble (0–1). Fades out over time.</summary>
    [ObservableProperty] private double _bubbleOpacity;

    // ── Interaction ──

    [ObservableProperty] private bool _isPointerOver;
    [ObservableProperty] private bool _isDragging;

    // ── Colors (derived from state) ──

    [ObservableProperty] private Color _bodyColor;
    [ObservableProperty] private Color _glowColor;
    [ObservableProperty] private Color _shadowColor;

    // ── Heartbeat ──

    private readonly HeartbeatCoordinator _heartbeat;
    private readonly AutonomousBehavior _autonomy = new();
    private DateTime _lastInteraction = DateTime.UtcNow;

    /// <summary>External physics squash values set by the physics engine.</summary>
    private double _physicsSquashY = 1.0;
    private double _physicsStretchX = 1.0;

    /// <summary>The heartbeat coordinator driving this ViewModel.</summary>
    public HeartbeatCoordinator Heartbeat => _heartbeat;

    public SpiritViewModel(HeartbeatCoordinator heartbeat)
    {
        _heartbeat = heartbeat;
        ApplyPalette(SpiritState.Idle);

        // Subscribe to heartbeat tiers
        _heartbeat.PhysicsBeat += OnPhysicsBeat;
        _heartbeat.PerceptionBeat += OnPerceptionBeat;
        _heartbeat.ConsciousnessBeat += OnConsciousnessBeat;
    }

    partial void OnStateChanged(SpiritState value)
    {
        ApplyPalette(value);
    }

    partial void OnIsPointerOverChanged(bool value)
    {
        TouchInteraction();
        SpiritOpacity = value ? 0.95 : 0.85;

        // Wake up from sleeping if user hovers
        if (value && State == SpiritState.Sleeping)
            State = SpiritState.Idle;
    }

    /// <summary>Records interaction time to prevent idle sleep.</summary>
    public void TouchInteraction()
    {
        _lastInteraction = DateTime.UtcNow;
        if (State == SpiritState.Sleeping)
            State = SpiritState.Idle;
    }

    /// <summary>Trigger a happy bounce when a task succeeds.</summary>
    [RelayCommand]
    private void Celebrate()
    {
        var prev = State;
        State = SpiritState.Happy;
        RevertStateAfter(SpiritState.Happy, prev, TimeSpan.FromSeconds(2));
    }

    /// <summary>Trigger a startled reaction (poke, shake, or sudden event).</summary>
    public void Startle()
    {
        TouchInteraction();
        var prev = State;
        State = SpiritState.Startled;
        RevertStateAfter(SpiritState.Startled, prev, TimeSpan.FromSeconds(1.2));
    }

    /// <summary>Enter falling state while in free-fall.</summary>
    public void EnterFalling()
    {
        if (State is not SpiritState.Falling)
            State = SpiritState.Falling;
    }

    /// <summary>Exit falling state after landing.</summary>
    public void ExitFalling()
    {
        if (State == SpiritState.Falling)
            State = SpiritState.Idle;
    }

    /// <summary>Sets physics-driven squash/stretch that overrides breathing animation.</summary>
    public void SetPhysicsSquash(double squashY, double stretchX)
    {
        _physicsSquashY = squashY;
        _physicsStretchX = stretchX;
        IsPhysicsSquashing = Math.Abs(squashY - 1.0) > 0.01;
    }

    private void RevertStateAfter(SpiritState expected, SpiritState revertTo, TimeSpan delay)
    {
        var revert = new DispatcherTimer { Interval = delay };
        revert.Tick += (_, _) =>
        {
            revert.Stop();
            if (State == expected)
                State = revertTo == expected ? SpiritState.Idle : revertTo;
        };
        revert.Start();
    }

    /// <summary>Shows a speech bubble above the spirit that auto-fades after the given duration.</summary>
    public void ShowBubble(string text, TimeSpan? duration = null)
    {
        BubbleText = text;
        BubbleOpacity = 1.0;

        var fadeDelay = duration ?? TimeSpan.FromSeconds(4);
        var fadeTimer = new DispatcherTimer { Interval = fadeDelay };
        fadeTimer.Tick += (_, _) =>
        {
            fadeTimer.Stop();
            // Start fade-out (decayed each physics beat)
            _bubbleFading = true;
        };
        fadeTimer.Start();
        _bubbleFading = false;
    }

    private bool _bubbleFading;

    // ── Tier 1: Physics Beat (60 Hz) — animation + squash/stretch ──

    private void OnPhysicsBeat(double dt)
    {
        AnimationPhase = _heartbeat.PhysicsPhase;

        // Decay heartbeat glow
        HeartbeatGlow *= 0.95;

        // Decay bubble opacity when fading
        if (_bubbleFading && BubbleOpacity > 0.01)
        {
            BubbleOpacity *= 0.92;
            if (BubbleOpacity < 0.02)
            {
                BubbleOpacity = 0;
                BubbleText = string.Empty;
                _bubbleFading = false;
            }
        }

        // When physics is driving squash/stretch, use those values directly
        if (IsPhysicsSquashing)
        {
            SquashY = _physicsSquashY;
            StretchX = _physicsStretchX;
            return;
        }

        // Falling: elongate vertically
        if (State == SpiritState.Falling)
        {
            SquashY = 1.15;
            StretchX = 0.88;
            return;
        }

        // Startled: quick tremor
        if (State == SpiritState.Startled)
        {
            var tremor = Math.Sin(AnimationPhase * 12.0) * 0.06;
            SquashY = 0.9 + tremor;
            StretchX = 1.1 - tremor * 0.5;
            return;
        }

        // Breathing: gentle vertical oscillation
        var breathAmp = State switch
        {
            SpiritState.Sleeping => 0.02,
            SpiritState.Thinking => 0.04,
            _ => 0.03
        };

        var breathSpeed = State switch
        {
            SpiritState.Sleeping => 0.5,
            SpiritState.Thinking => 2.0,
            _ => 1.0
        };

        var breath = Math.Sin(AnimationPhase * breathSpeed) * breathAmp;
        SquashY = 1.0 + breath;
        StretchX = 1.0 - breath * 0.5; // volume conservation
    }

    // ── Tier 2: Perception Beat (2 Hz) — environment sensing ──

    /// <summary>Latest environment snapshot from the perception beat.</summary>
    public EnvironmentSnapshot? LatestSnapshot { get; private set; }

    private void OnPerceptionBeat()
    {
        // Pulse the heartbeat glow
        HeartbeatGlow = 0.6;

        // Capture environment
        LatestSnapshot = EnvironmentSnapshot.Capture(
            isGrounded: !IsDragging,
            isDragging: IsDragging);
    }

    // ── Tier 3: Consciousness Beat (60 s) — autonomous decisions ──

    private void OnConsciousnessBeat()
    {
        UpdateDayPeriod();

        // Evaluate autonomous behavior
        var snapshot = LatestSnapshot ?? EnvironmentSnapshot.Capture(isGrounded: true, isDragging: false);
        var action = _autonomy.Evaluate(snapshot, State, DayPeriod);

        if (action is null) return;

        switch (action.Intent)
        {
            case AutonomousIntent.MorningGreeting:
                State = SpiritState.Happy;
                RevertStateAfter(SpiritState.Happy, SpiritState.Idle, TimeSpan.FromSeconds(3));
                break;

            case AutonomousIntent.RestReminder:
                State = SpiritState.Warning;
                RevertStateAfter(SpiritState.Warning, SpiritState.Idle, TimeSpan.FromSeconds(5));
                break;

            case AutonomousIntent.Sleep:
                State = SpiritState.Sleeping;
                break;

            case AutonomousIntent.WakeUp:
                if (State == SpiritState.Sleeping)
                    State = SpiritState.Idle;
                break;
        }
    }

    // ── Time-of-day ──

    private void UpdateDayPeriod()
    {
        var hour = DateTime.Now.Hour;
        DayPeriod = hour switch
        {
            >= 8 and < 9 => DayPeriod.Morning,
            >= 9 and < 13 => DayPeriod.Working,
            >= 13 and < 14 => DayPeriod.Afternoon,
            >= 14 and < 23 => DayPeriod.Active,
            _ => DayPeriod.LateNight
        };

        // Adjust opacity for late night
        if (DayPeriod == DayPeriod.LateNight && !IsPointerOver)
            SpiritOpacity = 0.6;
    }

    // ── Color palette ──

    private void ApplyPalette(SpiritState state)
    {
        (BodyColor, GlowColor, ShadowColor) = state switch
        {
            SpiritState.Idle => (
                Color.Parse("#A8E6CF"),  // matcha green
                Color.Parse("#D4F5E6"),  // light matcha
                Color.Parse("#4A7A5E")   // dark green shadow
            ),
            SpiritState.Thinking => (
                Color.Parse("#F5C563"),  // amber yellow
                Color.Parse("#FDE8A0"),  // light amber
                Color.Parse("#B8943A")   // dark amber shadow
            ),
            SpiritState.Happy => (
                Color.Parse("#FFB7C5"),  // sakura pink
                Color.Parse("#FFD6E0"),  // light pink
                Color.Parse("#C48A96")   // dark pink shadow
            ),
            SpiritState.Observing => (
                Color.Parse("#C3A6D8"),  // lilac purple
                Color.Parse("#E0D0EE"),  // light lilac
                Color.Parse("#8A6FA5")   // dark purple shadow
            ),
            SpiritState.Warning => (
                Color.Parse("#FF8B6A"),  // coral orange
                Color.Parse("#FFBCA8"),  // light coral
                Color.Parse("#B86048")   // dark coral shadow
            ),
            SpiritState.Sleeping => (
                Color.Parse("#8CC7AD"),  // dim matcha
                Color.Parse("#B0D9C6"),  // faded matcha
                Color.Parse("#5A8A6F")   // dim shadow
            ),
            SpiritState.Startled => (
                Color.Parse("#FFE066"),  // bright yellow
                Color.Parse("#FFF2B2"),  // light yellow
                Color.Parse("#C4A840")   // dark yellow shadow
            ),
            SpiritState.Falling => (
                Color.Parse("#B8D8F0"),  // sky blue
                Color.Parse("#D6ECFA"),  // light sky
                Color.Parse("#7AA0BA")   // dark sky shadow
            ),
            _ => (
                Color.Parse("#A8E6CF"),
                Color.Parse("#D4F5E6"),
                Color.Parse("#4A7A5E")
            )
        };
    }
}
