using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Extractors;
using PuddingCodeIntelligence.Services;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services;

namespace PuddingCodeIntelligence.Python;

/// <summary>
/// Python code indexer that extracts symbols by invoking a Python
/// extraction script (the component-owned asset <c>Scripts/extract-py-symbols.py</c>, resolved from
/// the directory holding this assembly through <see cref="IExtractorAssetResolver"/>) as a
/// subprocess and persists the results through <see cref="ICodeIndexStore"/>.
/// Supports two modes: project-level extraction (--project) for cross-file references,
/// and per-file extraction as a fallback.
/// </summary>
public sealed class PythonIndexer : ICodeIndexer, ICodeIndexFileUpdater, ICodeIndexFileBatchUpdater, ILanguageCodeIndexer
{
    // Uses centralized IndexExcludePatterns.NoiseDirNames for directory exclusion.

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py",
    };

    private readonly ICodeIndexStore _store;
    private readonly ILogger<PythonIndexer> _logger;
    private readonly IExtractorAssetResolver _assetResolver;

    /// <summary>Creates an indexer whose extractor assets are resolved from the component assembly directory.</summary>
    public PythonIndexer(ICodeIndexStore store, ILogger<PythonIndexer> logger)
        : this(store, logger, new ExtractorAssetResolver())
    {
    }

    /// <summary>Creates an indexer with an injected extractor asset resolver.</summary>
    public PythonIndexer(ICodeIndexStore store, ILogger<PythonIndexer> logger, IExtractorAssetResolver assetResolver)
    {
        ArgumentNullException.ThrowIfNull(assetResolver);

        _store = store;
        _logger = logger;
        _assetResolver = assetResolver;
    }

    /// <inheritdoc />
    public string Language => "Python";

    /// <inheritdoc />
    // Explicit implementation: the class already declares a private static field of this name, and that
    // field stays the single source of the extension set - no existing member is renamed or touched.
    IReadOnlyCollection<string> ILanguageCodeIndexer.SupportedExtensions => SupportedExtensions;

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

        // Resolve the component-owned extractor script: it belongs to this component and lives next
        // to its assembly, never under the indexed project (see IExtractorAssetResolver).
        var assets = _assetResolver.Resolve(ExtractorAssetKind.PythonScript);
        if (!assets.Success)
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed,
                assets.Message,
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId,
                StartedAtUtc: startedAt);
        }

        var scriptPath = assets.ScriptPath!;

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
            var usedProjectMode = false;

            // Try project-level extraction first (single call, captures cross-file references)
            var projectResult = await RunProjectExtractionAsync(pythonCommand, scriptPath, descriptor.ProjectPath, cancellationToken)
                .ConfigureAwait(false);

            if (projectResult is not null && projectResult.Files is { Count: > 0 })
            {
                usedProjectMode = true;
                _logger.LogInformation(
                    "Python project-mode extraction for {ProjectId}: {FileCount} files, {CrossRefCount} cross-references",
                    descriptor.ProjectId, projectResult.Files.Count,
                    projectResult.CrossReferences?.Count ?? 0);

                // 清旧符号仍留在**调用方**：投影函数保持只读（批量接缝复用同一投影时绝不能写库）。
                // 顺序与原实现等价：现在的清全部发生在写入之前，而原实现在同一次遍历里交错清理，
                // 每次清理只作用于该文件自己，之后才统一 upsert，因此结果一致。
                await ClearProjectModeFilesAsync(
                        descriptor.WorkspaceId, descriptor.ProjectId, descriptor.ProjectPath,
                        projectResult, cancellationToken)
                    .ConfigureAwait(false);

                ProcessProjectExtraction(descriptor.WorkspaceId, descriptor.ProjectId,
                    descriptor.ProjectPath, projectResult, allSymbols, allRelations, allFiles, now);
            }
            else
            {
                // Fallback: per-file extraction (existing behavior)
                _logger.LogInformation(
                    "Python project-mode not available for {ProjectId}, falling back to per-file extraction",
                    descriptor.ProjectId);

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
                "Python indexing ({Mode}) for {ProjectId}: {SymbolCount} symbols, {RelationCount} relations in {FileCount} files ({ErrorCount} errors)",
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
            _logger.LogError(ex, "Python indexing failed for {ProjectId}", descriptor.ProjectId);
            return new CodeIndexResult(false, CodeIndexStatus.Failed, $"Indexing failed: {ex.Message}",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId,
                StartedAtUtc: startedAt);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Per-file increment (U3-B3): the file is extracted on its own and only its rows are replaced. Every
    /// condition this indexer cannot handle (Python missing, extraction script missing, unusable extraction
    /// output, not a Python file, noise path) is reported as <see cref="CodeIndexStatus.Failed"/> so the
    /// caller escalates to a scope-level run instead of losing the change.
    /// </remarks>
    public async Task<CodeIndexResult> IndexFileAsync(
        CodeWorkspaceDescriptor descriptor,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (string.IsNullOrWhiteSpace(descriptor.WorkspaceId) || string.IsNullOrWhiteSpace(descriptor.ProjectId))
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed,
                "WorkspaceId and ProjectId are required.");
        }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed,
                $"File does not exist: {filePath}",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId);
        }

        if (!SupportedExtensions.Contains(Path.GetExtension(filePath)) ||
            IndexExcludePatterns.IsNoisePathBelow(descriptor.ProjectPath, filePath))
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed,
                $"Not an indexable Python file: {filePath}",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId);
        }

        if (string.IsNullOrWhiteSpace(descriptor.ProjectPath) || !Directory.Exists(descriptor.ProjectPath))
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed,
                $"Project path does not exist: {descriptor.ProjectPath}",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId);
        }

        var pythonCommand = GetPythonCommand();
        if (pythonCommand is null)
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed,
                "Python not available (tried 'python' and 'python3')",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId);
        }

        // Same component-owned asset as IndexWorkspaceAsync: never resolved from the indexed project.
        var assets = _assetResolver.Resolve(ExtractorAssetKind.PythonScript);
        if (!assets.Success)
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed,
                assets.Message,
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId);
        }

        var scriptPath = assets.ScriptPath!;

        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            var extraction = await RunExtractionScriptAsync(pythonCommand, scriptPath, filePath, cancellationToken)
                .ConfigureAwait(false);

            if (extraction is null)
            {
                return new CodeIndexResult(false, CodeIndexStatus.Failed,
                    $"Extraction produced no usable output for {filePath}",
                    WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId,
                    StartedAtUtc: startedAt);
            }

            var symbols = new List<CodeSymbolRecord>();
            var relations = new List<CodeRelationRecord>();
            ConvertToRecords(descriptor.WorkspaceId, descriptor.ProjectId, filePath, extraction, symbols, relations);

            await _store.ClearSymbolsForFileAsync(
                descriptor.WorkspaceId, descriptor.ProjectId, filePath, cancellationToken).ConfigureAwait(false);

            if (symbols.Count == 0)
            {
                // No declarations: a full run would not have a file record for it either.
                await _store.RemoveFilesAsync(
                    descriptor.WorkspaceId, descriptor.ProjectId, [filePath], cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _store.UpsertFilesAsync(
                    descriptor.WorkspaceId, descriptor.ProjectId,
                    [new CodeFileRecord(descriptor.WorkspaceId, descriptor.ProjectId, filePath, "Python", DateTimeOffset.UtcNow)],
                    cancellationToken).ConfigureAwait(false);
                await _store.UpsertSymbolsAsync(
                    descriptor.WorkspaceId, descriptor.ProjectId, symbols, cancellationToken).ConfigureAwait(false);
                await _store.UpsertRelationsAsync(
                    descriptor.WorkspaceId, descriptor.ProjectId, relations, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Python indexed file {FilePath}: {SymbolCount} symbols, {RelationCount} relations",
                filePath, symbols.Count, relations.Count);

            return new CodeIndexResult(true, CodeIndexStatus.Completed,
                $"File indexed: {filePath}",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId,
                StartedAtUtc: startedAt, CompletedAtUtc: DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed, "Indexing was cancelled.",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId, StartedAtUtc: startedAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Python indexing of file {FilePath} failed", filePath);
            return new CodeIndexResult(false, CodeIndexStatus.Failed, $"Indexing failed: {ex.Message}",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId, StartedAtUtc: startedAt);
        }
    }

    /// <summary>
    /// **批量更新**（D4，2026-10-02）：一个批次只跑**一次**项目级提取进程，逐文件产出 payload，
    /// <b>不写任何索引</b>（原子提交由调用方经 <c>ReplaceFilesAsync</c> 完成）。
    /// <para>
    /// 与 TypeScript 侧同一形状：路由（非 <c>.py</c>、噪声路径）⇒ `NotApplicable`；
    /// 工程根缺失 / Python 不可用 / 资产缺失 ⇒ 全部 `Retryable`（前两者不启动提取器）；
    /// 项目模式没覆盖到的请求路径退化逐文件提取，并把这批的 `SessionKey` 置空
    /// —— 它如实区分「批次复用了一次工程快照」与「退化成逐文件提取」。
    /// </para>
    /// </summary>
    /// <inheritdoc />
    public async Task<CodeIndexFileBatchResult> UpdateFilesAsync(
        CodeWorkspaceDescriptor descriptor,
        IReadOnlyCollection<string> filePaths,
        CodeIndexBatchContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(context);

        var paths = (filePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (paths.Length == 0)
            return new CodeIndexFileBatchResult([], context.ConfigurationFingerprint, SessionKey: null);

        var outcomes = new List<CodeFileIndexOutcome>(paths.Length);
        var applicable = new List<string>();

        foreach (var path in paths)
        {
            if (!SupportedExtensions.Contains(Path.GetExtension(path)))
            {
                outcomes.Add(new CodeFileIndexOutcome(
                    path, CodeIndexConsumerStatus.NotApplicable, Reason: "not a Python file"));
                continue;
            }

            if (IndexExcludePatterns.IsNoisePathBelow(descriptor.ProjectPath, path))
            {
                outcomes.Add(new CodeFileIndexOutcome(
                    path, CodeIndexConsumerStatus.NotApplicable, Reason: "excluded from indexing"));
                continue;
            }

            applicable.Add(path);
        }

        if (applicable.Count == 0)
            return new CodeIndexFileBatchResult(OrderOutcomes(outcomes), context.ConfigurationFingerprint, SessionKey: null);

        if (string.IsNullOrWhiteSpace(descriptor.ProjectPath) || !Directory.Exists(descriptor.ProjectPath))
        {
            AddRetryable(outcomes, applicable, $"Project path does not exist: {descriptor.ProjectPath}");
            return new CodeIndexFileBatchResult(OrderOutcomes(outcomes), context.ConfigurationFingerprint, SessionKey: null);
        }

        var pythonCommand = GetPythonCommand();
        if (pythonCommand is null)
        {
            AddRetryable(outcomes, applicable, "Python not available (tried 'python' and 'python3')");
            return new CodeIndexFileBatchResult(OrderOutcomes(outcomes), context.ConfigurationFingerprint, SessionKey: null);
        }

        var assets = _assetResolver.Resolve(ExtractorAssetKind.PythonScript);
        if (!assets.Success)
        {
            AddRetryable(outcomes, applicable, assets.Message);
            return new CodeIndexFileBatchResult(OrderOutcomes(outcomes), context.ConfigurationFingerprint, SessionKey: null);
        }

        var scriptPath = assets.ScriptPath!;
        var now = DateTimeOffset.UtcNow;
        string? sessionKey = null;

        try
        {
            // ⚠️ 整个批次只跑一次项目级提取。
            var projectResult = await RunProjectExtractionAsync(pythonCommand, scriptPath, descriptor.ProjectPath, cancellationToken)
                .ConfigureAwait(false);

            var payloads = new Dictionary<string, CodeFileIndexPayload>(StringComparer.OrdinalIgnoreCase);
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (projectResult is not null && projectResult.Files is { Count: > 0 })
            {
                sessionKey = $"python:project:{descriptor.ProjectPath}:{context.ConfigurationFingerprint}";

                var allSymbols = new List<CodeSymbolRecord>();
                var allRelations = new List<CodeRelationRecord>();
                var allFiles = new List<CodeFileRecord>();

                // 只读投影：任何持久化都由调用方经 ReplaceFilesAsync 完成。
                ProcessProjectExtraction(
                    descriptor.WorkspaceId, descriptor.ProjectId, descriptor.ProjectPath,
                    projectResult, allSymbols, allRelations, allFiles, now);

                foreach (var file in allFiles)
                    covered.Add(file.FilePath);

                foreach (var path in applicable)
                {
                    var symbols = allSymbols
                        .Where(symbol => IsSamePath(symbol.FilePath, path))
                        .ToList();

                    if (symbols.Count == 0)
                        continue;

                    payloads[path] = new CodeFileIndexPayload(
                        path,
                        symbols,
                        [],
                        allRelations.Where(relation => IsSamePath(relation.SourceFilePath, path)).ToList());
                }
            }

            foreach (var path in applicable)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (payloads.TryGetValue(path, out var payload))
                {
                    outcomes.Add(new CodeFileIndexOutcome(path, CodeIndexConsumerStatus.Applied, payload));
                    continue;
                }

                if (covered.Contains(path))
                {
                    // 项目模式看到了这个文件但没有任何符号：已处理、无符号，不是失败。
                    outcomes.Add(new CodeFileIndexOutcome(
                        path,
                        CodeIndexConsumerStatus.Applied,
                        new CodeFileIndexPayload(path, [], [], [])));
                    continue;
                }

                sessionKey = null;

                var extraction = await RunExtractionScriptAsync(pythonCommand, scriptPath, path, cancellationToken)
                    .ConfigureAwait(false);

                if (extraction is null)
                {
                    outcomes.Add(new CodeFileIndexOutcome(
                        path, CodeIndexConsumerStatus.Retryable,
                        Reason: $"Extraction produced no usable output for {path}"));
                    continue;
                }

                var fileSymbols = new List<CodeSymbolRecord>();
                var fileRelations = new List<CodeRelationRecord>();
                ConvertToRecords(descriptor.WorkspaceId, descriptor.ProjectId, path, extraction, fileSymbols, fileRelations);

                outcomes.Add(new CodeFileIndexOutcome(
                    path,
                    CodeIndexConsumerStatus.Applied,
                    new CodeFileIndexPayload(path, fileSymbols, [], fileRelations)));
            }

            return new CodeIndexFileBatchResult(OrderOutcomes(outcomes), context.ConfigurationFingerprint, sessionKey);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Python batch update failed for {ProjectId}", descriptor.ProjectId);
            AddRetryable(outcomes, applicable, $"Python batch update failed: {ex.Message}");
            return new CodeIndexFileBatchResult(OrderOutcomes(outcomes), context.ConfigurationFingerprint, SessionKey: null);
        }
    }

    private static void AddRetryable(
        List<CodeFileIndexOutcome> outcomes,
        IReadOnlyCollection<string> paths,
        string reason)
    {
        foreach (var path in paths)
            outcomes.Add(new CodeFileIndexOutcome(path, CodeIndexConsumerStatus.Retryable, Reason: reason));
    }

    private static IReadOnlyList<CodeFileIndexOutcome> OrderOutcomes(List<CodeFileIndexOutcome> outcomes) =>
        outcomes.OrderBy(outcome => outcome.FilePath, StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool IsSamePath(string? candidate, string filePath)
    {
        if (string.IsNullOrEmpty(candidate))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(candidate), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
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
            // 目录层已按名字剪枝；这里是「文件本身就叫 bin/obj」的兼容判断（不再做绝对路径扫描）。
            if (SupportedExtensions.Contains(ext) &&
                !IndexExcludePatterns.IsNoiseDirectoryName(Path.GetFileName(file)))
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
    /// Runs the extraction script in project mode: python extract-py-symbols.py --project &lt;directory&gt;
    /// Returns the parsed project output, or null if the script fails or is not supported.
    /// </summary>
    private async Task<PyProjectOutput?> RunProjectExtractionAsync(
        string pythonCommand,
        string scriptPath,
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = pythonCommand,
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

            var result = JsonSerializer.Deserialize<PyProjectOutput>(stdout, options);

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
    /// <summary>
    /// Clears the previous symbol graph of every file the project-mode extraction covers.
    /// <para>
    /// Deliberately the <b>caller's</b> job: the projection itself is read-only so the batch seam can reuse it
    /// without writing anything (the batch hands its payload to the caller's atomic replace).
    /// </para>
    /// </summary>
    private async Task ClearProjectModeFilesAsync(
        string workspaceId,
        string projectId,
        string projectRoot,
        PyProjectOutput projectOutput,
        CancellationToken cancellationToken)
    {
        if (projectOutput.Files is null)
            return;

        foreach (var fileEntry in projectOutput.Files)
        {
            if (string.IsNullOrEmpty(fileEntry.File))
                continue;

            cancellationToken.ThrowIfCancellationRequested();

            var absolutePath = Path.GetFullPath(Path.Combine(projectRoot, fileEntry.File));
            await _store.ClearSymbolsForFileAsync(workspaceId, projectId, absolutePath, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Processes project-mode extraction output: converts file entries to symbol/file records and
    /// cross-references to relation records. <b>Reads and writes nothing</b> — persistence is the caller's job.
    /// </summary>
    private void ProcessProjectExtraction(
        string workspaceId,
        string projectId,
        string projectRoot,
        PyProjectOutput projectOutput,
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

            if (fileEntry.Symbols is { Count: > 0 })
            {
                foreach (var pySymbol in fileEntry.Symbols)
                {
                    var symbolId = $"PY:{absolutePath}:{pySymbol.Name}:{pySymbol.Kind}:{pySymbol.Line}";
                    var kind = MapSymbolKind(pySymbol.Kind);

                    allSymbols.Add(new CodeSymbolRecord(
                        workspaceId,
                        projectId,
                        absolutePath,
                        symbolId,
                        pySymbol.Name ?? "unknown",
                        kind,
                        pySymbol.Line,
                        pySymbol.Line, // Python AST doesn't easily give end line in our simple extraction
                        pySymbol.Signature,
                        pySymbol.ContainerName));

                    // Register in lookup for cross-reference resolution
                    var lookupKey = fileEntry.File + "|" + (pySymbol.Name ?? "");
                    symbolIdLookup[lookupKey] = symbolId;

                    // Contains relation for nested symbols
                    if (!string.IsNullOrEmpty(pySymbol.ContainerName))
                    {
                        var containerId = $"PY:{absolutePath}:{pySymbol.ContainerName}";
                        allRelations.Add(new CodeRelationRecord(
                            workspaceId,
                            projectId,
                            containerId,
                            symbolId,
                            CodeRelationKind.Contains,
                            pySymbol.Line,
                            absolutePath));
                    }
                }

                allFiles.Add(new CodeFileRecord(
                    workspaceId, projectId, absolutePath,
                    "Python", now));
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
                    : $"PY:{targetAbsolutePath}:{crossRef.TargetName}";

                // Source symbol ID: use file-level reference since we don't know the exact containing symbol
                var sourceSymbolId = $"PY:{sourceAbsolutePath}:{crossRef.SourceLine}";

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

    /// <summary>
    /// Maps cross-reference kind strings from the Python project-mode output to CodeRelationKind.
    /// The Python script emits kinds like: call, import, decorator.
    /// </summary>
    private static CodeRelationKind MapCrossReferenceKind(string? kind) =>
        kind?.ToLowerInvariant() switch
        {
            "call" => CodeRelationKind.Calls,
            "import" => CodeRelationKind.References,
            "decorator" => CodeRelationKind.Uses,
            _ => CodeRelationKind.Unknown,
        };

    #region JSON Deserialization Models

    /// <summary>Per-file extraction output (existing single-file mode).</summary>
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

    /// <summary>Project-mode extraction output (--project flag).</summary>
    private sealed class PyProjectOutput
    {
        [JsonPropertyName("files")]
        public List<PyProjectFileEntry>? Files { get; set; }

        [JsonPropertyName("crossReferences")]
        public List<PyCrossReferenceEntry>? CrossReferences { get; set; }
    }

    private sealed class PyProjectFileEntry
    {
        [JsonPropertyName("file")]
        public string? File { get; set; }

        [JsonPropertyName("symbols")]
        public List<PySymbolEntry>? Symbols { get; set; }

        [JsonPropertyName("references")]
        public List<PyReferenceEntry>? References { get; set; }

        [JsonPropertyName("imports")]
        public List<PyImportEntry>? Imports { get; set; }

        [JsonPropertyName("exports")]
        public List<string>? Exports { get; set; }
    }

    private sealed class PyImportEntry
    {
        [JsonPropertyName("from")]
        public string? From { get; set; }

        [JsonPropertyName("names")]
        public List<string>? Names { get; set; }

        [JsonPropertyName("resolvedFile")]
        public string? ResolvedFile { get; set; }
    }

    private sealed class PyCrossReferenceEntry
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
