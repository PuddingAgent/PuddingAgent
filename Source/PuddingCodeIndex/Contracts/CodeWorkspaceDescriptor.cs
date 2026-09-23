using System.Collections.Generic;

namespace PuddingCodeIntelligence.Contracts;

public sealed record CodeWorkspaceDescriptor(
    string WorkspaceId,
    string ProjectId,
    string ProjectPath,
    bool IsLoaded = false,
    string? SolutionPath = null,
    IReadOnlyList<string>? ProjectFilePaths = null);
