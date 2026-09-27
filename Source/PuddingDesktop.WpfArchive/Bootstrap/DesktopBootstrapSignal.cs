namespace PuddingDesktop.Bootstrap;

/// <summary>
/// Parsed contents of a bootstrap signal file (JSON, UTF-8).
/// Protocol: { "token": "&lt;controlToken&gt;", "action": "rebuild-restart",
///             "deploymentMode": "desktop-build", "yolo": true,
///             "frontendMode": "skip", "requestedBy": "...", "message": "..." }
/// </summary>
public sealed record DesktopBootstrapSignal
{
    /// <summary>Must equal the current Desktop control token, otherwise the signal is rejected.</summary>
    public string? Token { get; init; }

    /// <summary>Supported action: "rebuild-restart".</summary>
    public string? Action { get; init; }

    /// <summary>When true (and AutoYolo is enabled), write yolo.signal before restarting Core.</summary>
    public bool Yolo { get; init; }

    /// <summary>desktop-build (default), prebuilt-artifact, or restart-only.</summary>
    public string? DeploymentMode { get; init; }

    /// <summary>Absolute prepared output directory, required by prebuilt-artifact mode.</summary>
    public string? ArtifactDirectory { get; init; }

    /// <summary>Optional expected SHA-256 for ArtifactDirectory/PuddingAgent.dll.</summary>
    public string? ArtifactAssemblySha256 { get; init; }

    /// <summary>Frontend step mode: skip (default), build, or load. Null/empty means skip.</summary>
    public string? FrontendMode { get; init; }

    /// <summary>Absolute prebuilt frontend dist directory, required by frontend_mode=load.</summary>
    public string? FrontendArtifactDirectory { get; init; }

    /// <summary>Optional expected SHA-256 of the dist index.html (frontend_mode=load only).</summary>
    public string? FrontendArtifactIndexSha256 { get; init; }

    /// <summary>Optional free-form requester identity, echoed into yolo.signal and the result file.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>Optional free-form message, currently informational only.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Result of one bootstrap attempt, written to &lt;SignalPath&gt;.result.json
/// so external tooling has self-evident health feedback.
/// </summary>
public sealed record DesktopBootstrapResult
{
    /// <summary>True only when the build succeeded, Core is running again and no error was recorded.</summary>
    public bool Success { get; init; }

    /// <summary>Action that was requested ("rebuild-restart").</summary>
    public string Action { get; init; } = "rebuild-restart";

    /// <summary>When the signal was first picked up.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the whole flow (including result write) finished.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>dotnet build exit code. Null when the build never ran.</summary>
    public int? BuildExitCode { get; init; }

    /// <summary>Last 30 lines of the build output (stdout + stderr).</summary>
    public List<string> BuildLogTail { get; init; } = [];

    /// <summary>Canonical deployment mode used for this operation.</summary>
    public string? DeploymentMode { get; init; }

    /// <summary>Prepared artifact directory produced by Desktop or supplied by the caller.</summary>
    public string? BuildOutputDirectory { get; init; }

    /// <summary>Directory containing the Core executable that Desktop actually starts.</summary>
    public string? DeploymentDirectory { get; init; }

    /// <summary>Core executable path used by the successful restart attempt.</summary>
    public string? CoreExecutablePath { get; init; }

    /// <summary>Files transactionally replaced and files already byte-identical.</summary>
    public int DeploymentCopied { get; init; }
    public int DeploymentSkipped { get; init; }

    /// <summary>SHA-256 of the prepared and launched PuddingAgent.dll assemblies.</summary>
    public string? PreparedAssemblySha256 { get; init; }
    public string? LoadedAssemblySha256 { get; init; }

    /// <summary>Deterministic fingerprint and file count for all managed launch artifacts.</summary>
    public string? PreparedArtifactManifestSha256 { get; init; }
    public string? LoadedArtifactManifestSha256 { get; init; }
    public int ManagedArtifactFileCount { get; init; }

    /// <summary>True only when the restarted Core path contains the exact prepared assembly.</summary>
    public bool AssembliesReloaded { get; init; }

    /// <summary>True when Core was confirmed running (Ready) after the restart attempt.</summary>
    public bool CoreRestarted { get; init; }

    /// <summary>True when yolo.signal was written after a successful build.</summary>
    public bool YoloSignalWritten { get; init; }

    /// <summary>
    /// Frontend step outcome. Always present (mode=skip when the frontend was
    /// not requested) so that "did this restart rebuild the frontend?" is a
    /// checkable fact in every result file.
    /// </summary>
    public FrontendStepResult Frontend { get; init; } = new() { Mode = "skip" };

    /// <summary>Human-readable error list. Empty on a fully successful loop.</summary>
    public List<string> Errors { get; init; } = [];

    /// <summary>Outcome of the optional frontend rebuild/deploy step of one bootstrap.</summary>
    public sealed record FrontendStepResult
    {
        /// <summary>Requested mode: "skip", "build", or "load".</summary>
        public string Mode { get; init; } = "skip";

        /// <summary>True when the frontend step actually ran (never for skip / restart-only).</summary>
        public bool Ran { get; init; }

        public bool RanInstall { get; init; }

        public bool BuiltFromSource { get; init; }

        public int CopiedFileCount { get; init; }

        public string? TargetAdminDirectory { get; init; }

        public string? IndexSha256 { get; init; }

        /// <summary>True when the step failed; the bootstrap was then aborted fail-closed.</summary>
        public bool Failed { get; init; }

        public string? Error { get; init; }
    }
}
