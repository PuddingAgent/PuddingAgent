using System.Text.RegularExpressions;
using PuddingWorkspace.Contracts;

namespace PuddingWorkspace;

/// <summary>
/// PA-05F path policy: only canonical, relative, in-workspace paths are accepted. Rooted paths, drive letters,
/// backslashes, "." / ".." segments, empty segments, control characters, Windows-reserved names and trailing
/// dots/spaces are rejected; normalization never "repairs" a suspicious path into an accepted one.
/// </summary>
public static partial class WorkspaceFilePolicy
{
    private const int MaxPathLength = 200;
    private const int MaxSegments = 32;

    public static string Normalize(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > MaxPathLength)
            throw Error(WorkspaceErrorCodes.PathRejected, "A bounded relative path is required.");
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\') || relativePath.Contains(':'))
            throw Error(WorkspaceErrorCodes.PathRejected, "Rooted paths and drive letters are not allowed.");
        var segments = relativePath.Split('/');
        if (segments.Length > MaxSegments)
            throw Error(WorkspaceErrorCodes.PathRejected, "The path has too many segments.");
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
                throw Error(WorkspaceErrorCodes.PathRejected, "Empty, '.' and '..' segments are not allowed.");
            if (segment.EndsWith('.') || segment.EndsWith(' '))
                throw Error(WorkspaceErrorCodes.PathRejected, "A segment cannot end with a dot or space.");
            if (segment.Any(c => c < 32 || "<>\"|?*".Contains(c)))
                throw Error(WorkspaceErrorCodes.PathRejected, "A segment contains an unsafe character.");
            if (ReservedName().IsMatch(segment))
                throw Error(WorkspaceErrorCodes.PathRejected, "A Windows reserved name is not allowed.");
        }
        return relativePath;
    }

    public static void EnsureWorkspaceId(string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || workspaceId.Length > 128 || workspaceId is "." or ".."
            || workspaceId.Contains("..") || workspaceId.Contains(':')
            || workspaceId.Contains('\\') || workspaceId.Contains('/'))
            throw Error(WorkspaceErrorCodes.WorkspaceForbidden, "A bounded authorized workspace id is required.");
    }

    private static WorkspaceException Error(string code, string message) => new(code, message);

    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();
}
