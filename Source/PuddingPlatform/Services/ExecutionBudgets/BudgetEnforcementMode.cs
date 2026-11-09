namespace PuddingPlatform.Services.ExecutionBudgets;

/// <summary>
/// PA-01 P2 enforcement mode, frozen when the root account is created. Legacy is diagnostic only; Shadow reserves
/// but does not gate; Enforced is the delivery target and must never be downgraded in place.
/// </summary>
public enum BudgetEnforcementMode { Legacy = 0, Shadow = 1, Enforced = 2 }
