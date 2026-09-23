using System.Collections.Generic;

namespace PuddingCodeIndex.Contracts;

public sealed record CodeWorkspaceDescriptor(
    string WorkspaceId,
    string ProjectId,
    string ProjectPath,
    bool IsLoaded = false,
    string? SolutionPath = null,
    IReadOnlyList<string>? ProjectFilePaths = null);
