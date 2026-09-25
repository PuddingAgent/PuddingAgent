using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.Extensions.Logging;

using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services;
using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.CSharp;

/// <summary>
/// C# code indexer that extracts declarations, Contains/Calls relations, and references
/// from Roslyn compilations and persists them through <see cref="ICodeIndexStore"/>.
/// </summary>
public sealed class RoslynCSharpIndexer : ICodeIndexer, ICodeIndexFileUpdater, ILanguageCodeIndexer
{


    private readonly ICodeIndexStore _store;
    private readonly ILogger _logger;

    public RoslynCSharpIndexer(ICodeIndexStore store, ILogger<RoslynCSharpIndexer> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Language => "C#";

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions => CSharpFileExtensions;

    /// <summary>
    /// The extension this indexer owns for per-file routing: C# files are normally resolved through the
    /// Roslyn workspace rather than by extension, so this set exists precisely so that the aggregate can
    /// hand a single changed file to its owner instead of guessing.
    /// </summary>
    private static readonly HashSet<string> CSharpFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
    };

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

        var hasWork = descriptor.SolutionPath is { Length: > 0 }
            || (descriptor.ProjectFilePaths is { Count: > 0 }
                && descriptor.ProjectFilePaths.Any(p =>
                    string.Equals(Path.GetExtension(p), ".csproj", StringComparison.OrdinalIgnoreCase)));

        if (!hasWork)
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed,
                "No .sln, .slnx, or .csproj found in the project.",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId);
        }

        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            var bootstrapper = new RoslynWorkspaceBootstrapper(_logger);
            using var roslynWorkspace = await bootstrapper.OpenWorkspaceAsync(descriptor, cancellationToken)
                .ConfigureAwait(false);

            return await IndexWorkspaceCoreAsync(
                roslynWorkspace.CurrentSolution,
                descriptor.WorkspaceId,
                descriptor.ProjectId,
                startedAt,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed, "Indexing was cancelled.",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId, StartedAtUtc: startedAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Indexing failed for {ProjectId}", descriptor.ProjectId);
            return new CodeIndexResult(false, CodeIndexStatus.Failed, $"Indexing failed: {ex.Message}",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId, StartedAtUtc: startedAt);
        }
    }

    /// <summary>
    /// Indexes a pre-built compilation. Exposed for test contexts that bypass
    /// MSBuild workspace loading.
    /// </summary>
    internal async Task<CodeIndexResult> IndexCompilationAsync(
        Compilation compilation,
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            using var workspace = new AdhocWorkspace();
            var projectInfo = ProjectInfo.Create(
                ProjectId.CreateNewId(), VersionStamp.Default, projectId, projectId, LanguageNames.CSharp)
                .WithMetadataReferences(compilation.References)
                .WithCompilationOptions(compilation.Options);

            var roslynSolution = workspace.CurrentSolution.AddProject(projectInfo);

            var roslynProjectId = roslynSolution.ProjectIds.Single();

            foreach (var tree in compilation.SyntaxTrees)
            {
                var docInfo = DocumentInfo.Create(
                    DocumentId.CreateNewId(roslynProjectId),
                    Path.GetFileName(tree.FilePath),
                    loader: TextLoader.From(TextAndVersion.Create(tree.GetRoot().GetText(), VersionStamp.Default)),
                    filePath: tree.FilePath);
                roslynSolution = roslynSolution.AddDocument(docInfo);
            }

            return await IndexWorkspaceCoreAsync(
                roslynSolution, workspaceId, projectId, startedAt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed, "Indexing was cancelled.",
                WorkspaceId: workspaceId, ProjectId: projectId, StartedAtUtc: startedAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Indexing failed for {ProjectId}", projectId);
            return new CodeIndexResult(false, CodeIndexStatus.Failed, $"Indexing failed: {ex.Message}",
                WorkspaceId: workspaceId, ProjectId: projectId, StartedAtUtc: startedAt);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Per-file increment (U3-B3): only the rows of <paramref name="filePath"/> are replaced instead of
    /// re-reading every file of the scope. A file that was deleted, that is not a C# file of the loaded
    /// workspace, or that lives under a noise directory is reported as <see cref="CodeIndexStatus.Failed"/>:
    /// the caller escalates to a scope-level run rather than trusting a half-done per-file update.
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

        if (IsNoiseFile(filePath, descriptor.ProjectPath))
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed,
                $"File is excluded from indexing: {filePath}",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId);
        }

        if (string.IsNullOrWhiteSpace(descriptor.ProjectPath) || !Directory.Exists(descriptor.ProjectPath))
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed,
                $"Project path does not exist: {descriptor.ProjectPath}",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId);
        }

        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            var bootstrapper = new RoslynWorkspaceBootstrapper(_logger);
            using var roslynWorkspace = await bootstrapper.OpenWorkspaceAsync(descriptor, cancellationToken)
                .ConfigureAwait(false);

            var (compilation, syntaxTree) = await FindCompilationForFileAsync(
                roslynWorkspace.CurrentSolution, filePath, cancellationToken).ConfigureAwait(false);

            if (compilation is null || syntaxTree is null)
            {
                return new CodeIndexResult(false, CodeIndexStatus.Failed,
                    $"File is not part of the loaded C# workspace: {filePath}",
                    WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId,
                    StartedAtUtc: startedAt);
            }

            // The previous rows of this file go away as part of the per-file write, exactly like the
            // per-file step of a full run (deleted symbols must never persist).
            await _store.ClearSymbolsForFileAsync(
                descriptor.WorkspaceId, descriptor.ProjectId, filePath, cancellationToken).ConfigureAwait(false);

            var extracted = await ExtractFileAsync(
                compilation, syntaxTree, descriptor.WorkspaceId, descriptor.ProjectId, cancellationToken)
                .ConfigureAwait(false);

            if (extracted.Symbols.Count == 0)
            {
                // A file without declarations has no file record in a full run either, so the record is
                // dropped instead of being left behind as a stale entry.
                await _store.RemoveFilesAsync(
                    descriptor.WorkspaceId, descriptor.ProjectId, [filePath], cancellationToken).ConfigureAwait(false);

                return new CodeIndexResult(true, CodeIndexStatus.Completed,
                    $"File indexed without symbols: {filePath}",
                    WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId,
                    StartedAtUtc: startedAt, CompletedAtUtc: DateTimeOffset.UtcNow);
            }

            await _store.UpsertFilesAsync(
                descriptor.WorkspaceId, descriptor.ProjectId,
                [new CodeFileRecord(descriptor.WorkspaceId, descriptor.ProjectId, filePath, "C#", DateTimeOffset.UtcNow)],
                cancellationToken).ConfigureAwait(false);
            await _store.UpsertSymbolsAsync(
                descriptor.WorkspaceId, descriptor.ProjectId, extracted.Symbols, cancellationToken).ConfigureAwait(false);
            await _store.UpsertRelationsAsync(
                descriptor.WorkspaceId, descriptor.ProjectId, extracted.Relations, cancellationToken).ConfigureAwait(false);
            await _store.UpsertReferencesAsync(
                descriptor.WorkspaceId, descriptor.ProjectId, extracted.References, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Indexed file {FilePath}: {SymbolCount} symbols, {RelationCount} relations, {ReferenceCount} references",
                filePath, extracted.Symbols.Count, extracted.Relations.Count, extracted.References.Count);

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
            _logger.LogError(ex, "Indexing file {FilePath} failed", filePath);
            return new CodeIndexResult(false, CodeIndexStatus.Failed, $"Indexing failed: {ex.Message}",
                WorkspaceId: descriptor.WorkspaceId, ProjectId: descriptor.ProjectId, StartedAtUtc: startedAt);
        }
    }

    /// <summary>Records extracted from a single syntax tree.</summary>
    private sealed record FileExtraction(
        List<CodeSymbolRecord> Symbols,
        List<CodeRelationRecord> Relations,
        List<CodeReferenceRecord> References);

    /// <summary>Extracts the records of one syntax tree. Reads no cross-file state and writes nothing.</summary>
    private static async Task<FileExtraction> ExtractFileAsync(
        Compilation compilation,
        SyntaxTree syntaxTree,
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken)
    {
        var filePath = syntaxTree.FilePath;
        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);

        var symbols = new List<CodeSymbolRecord>();
        var relations = new List<CodeRelationRecord>();
        var references = new List<CodeReferenceRecord>();

        ExtractSymbols(root, semanticModel, workspaceId, projectId, filePath,
            symbols, relations, cancellationToken);
        ExtractReferences(root, semanticModel, workspaceId, projectId, filePath,
            relations, references, cancellationToken);

        return new FileExtraction(symbols, relations, references);
    }

    /// <summary>Finds the compilation and syntax tree that own <paramref name="filePath"/>.</summary>
    private static async Task<(Compilation? Compilation, SyntaxTree? SyntaxTree)> FindCompilationForFileAsync(
        Solution solution,
        string filePath,
        CancellationToken cancellationToken)
    {
        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp)
                continue;

            var document = project.Documents.FirstOrDefault(
                candidate => candidate.FilePath is { Length: > 0 } candidatePath && IsSamePath(candidatePath, filePath));

            if (document is null)
                continue;

            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);

            if (syntaxTree is not null && compilation is not null)
                return (compilation, syntaxTree);
        }

        return (null, null);
    }

    private static bool IsSamePath(string candidate, string filePath)
    {
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

    private async Task<CodeIndexResult> IndexWorkspaceCoreAsync(
        Solution solution,
        string workspaceId,
        string projectId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var projectIds = solution.ProjectIds;
        if (projectIds.Count == 0)
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed,
                "No C# projects loaded from the workspace.",
                WorkspaceId: workspaceId, ProjectId: projectId, StartedAtUtc: startedAt);
        }

        var allSymbols = new List<CodeSymbolRecord>();
        var allRelations = new List<CodeRelationRecord>();
        var allReferences = new List<CodeReferenceRecord>();
        var allFiles = new List<CodeFileRecord>();
        var now = DateTimeOffset.UtcNow;

        foreach (var pid in projectIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = solution.GetProject(pid);
            if (project is null || project.Language != LanguageNames.CSharp)
                continue;

            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
                continue;

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var filePath = syntaxTree.FilePath;
                // 根取该项目自己的目录（csproj 所在目录）；跨项目/链接文件由 IsNoisePathBelow 降级处理。
                if (string.IsNullOrWhiteSpace(filePath) ||
                    IsNoiseFile(filePath, Path.GetDirectoryName(project.FilePath)))
                    continue;

                // Clear stale symbols for this file before re-indexing.
                // Ensures deleted symbols don't persist across re-index runs.
                await _store.ClearSymbolsForFileAsync(workspaceId, projectId, filePath, cancellationToken)
                    .ConfigureAwait(false);

                var extracted = await ExtractFileAsync(
                        compilation, syntaxTree, workspaceId, projectId, cancellationToken)
                    .ConfigureAwait(false);

                if (extracted.Symbols.Count > 0)
                {
                    allSymbols.AddRange(extracted.Symbols);
                    allRelations.AddRange(extracted.Relations);
                    allReferences.AddRange(extracted.References);
                    allFiles.Add(new CodeFileRecord(workspaceId, projectId, filePath,
                        "C#", now));
                }
            }
        }

        await _store.UpsertFilesAsync(workspaceId, projectId, allFiles, cancellationToken)
            .ConfigureAwait(false);
        await _store.UpsertSymbolsAsync(workspaceId, projectId, allSymbols, cancellationToken)
            .ConfigureAwait(false);
        await _store.UpsertRelationsAsync(workspaceId, projectId, allRelations, cancellationToken)
            .ConfigureAwait(false);
        await _store.UpsertReferencesAsync(workspaceId, projectId, allReferences, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Indexed {ProjectId}: {SymbolCount} symbols, {RelationCount} relations, {ReferenceCount} references in {FileCount} files",
            projectId, allSymbols.Count, allRelations.Count, allReferences.Count, allFiles.Count);

        return new CodeIndexResult(true, CodeIndexStatus.Completed,
            "Indexing complete.",
            WorkspaceId: workspaceId, ProjectId: projectId,
            StartedAtUtc: startedAt, CompletedAtUtc: DateTimeOffset.UtcNow);
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

    private static void ExtractSymbols(
        SyntaxNode root,
        SemanticModel semanticModel,
        string workspaceId,
        string projectId,
        string filePath,
        List<CodeSymbolRecord> symbols,
        List<CodeRelationRecord> relations,
        CancellationToken cancellationToken)
    {
        foreach (var node in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessNode(node, semanticModel, workspaceId, projectId, filePath, symbols, relations);
        }
    }

    private static void ProcessNode(
        SyntaxNode node,
        SemanticModel semanticModel,
        string workspaceId,
        string projectId,
        string filePath,
        List<CodeSymbolRecord> symbols,
        List<CodeRelationRecord> relations)
    {
        switch (node)
        {
            case NamespaceDeclarationSyntax ns:
            {
                var symbol = semanticModel.GetDeclaredSymbol(ns);
                if (symbol is not null)
                    AddDeclaration(workspaceId, projectId, filePath, symbol, symbols, relations);
                break;
            }
            case FileScopedNamespaceDeclarationSyntax ns:
            {
                var symbol = semanticModel.GetDeclaredSymbol(ns);
                if (symbol is not null)
                    AddDeclaration(workspaceId, projectId, filePath, symbol, symbols, relations);
                break;
            }
            case TypeDeclarationSyntax type:
            {
                var symbol = semanticModel.GetDeclaredSymbol(type);
                if (symbol is not null)
                    AddDeclaration(workspaceId, projectId, filePath, symbol, symbols, relations);
                break;
            }
            case EnumDeclarationSyntax _:
            case DelegateDeclarationSyntax _:
            {
                var symbol = semanticModel.GetDeclaredSymbol(node);
                if (symbol is not null)
                    AddDeclaration(workspaceId, projectId, filePath, symbol, symbols, relations);
                break;
            }
            case MethodDeclarationSyntax method:
            {
                var symbol = semanticModel.GetDeclaredSymbol(method);
                if (symbol is not null)
                    AddDeclaration(workspaceId, projectId, filePath, symbol, symbols, relations);
                break;
            }
            case ConstructorDeclarationSyntax ctor:
            {
                var symbol = semanticModel.GetDeclaredSymbol(ctor);
                if (symbol is not null)
                    AddDeclaration(workspaceId, projectId, filePath, symbol, symbols, relations);
                break;
            }
            case PropertyDeclarationSyntax prop:
            {
                var symbol = semanticModel.GetDeclaredSymbol(prop);
                if (symbol is not null)
                    AddDeclaration(workspaceId, projectId, filePath, symbol, symbols, relations);
                break;
            }
            case FieldDeclarationSyntax field:
            {
                foreach (var variable in field.Declaration.Variables)
                {
                    var symbol = semanticModel.GetDeclaredSymbol(variable);
                    if (symbol is not null)
                        AddDeclaration(workspaceId, projectId, filePath, symbol, symbols, relations);
                }
                break;
            }
            case EventDeclarationSyntax evt:
            case EventFieldDeclarationSyntax evtField:
            {
                var symbol = semanticModel.GetDeclaredSymbol(node);
                if (symbol is not null)
                    AddDeclaration(workspaceId, projectId, filePath, symbol, symbols, relations);
                break;
            }
            case ParameterSyntax param:
            {
                var symbol = semanticModel.GetDeclaredSymbol(param);
                if (symbol is not null)
                    AddDeclaration(workspaceId, projectId, filePath, symbol, symbols, relations);
                break;
            }
        }
    }

    private static void AddDeclaration(
        string workspaceId,
        string projectId,
        string filePath,
        ISymbol symbol,
        List<CodeSymbolRecord> symbols,
        List<CodeRelationRecord> relations)
    {
        var symbolId = RoslynSymbolId.GetId(symbol);
        var kind = ToSymbolKind(symbol);
        var lineSpan = symbol.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan()
            ?? default;

        var startLine = lineSpan.StartLinePosition.Line + 1;
        var endLine = lineSpan.EndLinePosition.Line + 1;
        var containerId = GetContainerSymbolId(symbol);
        var signature = GetSignature(symbol);

        symbols.Add(new CodeSymbolRecord(
            workspaceId,
            projectId,
            filePath,
            symbolId,
            symbol.Name,
            kind,
            startLine,
            startLine > 0 ? endLine : startLine,
            signature,
            containerId));

        if (containerId is not null)
        {
            relations.Add(new CodeRelationRecord(
                workspaceId,
                projectId,
                containerId,
                symbolId,
                CodeRelationKind.Contains,
                startLine,
                filePath));
        }
    }

    private static void ExtractReferences(
        SyntaxNode root,
        SemanticModel semanticModel,
        string workspaceId,
        string projectId,
        string filePath,
        List<CodeRelationRecord> relations,
        List<CodeReferenceRecord> references,
        CancellationToken cancellationToken)
    {
        foreach (var node in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (node)
            {
                case InvocationExpressionSyntax invocation:
                {
                    var targetSymbol = semanticModel.GetSymbolInfo(invocation).Symbol;
                    if (targetSymbol is not null)
                        AddCallOrReference(workspaceId, projectId, filePath, semanticModel, invocation,
                            targetSymbol, relations, references);
                    break;
                }
                case ObjectCreationExpressionSyntax creation:
                {
                    if (semanticModel.GetSymbolInfo(creation).Symbol is ISymbol target)
                        AddCallOrReference(workspaceId, projectId, filePath, semanticModel, creation,
                            target, relations, references);
                    break;
                }
                case MemberAccessExpressionSyntax member:
                {
                    var targetSymbol = semanticModel.GetSymbolInfo(member).Symbol;
                    if (targetSymbol is not null
                        && !IsNamespace(targetSymbol)
                        && !IsStaticTypeAccess(member, targetSymbol, semanticModel))
                    {
                        AddCallOrReference(workspaceId, projectId, filePath, semanticModel, member,
                            targetSymbol, relations, references);
                    }
                    break;
                }
                case IdentifierNameSyntax identifier:
                {
                    var parent = identifier.Parent;
                    if (parent is InvocationExpressionSyntax or MemberAccessExpressionSyntax
                        or ObjectCreationExpressionSyntax)
                        break;

                    var targetSymbol = semanticModel.GetSymbolInfo(identifier).Symbol;
                    if (targetSymbol is not null
                        && !IsNamespace(targetSymbol)
                        && targetSymbol is not ILocalSymbol
                        && targetSymbol is not IParameterSymbol)
                    {
                        AddCallOrReference(workspaceId, projectId, filePath, semanticModel, identifier,
                            targetSymbol, relations, references);
                    }
                    break;
                }
            }
        }
    }

    private static void AddCallOrReference(
        string workspaceId,
        string projectId,
        string filePath,
        SemanticModel semanticModel,
        SyntaxNode node,
        ISymbol targetSymbol,
        List<CodeRelationRecord> relations,
        List<CodeReferenceRecord> references)
    {
        var targetId = RoslynSymbolId.GetId(targetSymbol);
        var sourceLine = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var sourceText = node.ToString().Trim();
        if (sourceText.Length > 200)
            sourceText = sourceText[..200];

        var enclosingSymbol = FindEnclosingSymbol(semanticModel, node);
        if (enclosingSymbol is not null)
        {
            var sourceId = RoslynSymbolId.GetId(enclosingSymbol);

            references.Add(new CodeReferenceRecord(
                workspaceId,
                projectId,
                sourceId,
                targetId,
                filePath,
                sourceLine,
                sourceText,
                DateTimeOffset.UtcNow));

            if (targetSymbol is IMethodSymbol)
            {
                relations.Add(new CodeRelationRecord(
                    workspaceId,
                    projectId,
                    sourceId,
                    targetId,
                    CodeRelationKind.Calls,
                    sourceLine,
                    filePath));
            }
        }
    }

    private static ISymbol? FindEnclosingSymbol(SemanticModel semanticModel, SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            ISymbol? symbol = current switch
            {
                MethodDeclarationSyntax m => semanticModel.GetDeclaredSymbol(m),
                ConstructorDeclarationSyntax c => semanticModel.GetDeclaredSymbol(c),
                PropertyDeclarationSyntax p => semanticModel.GetDeclaredSymbol(p),
                _ => null,
            };

            if (symbol is not null)
                return symbol;
        }

        return null;
    }

    private static string? GetContainerSymbolId(ISymbol symbol)
    {
        var container = symbol.ContainingSymbol;
        if (container is null || container is INamespaceSymbol { IsGlobalNamespace: true })
            return null;

        return RoslynSymbolId.GetId(container);
    }

    private static string? GetSignature(ISymbol symbol) =>
        symbol switch
        {
            IMethodSymbol m => m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            IPropertySymbol p => p.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            IEventSymbol e => e.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            IFieldSymbol f => f.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            INamedTypeSymbol t => t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            _ => null,
        };

    private static CodeSymbolKind ToSymbolKind(ISymbol symbol) =>
        symbol switch
        {
            INamespaceSymbol => CodeSymbolKind.Namespace,
            INamedTypeSymbol t => t.TypeKind switch
            {
                TypeKind.Class => CodeSymbolKind.Class,
                TypeKind.Struct => CodeSymbolKind.Struct,
                TypeKind.Interface => CodeSymbolKind.Interface,
                TypeKind.Enum => CodeSymbolKind.Enum,
                TypeKind.Delegate => CodeSymbolKind.Delegate,
                _ => CodeSymbolKind.Type,
            },
            IMethodSymbol m => m.MethodKind switch
            {
                MethodKind.Constructor or MethodKind.StaticConstructor => CodeSymbolKind.Constructor,
                _ => CodeSymbolKind.Method,
            },
            IPropertySymbol => CodeSymbolKind.Property,
            IFieldSymbol => CodeSymbolKind.Field,
            IEventSymbol => CodeSymbolKind.Event,
            IParameterSymbol => CodeSymbolKind.Parameter,
            _ => CodeSymbolKind.Unknown,
        };

    // ADR-089 U4-4 D4: 原先是一份 5 项、且靠“含分隔符片段 Contains”判定的私有副本
    //（因此 path 首/尾段与大小写边界都与其它消费者不一致）；现统一走单一真源。
    /// <summary>
    /// 噪声判定必须喂「相对扫描根（项目根）」的路径。喂绝对路径会把宿主自身的目录名当噪声：
    /// 例如工作区在 <c>%TEMP%</c> 下时段名 <c>Temp</c> 命中 <c>temp</c>，整个项目会被静默排除。
    /// </summary>
    private static bool IsNoiseFile(string filePath, string? projectRoot)
        => string.IsNullOrWhiteSpace(projectRoot)
            // 拿不到项目根（例如 ad-hoc 编译没有 csproj）：只能按最后一段判定。
            // 绝不能用绝对路径做整串段匹配 —— 宿主自己的目录名（%TEMP% 里的 Temp）会命中。
            ? IndexExcludePatterns.IsNoiseDirectoryName(Path.GetFileName(filePath))
            : IndexExcludePatterns.IsNoisePathBelow(projectRoot, filePath);

    private static bool IsNamespace(ISymbol symbol) =>
        symbol is INamespaceSymbol;

    private static bool IsStaticTypeAccess(MemberAccessExpressionSyntax member, ISymbol targetSymbol, SemanticModel semanticModel)
    {
        if (targetSymbol is INamedTypeSymbol)
        {
            var exprSymbol = semanticModel.GetSymbolInfo(member.Expression).Symbol;
            return exprSymbol is INamedTypeSymbol;
        }

        return false;
    }
}
