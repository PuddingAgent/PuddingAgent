using PuddingDesktop.Configuration;

namespace PuddingDesktop.Debug;

/// <summary>
/// Resolves the source repository layout for debug mode. An explicit
/// RepositoryRoot in desktop.json wins; otherwise the repository root is
/// discovered by walking up from the Desktop executable until both the
/// backend project and the frontend package are found (mirrors the
/// CoreExecutableResolver dev-tree fallback).
/// </summary>
public static class DebugRepositoryResolver
{
    public const string BackendProjectRelativePath = @"Source\PuddingAgent\PuddingAgent.csproj";
    public const string FrontendDirectoryRelativePath = @"Source\PuddingPlatformAdmin";

    public static string ResolveRepositoryRoot(string? configuredRoot)
        => ResolveRepositoryRoot(configuredRoot, AppContext.BaseDirectory);

    internal static string ResolveRepositoryRoot(string? configuredRoot, string startDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var fullConfiguredRoot = Path.GetFullPath(configuredRoot);
            if (!Directory.Exists(fullConfiguredRoot))
                throw new DirectoryNotFoundException(
                    $"Configured debug repository root does not exist: {fullConfiguredRoot}");
            return fullConfiguredRoot;
        }

        var directory = Path.GetFullPath(startDirectory);
        while (directory is not null)
        {
            if (IsRepositoryRoot(directory))
                return directory;

            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new DirectoryNotFoundException(
            "Cannot locate the PuddingAgent repository root for debug mode. " +
            $"Expected {BackendProjectRelativePath} above {startDirectory}. " +
            "Set debug.repositoryRoot in desktop.json.");
    }

    public static string ResolveFrontendWorkingDirectory(DesktopDebugSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!string.IsNullOrWhiteSpace(settings.FrontendWorkingDirectory))
        {
            var configured = Path.GetFullPath(settings.FrontendWorkingDirectory);
            if (!Directory.Exists(configured))
                throw new DirectoryNotFoundException(
                    $"Configured debug frontend directory does not exist: {configured}");
            return configured;
        }

        var repositoryRoot = ResolveRepositoryRoot(settings.RepositoryRoot);
        var frontendDirectory = Path.Combine(repositoryRoot, FrontendDirectoryRelativePath);
        if (!File.Exists(Path.Combine(frontendDirectory, "package.json")))
            throw new DirectoryNotFoundException(
                $"Frontend package.json not found under: {frontendDirectory}");
        return frontendDirectory;
    }

    public static string ResolveBackendProjectPath(DesktopDebugSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!string.IsNullOrWhiteSpace(settings.BackendProjectPath))
        {
            var configured = Path.GetFullPath(settings.BackendProjectPath);
            if (!File.Exists(configured))
                throw new FileNotFoundException(
                    $"Configured debug backend project does not exist: {configured}");
            return configured;
        }

        var repositoryRoot = ResolveRepositoryRoot(settings.RepositoryRoot);
        var backendProject = Path.Combine(repositoryRoot, BackendProjectRelativePath);
        if (!File.Exists(backendProject))
            throw new FileNotFoundException(
                $"Backend project not found under: {backendProject}");
        return backendProject;
    }

    internal static bool IsRepositoryRoot(string directory) =>
        File.Exists(Path.Combine(directory, BackendProjectRelativePath))
        && File.Exists(Path.Combine(directory, FrontendDirectoryRelativePath, "package.json"));
}
