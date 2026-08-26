using PuddingCode.Goals;

namespace PuddingPlatform.Services.Scheduling;

public sealed class TaskBoundGoalOptions
{
    public const string SectionName = "TaskBoundGoals";

    /// <summary>Independent safety switch; defaults to false.</summary>
    public bool Enabled { get; set; }

    public int GoalIterationBudget { get; set; } = 32;
    public TimeSpan ReservationLease { get; set; } = TimeSpan.FromHours(2);

    public static IReadOnlyList<string> Validate(TaskBoundGoalOptions options)
    {
        var errors = new List<string>();
        if (!GoalLimits.IsValidIterationBudget(options.GoalIterationBudget))
            errors.Add($"TaskBoundGoals:GoalIterationBudget must be between 1 and {GoalLimits.MaxIterationsHardLimit}.");
        if (options.ReservationLease < TimeSpan.FromMinutes(5)
            || options.ReservationLease > TimeSpan.FromDays(1))
            errors.Add("TaskBoundGoals:ReservationLease must be between 5m and 24h.");
        return errors;
    }
}

