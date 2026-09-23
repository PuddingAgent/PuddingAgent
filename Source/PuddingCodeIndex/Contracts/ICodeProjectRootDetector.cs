using System.Threading;
using System.Threading.Tasks;

namespace PuddingCodeIntelligence.Contracts;

/// <summary>
/// Detects project root directories by walking upward from a starting path,
/// stopping at .git or known project marker files. Never recurses into child
/// directories.
/// </summary>
public interface ICodeProjectRootDetector
{
    /// <summary>
    /// Walk upward from <paramref name="startDirectory"/> to find the nearest
    /// project root. Returns null if no root is found within the filesystem
    /// boundary (drive root or '/').
    /// </summary>
    Task<string?> DetectRootAsync(
        string startDirectory,
        CancellationToken cancellationToken = default);
}
