using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.Lsp;

/// <summary>
/// Index-based language server service that provides LSP-like functionality
/// (Hover, Definition, References) using the code index and file outliners
/// instead of an external LSP server process.
/// </summary>
public sealed class IndexBasedLanguageServerService : ILanguageServerService
{
    private readonly ICodeQueryService _queryService;
    private readonly IFileOutlinerRegistry _outlinerRegistry;
    private readonly ILogger<IndexBasedLanguageServerService> _logger;

    public IndexBasedLanguageServerService(
        ICodeQueryService queryService,
        IFileOutlinerRegistry outlinerRegistry,
        ILogger<IndexBasedLanguageServerService> logger)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _outlinerRegistry = outlinerRegistry ?? throw new ArgumentNullException(nameof(outlinerRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<LanguageServerResponse> ExecuteAsync(
        LanguageServerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Method switch
        {
            LanguageServerMethod.Hover => await HandleHoverAsync(request, cancellationToken),
            LanguageServerMethod.Definition => await HandleDefinitionAsync(request, cancellationToken),
            LanguageServerMethod.References => await HandleReferencesAsync(request, cancellationToken),
            _ => LanguageServerResponse.Unsupported(request.Method, request.CorrelationId),
        };
    }

    // ── Hover ────────────────────────────────────────────────────────────────

    private async Task<LanguageServerResponse> HandleHoverAsync(
        LanguageServerRequest request, CancellationToken ct)
    {
        var symbolName = await ResolveSymbolAtLineAsync(request, ct);
        if (symbolName is null)
            return LanguageServerResponse.Unsupported(request.Method, request.CorrelationId);

        var searchRequest = new CodeSymbolSearchRequest(
            WorkspaceId: request.WorkspaceId,
            Query: symbolName,
            ProjectId: request.ProjectId,
            Limit: 5);

        var results = await _queryService.SearchSymbolsAsync(searchRequest, ct);
        if (results.Count == 0)
            return LanguageServerResponse.Unsupported(request.Method, request.CorrelationId);

        // Pick the best match (exact name match preferred)
        var best = results.FirstOrDefault(r =>
            string.Equals(r.Symbol.Name, symbolName, StringComparison.Ordinal)) ?? results[0];

        var hoverResult = new
        {
            symbol = best.Symbol.Name,
            kind = best.Symbol.Kind.ToString(),
            signature = best.Symbol.Signature ?? best.Symbol.Name,
            container = best.Symbol.Container,
            file = best.Symbol.FilePath,
            line = best.Symbol.StartLine,
            displayName = best.DisplayName,
        };

        var json = JsonSerializer.Serialize(hoverResult, JsonOptions);
        return LanguageServerResponse.Success(request.Method, json, request.CorrelationId);
    }

    // ── Definition ───────────────────────────────────────────────────────────

    private async Task<LanguageServerResponse> HandleDefinitionAsync(
        LanguageServerRequest request, CancellationToken ct)
    {
        var symbolName = await ResolveSymbolAtLineAsync(request, ct);
        if (symbolName is null)
            return LanguageServerResponse.Unsupported(request.Method, request.CorrelationId);

        var searchRequest = new CodeSymbolSearchRequest(
            WorkspaceId: request.WorkspaceId,
            Query: symbolName,
            ProjectId: request.ProjectId,
            Limit: 5);

        var results = await _queryService.SearchSymbolsAsync(searchRequest, ct);
        if (results.Count == 0)
            return LanguageServerResponse.Unsupported(request.Method, request.CorrelationId);

        var best = results.FirstOrDefault(r =>
            string.Equals(r.Symbol.Name, symbolName, StringComparison.Ordinal)) ?? results[0];

        var definitionResult = new
        {
            file = best.Symbol.FilePath,
            line = best.Symbol.StartLine,
            endLine = best.Symbol.EndLine,
            symbol = best.Symbol.Name,
            kind = best.Symbol.Kind.ToString(),
        };

        var json = JsonSerializer.Serialize(definitionResult, JsonOptions);
        return LanguageServerResponse.Success(request.Method, json, request.CorrelationId);
    }

    // ── References ───────────────────────────────────────────────────────────

    private async Task<LanguageServerResponse> HandleReferencesAsync(
        LanguageServerRequest request, CancellationToken ct)
    {
        var symbolName = await ResolveSymbolAtLineAsync(request, ct);
        if (symbolName is null)
            return LanguageServerResponse.Unsupported(request.Method, request.CorrelationId);

        var searchRequest = new CodeSymbolSearchRequest(
            WorkspaceId: request.WorkspaceId,
            Query: symbolName,
            ProjectId: request.ProjectId,
            Limit: 5);

        var results = await _queryService.SearchSymbolsAsync(searchRequest, ct);
        if (results.Count == 0)
            return LanguageServerResponse.Unsupported(request.Method, request.CorrelationId);

        var best = results.FirstOrDefault(r =>
            string.Equals(r.Symbol.Name, symbolName, StringComparison.Ordinal)) ?? results[0];

        // Get callers (references) for the matched symbol
        var callers = await _queryService.GetCallersAsync(
            request.WorkspaceId,
            best.Symbol.ProjectId,
            best.Symbol.SymbolId,
            ct);

        var references = callers.Select(c => new
        {
            file = c.SourceFilePath ?? "unknown",
            line = c.SourceLine,
            callerSymbolId = c.SourceSymbolId,
            relationKind = c.Kind.ToString(),
        }).ToList();

        var referencesResult = new
        {
            symbol = best.Symbol.Name,
            definitionFile = best.Symbol.FilePath,
            definitionLine = best.Symbol.StartLine,
            referenceCount = references.Count,
            references,
        };

        var json = JsonSerializer.Serialize(referencesResult, JsonOptions);
        return LanguageServerResponse.Success(request.Method, json, request.CorrelationId);
    }

    // ── Symbol resolution via file outline ───────────────────────────────────

    /// <summary>
    /// Uses the file outliner to parse the document and find the symbol node
    /// closest to the requested line.
    /// </summary>
    private async Task<string?> ResolveSymbolAtLineAsync(
        LanguageServerRequest request, CancellationToken ct)
    {
        var outliner = _outlinerRegistry.GetOutliner(request.DocumentPath);
        if (outliner is null)
        {
            _logger.LogDebug(
                "No outliner registered for file {FilePath}; cannot resolve symbol at line {Line}",
                request.DocumentPath, request.Line);
            return null;
        }

        string sourceCode;
        try
        {
            sourceCode = await File.ReadAllTextAsync(request.DocumentPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to read file {FilePath}", request.DocumentPath);
            return null;
        }

        var outlineResult = await outliner.OutlineAsync(request.DocumentPath, sourceCode, ct);
        if (!outlineResult.Success || outlineResult.Nodes.Count == 0)
        {
            _logger.LogDebug(
                "Outline failed or empty for {FilePath}: {Error}",
                request.DocumentPath, outlineResult.Error);
            return null;
        }

        // Flatten the tree and find the node whose range contains or is closest to the target line
        var targetLine = request.Line <= 0 ? 1 : request.Line;
        var bestNode = FindClosestNode(outlineResult.Nodes, targetLine);
        return bestNode?.Name;
    }

    /// <summary>
    /// Recursively searches the outline tree for the innermost node containing the target line,
    /// or the closest node if no exact containment is found.
    /// </summary>
    private static OutlineNode? FindClosestNode(IReadOnlyList<OutlineNode> nodes, int targetLine)
    {
        OutlineNode? best = null;
        var bestDistance = int.MaxValue;

        foreach (var node in nodes)
        {
            // Check if the target line is within this node's range
            if (targetLine >= node.StartLine && targetLine <= node.EndLine)
            {
                // This node contains the line; prefer deeper (child) nodes
                if (node.Children is { Count: > 0 })
                {
                    var childResult = FindClosestNode(node.Children, targetLine);
                    if (childResult is not null)
                        return childResult;
                }

                return node;
            }

            // Track closest node by distance
            var distance = targetLine < node.StartLine
                ? node.StartLine - targetLine
                : targetLine - node.EndLine;

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = node;
            }

            // Also search children for a closer match
            if (node.Children is { Count: > 0 })
            {
                var childResult = FindClosestNode(node.Children, targetLine);
                if (childResult is not null)
                {
                    var childDistance = targetLine < childResult.StartLine
                        ? childResult.StartLine - targetLine
                        : targetLine - childResult.EndLine;

                    if (childDistance < bestDistance)
                    {
                        bestDistance = childDistance;
                        best = childResult;
                    }
                }
            }
        }

        return best;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
