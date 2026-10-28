using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

using Microsoft.Extensions.Logging;

using PuddingCodeIntelligence.Contracts;
using PuddingCodeIndex.Contracts;

namespace PuddingCodeIntelligence.CSharp;

/// <summary>
/// Bootstraps a Roslyn <see cref="MSBuildWorkspace"/>, registers the MSBuild
/// locator once per process, and loads solutions or individual projects.
/// </summary>
internal sealed class RoslynWorkspaceBootstrapper
{
    private static readonly object Lock = new();
    private static bool _msbuildRegistered;

    private readonly ILogger _logger;

    public RoslynWorkspaceBootstrapper(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Opens the workspace described by <paramref name="descriptor"/>.
    /// Tries .sln first, then .slnx, then falls back to enumerating .csproj.
    /// </summary>
    public async Task<Microsoft.CodeAnalysis.Workspace> OpenWorkspaceAsync(
        CodeWorkspaceDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        EnsureMsBuildRegistered();

        var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(OnWorkspaceFailed);

        var solution = await TryOpenSolutionAsync(workspace, descriptor, cancellationToken).ConfigureAwait(false);
        if (solution is not null)
            return workspace;

        return await OpenProjectsAsync(workspace, descriptor, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureMsBuildRegistered()
    {
        if (_msbuildRegistered)
            return;

        lock (Lock)
        {
            if (_msbuildRegistered)
                return;

            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();

            _msbuildRegistered = true;
        }
    }

    private async Task<Solution?> TryOpenSolutionAsync(
        MSBuildWorkspace workspace,
        CodeWorkspaceDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (descriptor.SolutionPath is { Length: > 0 })
        {
            try
            {
                _logger.LogInformation("Opening solution: {SolutionPath}", descriptor.SolutionPath);
                return await workspace.OpenSolutionAsync(descriptor.SolutionPath, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to open solution {SolutionPath}", descriptor.SolutionPath);
            }
        }

        if (descriptor.ProjectFilePaths is { Count: > 0 })
        {
            var slnx = descriptor.ProjectFilePaths.FirstOrDefault(
                p => string.Equals(Path.GetExtension(p), ".slnx", StringComparison.OrdinalIgnoreCase));
            if (slnx is not null)
            {
                try
                {
                    _logger.LogInformation("Opening .slnx: {SlnxPath}", slnx);
                    return await workspace.OpenSolutionAsync(slnx, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to open .slnx {SlnxPath}; will fall back to .csproj enumeration", slnx);
                }
            }
        }

        return null;
    }

    private async Task<Microsoft.CodeAnalysis.Workspace> OpenProjectsAsync(
        MSBuildWorkspace workspace,
        CodeWorkspaceDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var csprojFiles = descriptor.ProjectFilePaths is { Count: > 0 }
            ? descriptor.ProjectFilePaths
                .Where(p => string.Equals(Path.GetExtension(p), ".csproj", StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [];

        if (csprojFiles.Length == 0)
        {
            _logger.LogWarning("No .csproj files found to index in {ProjectPath}", descriptor.ProjectPath);
            return workspace;
        }

        // MSBuild 会把被引用项目作为依赖一并装载：按列表逐个显式打开时，
        // 后出现的被引用项目已经在工作区里，OpenProjectAsync 必定抛 ArgumentException，
        // 而那份 MSBuild 评估是纯浪费（读整套引用程序集 + 重新求值）。
        // 先在候选里去掉重复项、再剔除已装载项，只对真正缺的项目调用。
        var alreadyLoaded = workspace.CurrentSolution.Projects
            .Select(project => project.FilePath)
            .ToArray();
        var planned = ProjectOpenPlanner.PlanOpenOrder(csprojFiles, alreadyLoaded);

        if (planned.Count < csprojFiles.Length)
        {
            _logger.LogInformation(
                "Skipping {Skipped} of {Total} project(s) already present in the workspace (loaded as dependencies or listed twice).",
                csprojFiles.Length - planned.Count,
                csprojFiles.Length);
        }

        foreach (var csproj in planned)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _logger.LogInformation("Opening project: {ProjectPath}", csproj);
                await workspace.OpenProjectAsync(csproj, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to open project {ProjectPath}", csproj);
            }
        }

        return workspace;
    }

    private void OnWorkspaceFailed(WorkspaceDiagnosticEventArgs e)
    {
        var level = e.Diagnostic.Kind switch
        {
            WorkspaceDiagnosticKind.Failure => LogLevel.Warning,
            _ => LogLevel.Information,
        };

        _logger.Log(level, "Roslyn workspace diagnostic: {Message}", e.Diagnostic.Message);
    }
}
