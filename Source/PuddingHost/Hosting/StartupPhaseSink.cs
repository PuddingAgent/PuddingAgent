namespace PuddingHost.Hosting;

/// <summary>
/// Optional startup evidence seam. The host reports phase boundaries, outcomes and data-scale counts;
/// it never decides how evidence is stored and never reports session content or secrets. A null sink
/// means "no evidence requested" and must not change startup behavior.
/// <para>
/// Defined here, not in a Desktop assembly, because Core must not depend on the Desktop shell: the
/// Desktop composition root adapts its own recorder to this port.
/// </para>
/// </summary>
public interface IStartupPhaseSink
{
    IStartupPhaseScope Phase(string name);

    /// <summary>A count that explains duration (rows, workspaces, steps). Never content.</summary>
    void Metric(string name, long value);
}

/// <summary>One phase in flight. Completion is explicit; disposing without it means the step died.</summary>
public interface IStartupPhaseScope : IDisposable
{
    void Complete(string? detail = null);
    void Skip(string reason);
}

/// <summary>
/// The phase vocabulary this host reports. The Desktop evidence artifact keeps the matching list in
/// <c>PuddingDesktop.Foundation.StartupPhases</c>; Core must not reference the Desktop assembly, so the
/// two lists are separate and a Composition test fails when this one grows a name the report cannot
/// place. Names are stable identifiers: never put paths, ids or content in them.
/// </summary>
public static class StartupPhaseNames
{
    public const string PlatformSchema = "host.initialize.platform-schema";
    public const string PlatformSchemaStepPrefix = "host.initialize.platform-schema.";
    public const string PlatformSchemaMarker = "host.initialize.platform-schema.marker";
    public const string PlatformSchemaStamp = "host.initialize.platform-schema.stamp";
    public const string GoalReconcile = "host.initialize.goal-reconcile";
    public const string ExternalApiConfig = "host.initialize.external-api-config";
    public const string EventStore = "host.initialize.event-store";
    public const string MemoryDbCore = "host.initialize.memory-db.core";
    public const string MemoryDbLibrary = "host.initialize.memory-db.library";
    public const string WorkspaceCatalog = "host.initialize.workspace-catalog";
    public const string JiebaBackfill = "host.initialize.jieba-backfill";
}

/// <summary>Data-scale counts that explain a duration without exposing content.</summary>
public static class StartupMetrics
{
    public const string SchemaStepCount = "platform-schema.step-count";
    public const string SchemaRevision = "platform-schema.revision";
    public const string SchemaLadderSkipped = "platform-schema.ladder-skipped";
    public const string WorkspaceCount = "workspace.count";
}
