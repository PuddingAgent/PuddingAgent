using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PuddingDesktop.Bootstrap;

/// <summary>
/// Pure, side-effect-free parsing / validation / path logic for the bootstrap
/// signal protocol. Kept internal static so it can be unit tested without a UI
/// or process dependency (no PuddingDesktopTests project exists today).
/// </summary>
internal static class DesktopBootstrapSignalParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public const string RebuildRestartAction = "rebuild-restart";
    public const string DesktopBuildMode = "desktop-build";
    public const string PrebuiltArtifactMode = "prebuilt-artifact";
    public const string RestartOnlyMode = "restart-only";
    public const string RepositoryRootEnvironmentVariable = "PUDDING_REPOSITORY_ROOT";
    public const string YoloSignalFileName = "yolo.signal";
    public const string DefaultSignalFileName = "rebuild.signal";
    public const string DefaultBuildProjectRelativePath = "Source/PuddingAgent/PuddingAgent.csproj";
    public const int DefaultBuildTimeoutSeconds = 300;
    private const int MaxRepositoryWalkDepth = 8;

    /// <summary>
    /// Parses the signal file JSON payload. Returns null when the payload is not
    /// valid JSON or is empty — the caller treats that as "reject and delete".
    /// </summary>
    public static DesktopBootstrapSignal? TryParseSignal(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<DesktopBootstrapSignal>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Constant-time token comparison. Rejects null/empty without leaking the
    /// expected value.
    /// </summary>
    public static bool IsTokenValid(string? providedToken, string expectedToken)
    {
        if (string.IsNullOrEmpty(providedToken) || string.IsNullOrEmpty(expectedToken))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedToken),
            Encoding.UTF8.GetBytes(expectedToken));
    }

    /// <summary>Checks the action field against the supported "rebuild-restart" action.</summary>
    public static bool IsSupportedAction(string? action)
        => string.Equals(action, RebuildRestartAction, StringComparison.OrdinalIgnoreCase);

    /// <summary>Normalizes supported deployment mode aliases; null means unsupported.</summary>
    public static string? NormalizeDeploymentMode(string? mode, string? defaultMode = DesktopBuildMode)
    {
        var value = string.IsNullOrWhiteSpace(mode) ? defaultMode : mode;
        return value?.Trim().ToLowerInvariant().Replace('_', '-') switch
        {
            "desktop-build" or "build" => DesktopBuildMode,
            "prebuilt-artifact" or "prebuilt" => PrebuiltArtifactMode,
            "restart-only" or "restart" => RestartOnlyMode,
            _ => null,
        };
    }

    /// <summary>
    /// Resolves the repository root: PUDDING_REPOSITORY_ROOT environment variable
    /// first, otherwise walks up from baseDirectory looking for dev-up.py or .git
    /// (same approach as YoloSignalService.ResolveSignalPath).
    /// </summary>
    public static string ResolveRepositoryRoot(string? environmentRepositoryRoot, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(environmentRepositoryRoot))
            return Path.GetFullPath(environmentRepositoryRoot);

        var current = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        for (var depth = 0; depth < MaxRepositoryWalkDepth && current is not null; depth++, current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "dev-up.py"))
                || Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }
        }

        return Path.GetFullPath(baseDirectory);
    }

    /// <summary>Path of yolo.signal inside the repository root (YoloSignalService-compatible).</summary>
    public static string ResolveYoloSignalPath(string repositoryRoot)
        => Path.Combine(repositoryRoot, YoloSignalFileName);

    /// <summary>
    /// Signal file path: configured absolute path when provided, otherwise
    /// &lt;DataRoot&gt;\config\rebuild.signal.
    /// </summary>
    public static string ResolveSignalPath(string? configuredSignalPath, string dataRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredSignalPath))
            return Path.GetFullPath(configuredSignalPath);

        return Path.Combine(dataRoot, "config", DefaultSignalFileName);
    }

    /// <summary>Build log path under the DataRoot logs directory.</summary>
    public static string ResolveBuildLogPath(string dataRoot)
        => Path.Combine(dataRoot, "logs", "desktop-bootstrap-build.log");

    /// <summary>Result file path: signal file path + ".result.json".</summary>
    public static string ResolveResultPath(string signalPath)
        => signalPath + ".result.json";

    /// <summary>
    /// Splits a command-line argument string into tokens, honoring double quotes.
    /// Empty or whitespace input yields an empty list.
    /// </summary>
    public static IReadOnlyList<string> SplitArguments(string? arguments)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(arguments))
            return result;

        var current = new StringBuilder();
        var inQuote = false;
        foreach (var ch in arguments)
        {
            if (ch == '"')
            {
                inQuote = !inQuote;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuote)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }
}
