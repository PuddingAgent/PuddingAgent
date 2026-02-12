using System;
using PuddingAssistantDesktop.ViewModels;

namespace PuddingAssistantDesktop.Heartbeat;

/// <summary>
/// Consciousness-tier autonomous behavior engine.
/// Evaluates <see cref="EnvironmentSnapshot"/> context and decides
/// whether the pudding should self-initiate actions (greetings, reminders, state changes).
/// </summary>
internal sealed class AutonomousBehavior
{
    /// <summary>User idle threshold before pudding enters sleep state.</summary>
    private static readonly TimeSpan IdleSleepThreshold = TimeSpan.FromMinutes(5);

    /// <summary>User idle threshold for a gentle rest reminder (late night only).</summary>
    private static readonly TimeSpan LateNightReminderThreshold = TimeSpan.FromMinutes(30);

    /// <summary>Tracks whether a morning greeting has been delivered today.</summary>
    private DateTime _lastGreetingDate = DateTime.MinValue;

    /// <summary>Tracks the last rest reminder to avoid spamming.</summary>
    private DateTime _lastRestReminder = DateTime.MinValue;

    /// <summary>
    /// Evaluates the current environment and decides an autonomous action.
    /// Called once per consciousness beat (~60s).
    /// </summary>
    /// <returns>The action the pudding should take, or <c>null</c> if none.</returns>
    public AutonomousAction? Evaluate(EnvironmentSnapshot snapshot, SpiritState currentState, DayPeriod dayPeriod)
    {
        // Morning greeting (once per day, during morning period)
        if (dayPeriod == DayPeriod.Morning && _lastGreetingDate.Date != snapshot.Timestamp.Date)
        {
            _lastGreetingDate = snapshot.Timestamp;
            return new AutonomousAction(AutonomousIntent.MorningGreeting, "Good morning!");
        }

        // Late-night rest reminder
        if (dayPeriod == DayPeriod.LateNight
            && snapshot.UserIdleDuration < TimeSpan.FromMinutes(2) // user is actively working
            && (snapshot.Timestamp - _lastRestReminder) > LateNightReminderThreshold)
        {
            _lastRestReminder = snapshot.Timestamp;
            return new AutonomousAction(AutonomousIntent.RestReminder, "It's late, time to rest.");
        }

        // Idle sleep transition
        if (currentState is SpiritState.Idle or SpiritState.Happy
            && snapshot.UserIdleDuration > IdleSleepThreshold
            && !snapshot.IsDragging)
        {
            return new AutonomousAction(AutonomousIntent.Sleep, null);
        }

        // Wake up from sleep if user becomes active
        if (currentState == SpiritState.Sleeping
            && snapshot.UserIdleDuration < TimeSpan.FromSeconds(10))
        {
            return new AutonomousAction(AutonomousIntent.WakeUp, null);
        }

        return null;
    }
}

/// <summary>The type of autonomous action the pudding decides to take.</summary>
internal enum AutonomousIntent
{
    /// <summary>Morning period greeting, first interaction of the day.</summary>
    MorningGreeting,

    /// <summary>Gentle late-night rest reminder.</summary>
    RestReminder,

    /// <summary>Transition to sleep state due to prolonged user idle.</summary>
    Sleep,

    /// <summary>Wake up from sleep because user became active.</summary>
    WakeUp
}

/// <summary>An autonomous action with an intent and optional message.</summary>
internal sealed record AutonomousAction(AutonomousIntent Intent, string? Message);
