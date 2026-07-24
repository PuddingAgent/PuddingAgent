using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.Python;

/// <summary>
/// Python code indexer that extracts symbols by invoking a Python
/// extraction script (Scripts/extract-py-symbols.py) as a subprocess and persists
/// the results through <see cref="ICodeIndexStore"/>.
/// </summary>
public sealed class PythonIndexer : ICodeIndexer
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "__pycache__",
        ".git",
        "venv",
        ".venv",
        "node_modules",
        "bin",
        "obj",
    };

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py",
    };

    private readonly ICodeIndexStore _store;
    private readonly ILogger<PythonIndexer> _logger;

    public PythonIndexer(ICodeIndexStore store, ILogger<PythonIndexer> logger)
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

        // Check Python availability
        var pythonCommand = GetPythonCommand();
        if (pythonCommand is null)
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed,
                "Python not available (tried 'python' and 'python3')",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId,
                StartedAtUtc: startedAt);
        }

        // Locate the extraction script relative to the project root
        var scriptPath = Path.Combine(descriptor.ProjectPath, "Scripts", "extract-py-symbols.py");
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
                    "No Python files found.",
                    WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId,
                    StartedAtUtc: startedAt, CompletedAtUtc: DateTimeOffset.UtcNow);
            }

            var allSymbols = new List<CodeSymbolRecord>();
            var allRelations = new List<CodeRelationRecord>();
            var allFiles = new List<CodeFileRecord>();
            var now = DateTimeOffset.UtcNow;
            var errorCount = 0;

            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var extractionResult = await RunExtractionScriptAsync(pythonCommand, scriptPath, filePath, cancellationToken)
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
                        "Python", now));
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

            _logger.LogInformation(
                "Python indexing for {ProjectId}: {SymbolCount} symbols, {RelationCount} relations in {FileCount} files ({ErrorCount} errors)",
                descriptor.ProjectId, allSymbols.Count, allRelations.Count, allFiles.Count, errorCount);

            return new CodeIndexResult(true, CodeIndexStatus.Completed,
                $"Indexing complete. {allSymbols.Count} symbols in {allFiles.Count} files.",
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
            _logger.LogError(ex, "Python indexing failed for {ProjectId}", descriptor.ProjectId);
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

    private static string? GetPythonCommand()
    {
        foreach (var cmd in new[] { "python", "python3" })
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = cmd,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                process.Start();
                process.WaitForExit(5000);
                if (process.ExitCode == 0)
                    return cmd;
            }
            catch
            {
                // Try next command
            }
        }
        return null;
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
            if (SupportedExtensions.Contains(ext))
                result.Add(file);
        }

        foreach (var subDir in Directory.EnumerateDirectories(directory))
        {
            var dirName = Path.GetFileName(subDir);
            if (ExcludedDirectories.Contains(dirName))
                continue;

            CollectSourceFilesRecursive(subDir, result);
        }
    }

    private async Task<PyExtractionOutput?> RunExtractionScriptAsync(
        string pythonCommand,
        string scriptPath,
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = pythonCommand,
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

            return JsonSerializer.Deserialize<PyExtractionOutput>(stdout, options);
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
        PyExtractionOutput extraction,
        List<CodeSymbolRecord> symbols,
        List<CodeRelationRecord> relations)
    {
        if (extraction.Symbols is null)
            return;

        foreach (var pySymbol in extraction.Symbols)
        {
            var symbolId = $"PY:{filePath}:{pySymbol.Name}:{pySymbol.Kind}:{pySymbol.Line}";
            var kind = MapSymbolKind(pySymbol.Kind);

            symbols.Add(new CodeSymbolRecord(
                workspaceId,
                projectId,
                filePath,
                symbolId,
                pySymbol.Name ?? "unknown",
                kind,
                pySymbol.Line,
                pySymbol.Line, // Python AST doesn't easily give end line in our simple extraction
                pySymbol.Signature,
                pySymbol.ContainerName));

            // Create Contains relation if container is specified
            if (!string.IsNullOrEmpty(pySymbol.ContainerName))
            {
                relations.Add(new CodeRelationRecord(
                    workspaceId,
                    projectId,
                    pySymbol.ContainerName,
                    symbolId,
                    CodeRelationKind.Contains,
                    pySymbol.Line,
                    filePath));
            }
        }

        // Process references as Calls relations
        if (extraction.References is not null)
        {
            foreach (var pyRef in extraction.References)
            {
                var relationKind = MapReferenceKind(pyRef.Kind);
                relations.Add(new CodeRelationRecord(
                    workspaceId,
                    projectId,
                    filePath,
                    pyRef.Name ?? string.Empty,
                    relationKind,
                    pyRef.Line,
                    filePath));
            }
        }
    }

    private static CodeSymbolKind MapSymbolKind(string? kind) =>
        kind?.ToLowerInvariant() switch
        {
            "class" => CodeSymbolKind.Class,
            "function" => CodeSymbolKind.Method,
            "method" => CodeSymbolKind.Method,
            "async_func" => CodeSymbolKind.Method,
            _ => CodeSymbolKind.Unknown,
        };

    private static CodeRelationKind MapReferenceKind(string? kind) =>
        kind?.ToLowerInvariant() switch
        {
            "call" => CodeRelationKind.Calls,
            "import" => CodeRelationKind.References,
            "decorator" => CodeRelationKind.Uses,
            _ => CodeRelationKind.Unknown,
        };

    #region JSON Deserialization Models

    private sealed class PyExtractionOutput
    {
        [JsonPropertyName("symbols")]
        public List<PySymbolEntry>? Symbols { get; set; }

        [JsonPropertyName("references")]
        public List<PyReferenceEntry>? References { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private sealed class PySymbolEntry
    {
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("line")]
        public int Line { get; set; }

        [JsonPropertyName("signature")]
        public string? Signature { get; set; }

        [JsonPropertyName("containerName")]
        public string? ContainerName { get; set; }
    }

    private sealed class PyReferenceEntry
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("line")]
        public int Line { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }
    }

    #endregion
}
