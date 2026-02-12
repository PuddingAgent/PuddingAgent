using Microsoft.CodeAnalysis;

namespace PuddingCodeIntelligence.CSharp;

/// <summary>
/// Generates a stable symbol id from a Roslyn <see cref="ISymbol"/>.
/// Uses Roslyn's built-in documentation comment id when available,
/// falling back to a composite of kind, containing symbol, name, and span.
/// </summary>
internal static class RoslynSymbolId
{
    /// <summary>
    /// Returns a stable identifier for <paramref name="symbol"/>.
    /// The returned id is reproducible across indexing runs as long as
    /// the symbol name, containing hierarchy, and position don't change.
    /// </summary>
    public static string GetId(ISymbol symbol)
    {
        var docId = symbol.GetDocumentationCommentId();
        if (!string.IsNullOrEmpty(docId))
            return docId;

        return BuildFallbackId(symbol);
    }

    private static string BuildFallbackId(ISymbol symbol)
    {
        var kind = symbol.Kind.ToString();
        var containing = symbol.ContainingSymbol is null or INamespaceSymbol { IsGlobalNamespace: true }
            ? string.Empty
            : $"({GetId(symbol.ContainingSymbol)})";

        var name = symbol switch
        {
            IMethodSymbol m => $"{m.Name}[{m.Arity}]",
            IPropertySymbol p => $"{p.Name}",
            _ => symbol.Name,
        };

        var locations = symbol.Locations;
        var span = locations.Length > 0 && locations[0].IsInSource
            ? $"{locations[0].SourceSpan.Start}"
            : string.Empty;

        return $"{kind}:{containing}{name}@{span}";
    }
}
