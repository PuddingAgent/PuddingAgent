using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.Extensions.Logging;

using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.CSharp;

/// <summary>
/// C# code indexer that extracts declarations, Contains/Calls relations, and references
/// from Roslyn compilations and persists them through <see cref="ICodeIndexStore"/>.
/// </summary>
public sealed class RoslynCSharpIndexer : ICodeIndexer
{
    private static readonly HashSet<string> NoisePathSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.pudding-code{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
    };

    private readonly ICodeIndexStore _store;
    private readonly ILogger _logger;

    public RoslynCSharpIndexer(ICodeIndexStore store, ILogger<RoslynCSharpIndexer> logger)
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
                if (string.IsNullOrWhiteSpace(filePath) || IsNoiseFile(filePath))
                    continue;

                // Clear stale symbols for this file before re-indexing.
                // Ensures deleted symbols don't persist across re-index runs.
                await _store.ClearSymbolsForFileAsync(workspaceId, projectId, filePath, cancellationToken)
                    .ConfigureAwait(false);

                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);

                var symbols = new List<CodeSymbolRecord>();
                var relations = new List<CodeRelationRecord>();
                var references = new List<CodeReferenceRecord>();
                var containerStack = new Stack<string>();

                ExtractSymbols(root, semanticModel, workspaceId, projectId, filePath,
                    symbols, relations, cancellationToken);
                ExtractReferences(root, semanticModel, workspaceId, projectId, filePath,
                    relations, references, cancellationToken);

                if (symbols.Count > 0)
                {
                    allSymbols.AddRange(symbols);
                    allRelations.AddRange(relations);
                    allReferences.AddRange(references);
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

    private static bool IsNoiseFile(string filePath) =>
        NoisePathSegments.Any(seg =>
            filePath.Contains(seg, StringComparison.OrdinalIgnoreCase));

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
