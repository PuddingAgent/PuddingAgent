namespace PuddingCode.Configuration;

/// <summary>Feature flags and scheduling controls for subconscious memory maintenance.</summary>
public sealed class SubconsciousOptions
{
    public const string SectionName = "Subconscious";

    /// <summary>
    /// Enables the legacy agent-loop hook that writes directly to the in-memory consolidation channel.
    /// Keep disabled when durable Hook v2 jobs are the primary maintenance path.
    /// </summary>
    public bool EnableLegacyConsolidationHook { get; init; }

    /// <summary>
    /// Enables the older AgentExecutionService channel fallback when no legacy hook is registered.
    /// This is off by default to prevent duplicate learning beside durable SubconsciousJobs.
    /// </summary>
    public bool EnableLegacyAgentExecutionFallback { get; init; }

    /// <summary>Scheduling controls for durable subconscious jobs.</summary>
    public SubconsciousSchedulingOptions Scheduling { get; init; } = new();

    /// <summary>
    /// Enables the dedicated debug API group for runtime pause/resume and diagnostics.
    /// Keep this route isolated from normal admin APIs so it can be closed as one unit.
    /// </summary>
    public bool DebugApiEnabled { get; init; } = true;
}

/// <summary>Runtime scheduling controls for durable subconscious background jobs.</summary>
public sealed class SubconsciousSchedulingOptions
{
    public bool Enabled { get; init; } = true;
    public bool DryRun { get; init; }
    public int TickIntervalSeconds { get; init; } = 2;
    public int IdleCooldownSeconds { get; init; } = 60;
    public bool ForegroundGenerationBlocksExecution { get; init; } = true;
    public int MaxGlobalConcurrentJobs { get; init; } = 1;
    public int MaxWorkspaceConcurrentJobs { get; init; } = 1;
    public int MaxSessionConcurrentJobs { get; init; } = 1;
    public int MaxJobsPerTick { get; init; } = 1;
    public int MaxJobsPerWorkspacePerHour { get; init; } = 20;
    public int MaxRetryAttempts { get; init; } = 3;
    public int RetryBackoffSeconds { get; init; } = 60;
    public int BudgetWindowMinutes { get; init; } = 60;
}
