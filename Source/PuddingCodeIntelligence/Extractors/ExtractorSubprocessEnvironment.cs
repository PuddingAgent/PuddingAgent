using System.Diagnostics;

namespace PuddingCodeIntelligence.Extractors;

/// <summary>
/// Scopes the environment of an extractor subprocess.
/// </summary>
/// <remarks>
/// <para>
/// <c>NODE_PATH</c> has to reach the child process only. <see cref="Environment.SetEnvironmentVariable(string,string)"/>
/// would mutate the whole host process: every later subprocess (and any other component) would inherit
/// the asset directory, and the value would survive the call, which makes the failure surface far away
/// from its cause. <c>ProcessStartInfo.Environment</c> is per-child by construction.
/// </para>
/// <para>
/// Setting (not appending) the variable is intentional: the child's module resolution must be determined
/// by the component's own assets, not by whatever the ambient environment happens to contain.
/// </para>
/// </remarks>
internal static class ExtractorSubprocessEnvironment
{
    /// <summary>Environment variable Node.js resolves modules through when they are not co-located with the script.</summary>
    internal const string NodeModulesPathVariable = "NODE_PATH";

    /// <summary>
    /// Points the child process at <paramref name="nodeModulesPath"/> through its own environment block.
    /// No-op when <paramref name="nodeModulesPath"/> is null or blank (a kind that needs no node modules).
    /// </summary>
    internal static void ApplyNodeModulesPath(ProcessStartInfo startInfo, string? nodeModulesPath)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        if (string.IsNullOrWhiteSpace(nodeModulesPath))
            return;

        startInfo.Environment[NodeModulesPathVariable] = nodeModulesPath;
    }
}
