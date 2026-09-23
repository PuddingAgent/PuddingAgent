using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.Services;

/// <summary>
/// Resolves registered project directories into C# workspace metadata that
/// later indexers and language servers can consume.
/// </summary>
public sealed class DefaultCodeWorkspaceResolver : ICodeWorkspaceResolver
{
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".pudding-code",
        "bin",
        "node_modules",
        "obj",
    };

    private readonly ICodeIndexStore _store;

    public DefaultCodeWorkspaceResolver(ICodeIndexStore store)
    {
        _store = store;
    }

    public async Task<CodeWorkspaceDescriptor?> ResolveWorkspaceAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await _store.GetProjectAsync(workspaceId, projectId, cancellationToken)
            .ConfigureAwait(false);
        return project is null || project.Status == CodeProjectStatus.Removed
            ? null
            : ResolveProject(project);
    }

    public async Task<IReadOnlyList<CodeWorkspaceDescriptor>> ResolveWorkspacesByProjectPathAsync(
        string workspaceId,
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        if (!CodePathIdentity.TryNormalizeDirectoryPath(projectPath, out var normalizedPath, out _))
            return [];

        var projects = await _store.ListProjectsAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return projects
            .Where(project => CodePathIdentity.TryNormalizeDirectoryPath(project.ProjectPath, out var candidatePath, out _)
                && PathEquals(candidatePath, normalizedPath))
            .Select(ResolveProject)
            .ToArray();
    }

    public async Task<IReadOnlyList<CodeWorkspaceDescriptor>> ResolveWorkspacesAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var projects = await _store.ListProjectsAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return projects.Select(ResolveProject).ToArray();
    }

    private static CodeWorkspaceDescriptor ResolveProject(CodeProjectRecord project)
    {
        if (!CodePathIdentity.TryNormalizeDirectoryPath(project.ProjectPath, out var projectPath, out _))
        {
            return new CodeWorkspaceDescriptor(
                project.WorkspaceId,
                project.ProjectId,
                project.ProjectPath);
        }

        if (!Directory.Exists(projectPath))
        {
            return new CodeWorkspaceDescriptor(
                project.WorkspaceId,
                project.ProjectId,
                projectPath);
        }

        var solutionPath = FindPrimarySolutionPath(projectPath);
        var projectFiles = EnumerateCandidateFiles(projectPath)
            .Where(path => string.Equals(Path.GetExtension(path), ".csproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, PathComparer.Instance)
            .ToArray();

        return new CodeWorkspaceDescriptor(
            project.WorkspaceId,
            project.ProjectId,
            projectPath,
            IsLoaded: solutionPath is not null || projectFiles.Length > 0,
            SolutionPath: solutionPath,
            ProjectFilePaths: projectFiles);
    }

    private static string? FindPrimarySolutionPath(string projectPath)
    {
        return EnumerateCandidateFiles(projectPath)
            .Where(path =>
            {
                var extension = Path.GetExtension(path);
                return string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(GetSolutionPriority)
            .ThenBy(path => path, PathComparer.Instance)
            .FirstOrDefault();
    }

    private static IEnumerable<string> EnumerateCandidateFiles(string root)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(CodePathIdentity.PathComparer);
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (!CodePathIdentity.TryNormalizeDirectoryPath(directory, out var normalizedDirectory, out _)
                || !visited.Add(normalizedDirectory))
            {
                continue;
            }

            foreach (var file in SafeEnumerateFiles(directory))
                yield return file;

            foreach (var child in SafeEnumerateDirectories(directory))
            {
                if (!IgnoredDirectoryNames.Contains(Path.GetFileName(child)) && !IsReparsePoint(child))
                    pending.Push(child);
            }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return true;
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directory)
    {
        try
        {
            return Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static int GetSolutionPriority(string path) =>
        string.Equals(Path.GetExtension(path), ".slnx", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, CodePathIdentity.PathComparison);

    private sealed class PathComparer : IComparer<string>
    {
        public static readonly PathComparer Instance = new();

        public int Compare(string? x, string? y) =>
            string.Compare(x, y, CodePathIdentity.PathComparison);
    }
}
