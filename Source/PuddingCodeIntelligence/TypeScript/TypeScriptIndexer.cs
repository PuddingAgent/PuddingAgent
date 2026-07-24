using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Services;

namespace PuddingCodeIntelligence.TypeScript;

/// <summary>
/// TypeScript/JavaScript code indexer that extracts symbols by invoking a Node.js
/// extraction script (Scripts/extract-ts-symbols.js) as a subprocess and persists
/// the results through <see cref="ICodeIndexStore"/>.
/// Supports two modes: project-level extraction (--project) for cross-file references,
/// and per-file extraction as a fallback.
/// </summary>
public sealed class TypeScriptIndexer : ICodeIndexer
{
    // Uses centralized IndexExcludePatterns.NoiseDirNames for directory exclusion.

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts",
        ".tsx",
        ".js",
        ".jsx",
    };

    private readonly ICodeIndexStore _store;
    private readonly ILogger<TypeScriptIndexer> _logger;

    public TypeScriptIndexer(ICodeIndexStore store, ILogger<TypeScriptIndexer> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CodeIndexResult> IndexWorkspaceAsync(
        CodeWorkspaceDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (string.IsNullOrWhiteSpace(descriptor.WorkspaceId) || string.IsNullOrWhiteSpace(descriptor.ProjectId))
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed,
                "WorkspaceId and ProjectId are required.");
        }

        if (string.IsNullOrWhiteSpace(descriptor.ProjectPath) || !Directory.Exists(descriptor.ProjectPath))
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed,
                $"Project path does not exist: {descriptor.ProjectPath}",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId);
        }

        var startedAt = DateTimeOffset.UtcNow;

        // Check Node.js availability
        if (!IsNodeAvailable())
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed,
                "Node.js not available",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId,
                StartedAtUtc: startedAt);
        }

        // Locate the extraction script relative to the project root
        var scriptPath = Path.Combine(descriptor.ProjectPath, "Scripts", "extract-ts-symbols.js");
        if (!File.Exists(scriptPath))
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed,
                $"Extraction script not found: {scriptPath}",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId,
                StartedAtUtc: startedAt);
        }

        try
        {
            var files = CollectSourceFiles(descriptor.ProjectPath);
            if (files.Count == 0)
            {
                return new CodeIndexResult(true, CodeIndexStatus.Completed,
                    "No TypeScript/JavaScript files found.",
                    WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId,
                    StartedAtUtc: startedAt, CompletedAtUtc: DateTimeOffset.UtcNow);
            }

            var allSymbols = new List<CodeSymbolRecord>();
            var allRelations = new List<CodeRelationRecord>();
            var allFiles = new List<CodeFileRecord>();
            var now = DateTimeOffset.UtcNow;
            var errorCount = 0;
            var usedProjectMode = false;

            // Try project-level extraction first (single call, captures cross-file references)
            var projectResult = await RunProjectExtractionAsync(scriptPath, descriptor.ProjectPath, cancellationToken)
                .ConfigureAwait(false);

            if (projectResult is not null && projectResult.Files is { Count: > 0 })
            {
                usedProjectMode = true;
                _logger.LogInformation(
                    "TypeScript project-mode extraction for {ProjectId}: {FileCount} files, {CrossRefCount} cross-references",
                    descriptor.ProjectId, projectResult.Files.Count,
                    projectResult.CrossReferences?.Count ?? 0);

                ProcessProjectExtraction(descriptor.WorkspaceId, descriptor.ProjectId,
                    descriptor.ProjectPath, projectResult, allSymbols, allRelations, allFiles, now);
            }
            else
            {
                // Fallback: per-file extraction (existing behavior)
                _logger.LogInformation(
                    "TypeScript project-mode not available for {ProjectId}, falling back to per-file extraction",
                    descriptor.ProjectId);

                foreach (var filePath in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var extractionResult = await RunExtractionScriptAsync(scriptPath, filePath, cancellationToken)
                        .ConfigureAwait(false);

                    if (extractionResult is null)
                    {
                        errorCount++;
                        continue;
                    }

                    if (extractionResult.Symbols is not { Count: > 0 })
                        continue;

                    // Clear stale symbols for this file before re-indexing
                    await _store.ClearSymbolsForFileAsync(
                        descriptor.WorkspaceId, descriptor.ProjectId, filePath, cancellationToken)
                        .ConfigureAwait(false);

                    var symbols = new List<CodeSymbolRecord>();
                    var relations = new List<CodeRelationRecord>();

                    ConvertToRecords(descriptor.WorkspaceId, descriptor.ProjectId, filePath,
                        extractionResult, symbols, relations);

                    if (symbols.Count > 0)
                    {
                        allSymbols.AddRange(symbols);
                        allRelations.AddRange(relations);
                        allFiles.Add(new CodeFileRecord(
                            descriptor.WorkspaceId, descriptor.ProjectId, filePath,
                            GetLanguage(filePath), now));
                    }
                }
            }

            if (allSymbols.Count > 0)
            {
                await _store.UpsertFilesAsync(descriptor.WorkspaceId, descriptor.ProjectId, allFiles, cancellationToken)
                    .ConfigureAwait(false);
                await _store.UpsertSymbolsAsync(descriptor.WorkspaceId, descriptor.ProjectId, allSymbols, cancellationToken)
                    .ConfigureAwait(false);
                await _store.UpsertRelationsAsync(descriptor.WorkspaceId, descriptor.ProjectId, allRelations, cancellationToken)
                    .ConfigureAwait(false);
            }

            var modeLabel = usedProjectMode ? "project" : "per-file";
            _logger.LogInformation(
                "TypeScript indexing ({Mode}) for {ProjectId}: {SymbolCount} symbols, {RelationCount} relations in {FileCount} files ({ErrorCount} errors)",
                modeLabel, descriptor.ProjectId, allSymbols.Count, allRelations.Count, allFiles.Count, errorCount);

            return new CodeIndexResult(true, CodeIndexStatus.Completed,
                $"Indexing complete ({modeLabel} mode). {allSymbols.Count} symbols in {allFiles.Count} files.",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId,
                StartedAtUtc: startedAt, CompletedAtUtc: DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed, "Indexing was cancelled.",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId,
                StartedAtUtc: startedAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TypeScript indexing failed for {ProjectId}", descriptor.ProjectId);
            return new CodeIndexResult(false, CodeIndexStatus.Failed, $"Indexing failed: {ex.Message}",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId,
                StartedAtUtc: startedAt);
        }
    }

    /// <inheritdoc />
    public async Task<CodeIndexResult> RemoveWorkspaceIndexAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        await _store.RemoveProjectAsync(workspaceId, projectId, removeIndexedArtifacts: true, cancellationToken)
            .ConfigureAwait(false);

        return new CodeIndexResult(true, CodeIndexStatus.Completed, "Project index removed.",
            WorkspaceId: workspaceId, ProjectId: projectId);
    }

    private static bool IsNodeAvailable()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.Start();
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> CollectSourceFiles(string rootPath)
    {
        var result = new List<string>();
        CollectSourceFilesRecursive(rootPath, result);
        return result;
    }

    private static void CollectSourceFilesRecursive(string directory, List<string> result)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var ext = Path.GetExtension(file);
            if (SupportedExtensions.Contains(ext) && !IndexExcludePatterns.IsNoisePath(file))
                result.Add(file);
        }

        foreach (var subDir in Directory.EnumerateDirectories(directory))
        {
            var dirName = Path.GetFileName(subDir);
            if (IndexExcludePatterns.NoiseDirNames.Contains(dirName))
                continue;

            CollectSourceFilesRecursive(subDir, result);
        }
    }

    /// <summary>
    /// Runs the extraction script in project mode: node extract-ts-symbols.js --project &lt;directory&gt;
    /// Returns the parsed project output, or null if the script fails or is not supported.
    /// </summary>
    private async Task<TsProjectOutput?> RunProjectExtractionAsync(
        string scriptPath,
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = $"\"{scriptPath}\" --project \"{projectDirectory}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken)
                .ConfigureAwait(false);
            await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "Project-mode extraction script failed for {Directory} with exit code {ExitCode}",
                    projectDirectory, process.ExitCode);
                return null;
            }

            if (string.IsNullOrWhiteSpace(stdout))
                return null;

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };

            var result = JsonSerializer.Deserialize<TsProjectOutput>(stdout, options);

            // Validate that the output has the expected project-mode shape
            if (result?.Files is null)
                return null;

            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON parse failed for project-mode extraction output of {Directory}", projectDirectory);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to run project-mode extraction for {Directory}", projectDirectory);
            return null;
        }
    }

    /// <summary>
    /// Processes project-mode extraction output: converts file entries to symbol/file records
    /// and cross-references to relation records.
    /// </summary>
    private void ProcessProjectExtraction(
        string workspaceId,
        string projectId,
        string projectRoot,
        TsProjectOutput projectOutput,
        List<CodeSymbolRecord> allSymbols,
        List<CodeRelationRecord> allRelations,
        List<CodeFileRecord> allFiles,
        DateTimeOffset now)
    {
        if (projectOutput.Files is null)
            return;

        // Build a lookup: "relativeFile|symbolName" -> symbolId for cross-reference resolution
        var symbolIdLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fileEntry in projectOutput.Files)
        {
            if (string.IsNullOrEmpty(fileEntry.File))
                continue;

            var absolutePath = Path.GetFullPath(Path.Combine(projectRoot, fileEntry.File));

            // Clear stale symbols for this file
            _store.ClearSymbolsForFileAsync(workspaceId, projectId, absolutePath, CancellationToken.None)
                .GetAwaiter().GetResult();

            if (fileEntry.Symbols is { Count: > 0 })
            {
                foreach (var tsSymbol in fileEntry.Symbols)
                {
                    var symbolId = $"TS:{absolutePath}:{tsSymbol.Name}:{tsSymbol.Kind}";
                    var kind = MapSymbolKind(tsSymbol.Kind);

                    allSymbols.Add(new CodeSymbolRecord(
                        workspaceId,
                        projectId,
                        absolutePath,
                        symbolId,
                        tsSymbol.Name ?? "unknown",
                        kind,
                        tsSymbol.Line,
                        tsSymbol.Line, // endLine = startLine for project mode (script provides single line)
                        tsSymbol.Signature,
                        tsSymbol.ContainerName));

                    // Register in lookup for cross-reference resolution
                    var lookupKey = fileEntry.File + "|" + (tsSymbol.Name ?? "");
                    symbolIdLookup[lookupKey] = symbolId;

                    // Contains relation for nested symbols
                    if (!string.IsNullOrEmpty(tsSymbol.ContainerName))
                    {
                        var containerId = $"TS:{absolutePath}:{tsSymbol.ContainerName}";
                        allRelations.Add(new CodeRelationRecord(
                            workspaceId,
                            projectId,
                            containerId,
                            symbolId,
                            CodeRelationKind.Contains,
                            tsSymbol.Line,
                            absolutePath));
                    }
                }

                allFiles.Add(new CodeFileRecord(
                    workspaceId, projectId, absolutePath,
                    GetLanguage(absolutePath), now));
            }
        }

        // Process cross-file references
        if (projectOutput.CrossReferences is { Count: > 0 })
        {
            foreach (var crossRef in projectOutput.CrossReferences)
            {
                if (string.IsNullOrEmpty(crossRef.SourceFile) || string.IsNullOrEmpty(crossRef.TargetFile))
                    continue;

                var sourceAbsolutePath = Path.GetFullPath(Path.Combine(projectRoot, crossRef.SourceFile));
                var targetAbsolutePath = Path.GetFullPath(Path.Combine(projectRoot, crossRef.TargetFile));

                // Resolve target symbol ID from lookup
                var targetLookupKey = crossRef.TargetFile + "|" + (crossRef.TargetName ?? "");
                var targetSymbolId = symbolIdLookup.TryGetValue(
                    targetLookupKey, out var resolvedId)
                    ? resolvedId
                    : $"TS:{targetAbsolutePath}:{crossRef.TargetName}";

                // Source symbol ID: use file-level reference since we don't know the exact containing symbol
                var sourceSymbolId = $"TS:{sourceAbsolutePath}:{crossRef.SourceLine}";

                var relationKind = MapCrossReferenceKind(crossRef.Kind);

                allRelations.Add(new CodeRelationRecord(
                    workspaceId,
                    projectId,
                    sourceSymbolId,
                    targetSymbolId,
                    relationKind,
                    crossRef.SourceLine,
                    sourceAbsolutePath));
            }
        }
    }

    private async Task<TsExtractionOutput?> RunExtractionScriptAsync(
        string scriptPath,
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = $"\"{scriptPath}\" \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken)
                .ConfigureAwait(false);
            await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("Extraction script failed for {FilePath} with exit code {ExitCode}",
                    filePath, process.ExitCode);
                return null;
            }

            if (string.IsNullOrWhiteSpace(stdout))
                return null;

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };

            return JsonSerializer.Deserialize<TsExtractionOutput>(stdout, options);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parse failed for extraction output of {FilePath}", filePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run extraction script for {FilePath}", filePath);
            return null;
        }
    }

    private static void ConvertToRecords(
        string workspaceId,
        string projectId,
        string filePath,
        TsExtractionOutput extraction,
        List<CodeSymbolRecord> symbols,
        List<CodeRelationRecord> relations)
    {
        if (extraction.Symbols is null)
            return;

        foreach (var tsSymbol in extraction.Symbols)
        {
            var symbolId = tsSymbol.Id ?? $"TS:{filePath}:{tsSymbol.Name}:{tsSymbol.Kind}";
            var kind = MapSymbolKind(tsSymbol.Kind);

            symbols.Add(new CodeSymbolRecord(
                workspaceId,
                projectId,
                filePath,
                symbolId,
                tsSymbol.Name ?? "unknown",
                kind,
                tsSymbol.StartLine,
                tsSymbol.EndLine,
                tsSymbol.Signature,
                tsSymbol.Container));

            // Create Contains relation if container is specified
            if (!string.IsNullOrEmpty(tsSymbol.Container))
            {
                relations.Add(new CodeRelationRecord(
                    workspaceId,
                    projectId,
                    tsSymbol.Container,
                    symbolId,
                    CodeRelationKind.Contains,
                    tsSymbol.StartLine,
                    filePath));
            }
        }

        // Process relations from extraction output
        if (extraction.Relations is not null)
        {
            foreach (var tsRelation in extraction.Relations)
            {
                var relationKind = MapRelationKind(tsRelation.Kind);
                relations.Add(new CodeRelationRecord(
                    workspaceId,
                    projectId,
                    tsRelation.Source ?? string.Empty,
                    tsRelation.Target ?? string.Empty,
                    relationKind,
                    tsRelation.Line,
                    filePath));
            }
        }
    }

    private static CodeSymbolKind MapSymbolKind(string? kind) =>
        kind?.ToLowerInvariant() switch
        {
            "class" => CodeSymbolKind.Class,
            "interface" => CodeSymbolKind.Interface,
            "function" => CodeSymbolKind.Method,
            "method" => CodeSymbolKind.Method,
            "constructor" => CodeSymbolKind.Constructor,
            "property" => CodeSymbolKind.Property,
            "field" => CodeSymbolKind.Field,
            "variable" => CodeSymbolKind.Variable,
            "constant" => CodeSymbolKind.Constant,
            "enum" => CodeSymbolKind.Enum,
            "type" => CodeSymbolKind.Type,
            "namespace" => CodeSymbolKind.Namespace,
            "parameter" => CodeSymbolKind.Parameter,
            _ => CodeSymbolKind.Unknown,
        };

    private static CodeRelationKind MapRelationKind(string? kind) =>
        kind?.ToLowerInvariant() switch
        {
            "contains" => CodeRelationKind.Contains,
            "calls" => CodeRelationKind.Calls,
            "references" => CodeRelationKind.References,
            "inherits" => CodeRelationKind.Inherits,
            "implements" => CodeRelationKind.Implements,
            "overrides" => CodeRelationKind.Overrides,
            "uses" => CodeRelationKind.Uses,
            _ => CodeRelationKind.Unknown,
        };

    /// <summary>
    /// Maps cross-reference kind strings from the JS project-mode output to CodeRelationKind.
    /// The JS script emits kinds like: call, new, extends, implements, type_ref.
    /// </summary>
    private static CodeRelationKind MapCrossReferenceKind(string? kind) =>
        kind?.ToLowerInvariant() switch
        {
            "call" => CodeRelationKind.Calls,
            "new" => CodeRelationKind.Calls,
            "extends" => CodeRelationKind.Inherits,
            "implements" => CodeRelationKind.Implements,
            "type_ref" => CodeRelationKind.References,
            "references" => CodeRelationKind.References,
            "uses" => CodeRelationKind.Uses,
            _ => CodeRelationKind.Unknown,
        };

    private static string GetLanguage(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".ts" => "TypeScript",
            ".tsx" => "TypeScript JSX",
            ".js" => "JavaScript",
            ".jsx" => "JavaScript JSX",
            _ => "Unknown",
        };

    #region JSON Deserialization Models

    /// <summary>Per-file extraction output (existing single-file mode).</summary>
    private sealed class TsExtractionOutput
    {
        [JsonPropertyName("symbols")]
        public List<TsSymbolEntry>? Symbols { get; set; }

        [JsonPropertyName("relations")]
        public List<TsRelationEntry>? Relations { get; set; }
    }

    private sealed class TsSymbolEntry
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("startLine")]
        public int StartLine { get; set; }

        [JsonPropertyName("endLine")]
        public int EndLine { get; set; }

        [JsonPropertyName("signature")]
        public string? Signature { get; set; }

        [JsonPropertyName("container")]
        public string? Container { get; set; }
    }

    private sealed class TsRelationEntry
    {
        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("target")]
        public string? Target { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("line")]
        public int? Line { get; set; }
    }

    /// <summary>Project-mode extraction output (--project flag).</summary>
    private sealed class TsProjectOutput
    {
        [JsonPropertyName("files")]
        public List<TsProjectFileEntry>? Files { get; set; }

        [JsonPropertyName("crossReferences")]
        public List<TsCrossReferenceEntry>? CrossReferences { get; set; }
    }

    private sealed class TsProjectFileEntry
    {
        [JsonPropertyName("file")]
        public string? File { get; set; }

        [JsonPropertyName("symbols")]
        public List<TsProjectSymbolEntry>? Symbols { get; set; }

        [JsonPropertyName("references")]
        public List<TsProjectReferenceEntry>? References { get; set; }

        [JsonPropertyName("imports")]
        public List<TsImportEntry>? Imports { get; set; }

        [JsonPropertyName("exports")]
        public List<string>? Exports { get; set; }
    }

    private sealed class TsProjectSymbolEntry
    {
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("fullName")]
        public string? FullName { get; set; }

        [JsonPropertyName("line")]
        public int Line { get; set; }

        [JsonPropertyName("signature")]
        public string? Signature { get; set; }

        [JsonPropertyName("modifiers")]
        public string? Modifiers { get; set; }

        [JsonPropertyName("containerName")]
        public string? ContainerName { get; set; }
    }

    private sealed class TsProjectReferenceEntry
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("line")]
        public int Line { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }
    }

    private sealed class TsImportEntry
    {
        [JsonPropertyName("from")]
        public string? From { get; set; }

        [JsonPropertyName("names")]
        public List<string>? Names { get; set; }

        [JsonPropertyName("resolvedFile")]
        public string? ResolvedFile { get; set; }
    }

    private sealed class TsCrossReferenceEntry
    {
        [JsonPropertyName("sourceFile")]
        public string? SourceFile { get; set; }

        [JsonPropertyName("sourceLine")]
        public int SourceLine { get; set; }

        [JsonPropertyName("targetFile")]
        public string? TargetFile { get; set; }

        [JsonPropertyName("targetName")]
        public string? TargetName { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }
    }

    #endregion
}
