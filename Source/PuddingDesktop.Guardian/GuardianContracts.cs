using System.Text.Json;

namespace PuddingDesktop.Guardian;

public sealed record GuardianSettings
{
    public required string DesktopExecutablePath { get; init; }
    public required string CoreExecutablePath { get; init; }
    public required string DesktopHome { get; init; }
    public required string DataRoot { get; init; }
    public required string StateDirectory { get; init; }
    public string? RepositoryRoot { get; init; }
    public int HttpPort { get; init; } = 8200;
    public int DesktopControlPort { get; init; } = 8199;
    public int StartupTimeoutSeconds { get; init; } = 120;
    public int StableSeconds { get; init; } = 15;
    public int PollSeconds { get; init; } = 5;
    public int FailureThreshold { get; init; } = 3;
    public int BuildTimeoutSeconds { get; init; } = 600;
    public int ShutdownTimeoutSeconds { get; init; } = 30;
    public string Configuration { get; init; } = "Debug";
    public bool AutoStartCore { get; init; } = true;
    public bool AutoYolo { get; init; } = true;
    public bool StartHidden { get; init; }

    public void Validate()
    {
        foreach (var path in new[] { DesktopExecutablePath, CoreExecutablePath, DesktopHome, DataRoot, StateDirectory })
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Guardian paths must be absolute.");
        if (HttpPort is < 1024 or > 65535 || DesktopControlPort is < 1 or > 65535 || HttpPort == DesktopControlPort)
            throw new ArgumentException("Guardian requires a separate loopback port.");
        if (StartupTimeoutSeconds is < 5 or > 600 || StableSeconds is < 1 or > 120 || StableSeconds >= StartupTimeoutSeconds
            || PollSeconds is < 1 or > 30 || FailureThreshold is < 1 or > 10
            || BuildTimeoutSeconds is < 10 or > 3600 || ShutdownTimeoutSeconds is < 1 or > 120)
            throw new ArgumentException("Guardian timeout or health policy is out of range.");
        if (Configuration is not ("Debug" or "Release")) throw new ArgumentException("Invalid build configuration.");
        var package = Path.GetDirectoryName(DesktopExecutablePath)!;
        if (!SafeFiles.Within(CoreExecutablePath, package)) throw new ArgumentException("Guardian requires a bundled Core.");
        if (SafeFiles.Overlaps(StateDirectory, package) || SafeFiles.Overlaps(DataRoot, package)
            || SafeFiles.Overlaps(StateDirectory, DataRoot))
            throw new ArgumentException("Package, data and guardian state must be isolated.");
    }

    public string TargetDirectory(string target) => Path.GetDirectoryName(target == "core" ? CoreExecutablePath : DesktopExecutablePath)!;
    public string EntryFile(string target) => Path.GetFileName(target == "core" ? CoreExecutablePath : DesktopExecutablePath);
}

public sealed record GuardianCommand
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public string Action { get; init; } = "build-restart";
    public string Target { get; init; } = "core";
    public string DeploymentMode { get; init; } = "desktop-build";
    public string? ArtifactDirectory { get; init; }
    public string? ArtifactAssemblySha256 { get; init; }
    public string FrontendMode { get; init; } = "skip";
    public string? FrontendArtifactDirectory { get; init; }
    public string? FrontendArtifactIndexSha256 { get; init; }
    public string? RequestedBy { get; init; }
    public bool Yolo { get; init; }

    public void Validate()
    {
        if (!Guid.TryParse(RequestId, out _)) throw new ArgumentException("requestId must be a UUID.");
        if (Action is not ("build-restart" or "build" or "restart" or "rollback" or "start" or "stop"))
            throw new ArgumentException("Unsupported action.");
        if (Target is not ("core" or "desktop")) throw new ArgumentException("target must be core or desktop (includes Core).");
        if (DeploymentMode is not ("desktop-build" or "prebuilt-artifact" or "restart-only"))
            throw new ArgumentException("Unsupported deploymentMode.");
        if (FrontendMode is not ("skip" or "build" or "load")) throw new ArgumentException("Unsupported frontendMode.");
        if (DeploymentMode == "prebuilt-artifact" && string.IsNullOrWhiteSpace(ArtifactDirectory))
            throw new ArgumentException("artifactDirectory is required.");
        if (FrontendMode == "load" && string.IsNullOrWhiteSpace(FrontendArtifactDirectory))
            throw new ArgumentException("frontendArtifactDirectory is required.");
        if (FrontendMode != "skip" && (DeploymentMode != "desktop-build" || Action is not ("build-restart" or "build")))
            throw new ArgumentException("Frontend preparation requires a source build command.");
        if (RequestedBy?.Length > 256) throw new ArgumentException("requestedBy is too long.");
    }
}

public sealed record ProcessIdentity(int Id, DateTime StartTimeUtc, string ExecutablePath);
public sealed record GuardianHealth(bool DesktopHealthy, bool CoreHealthy, bool IntentionalStop = false, string? Reason = null)
{
    public bool Healthy => DesktopHealthy && CoreHealthy && !IntentionalStop;
}
public sealed record ArtifactFile(string Path, string Sha256);
public sealed record ArtifactSnapshot(string Directory, string Target, IReadOnlyList<ArtifactFile> Files);
public sealed record PendingDeployment(ArtifactSnapshot Backup, ArtifactSnapshot Candidate);
public sealed record GuardianState
{
    public string Phase { get; init; } = "starting";
    public bool Monitoring { get; init; } = true;
    public string? RequestId { get; init; }
    public string? RequestFingerprint { get; init; }
    public string? Error { get; init; }
    public string? PreparedDirectory { get; init; }
    public PendingDeployment? Pending { get; init; }
    public ArtifactSnapshot? Fallback { get; init; }
    public ProcessIdentity? Desktop { get; init; }
    public ProcessIdentity? Core { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public interface IGuardianRuntime
{
    ProcessIdentity? Desktop { get; }
    ProcessIdentity? Core { get; }
    Task<GuardianHealth> ProbeAsync(CancellationToken ct);
    Task<string> PrepareAsync(GuardianCommand command, CancellationToken ct);
    Task StopAsync(string target, CancellationToken ct);
    Task StartAsync(string target, bool yolo, CancellationToken ct);
}

public static class GuardianJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };
}
