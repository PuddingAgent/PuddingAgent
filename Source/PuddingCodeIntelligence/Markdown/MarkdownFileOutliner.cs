using System.Text.RegularExpressions;

using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.Markdown;

/// <summary>
/// Markdown file outliner.
/// Extracts headings (H1-H6), code blocks, and front-matter as outline nodes.
/// Returns a flat list sorted by line number; clients can reconstruct hierarchy
/// from the Modifiers field ("h1", "h2", etc.) on demand.
/// </summary>
public sealed partial class MarkdownFileOutliner : IFileOutliner
{
    private static readonly string[] Extensions = [".md", ".mdx", ".markdown"];

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    // ATX headings: # H1 ... ###### H6
    [GeneratedRegex(@"^(#{1,6})\s+(.+?)(?:\s+#+\s*)?$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    // Fenced code blocks: ``` or ~~~
    [GeneratedRegex(@"^(`{3,}|~{3,})(\w*)", RegexOptions.Multiline)]
    private static partial Regex CodeFenceRegex();

    // YAML front-matter: --- ... ---
    [GeneratedRegex(@"^---\s*\n(.*?)\n---\s*\n", RegexOptions.Singleline)]
    private static partial Regex FrontMatterRegex();

    public Task<OutlineResult> OutlineAsync(
        string filePath,
        string sourceCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var nodes = ParseSource(sourceCode);
            return Task.FromResult(new OutlineResult(true, filePath, nodes));
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                new OutlineResult(false, filePath, [], ex.Message));
        }
    }

    internal static IReadOnlyList<OutlineNode> ParseSource(string source)
    {
        var nodes = new List<OutlineNode>();
        var lines = source.Split('\n');

        // Track code block state to avoid parsing headings inside code
        var inCodeBlock = false;

        // 1. Parse front-matter
        var fmMatch = FrontMatterRegex().Match(source);
        if (fmMatch.Success)
        {
            var fmLines = fmMatch.Value.Split('\n').Length;
            nodes.Add(new OutlineNode(
                "front-matter", CodeSymbolKind.Constant, 1, fmLines,
                Signature: "--- ... ---"));
        }

        // 2. Parse headings
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;

            // Track code blocks
            if (CodeFenceRegex().IsMatch(line))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock) continue;

            // ATX heading
            var headingMatch = HeadingRegex().Match(line);
            if (headingMatch.Success)
            {
                var level = headingMatch.Groups[1].Value.Length;
                var title = headingMatch.Groups[2].Value.TrimEnd();
                var kind = level switch
                {
                    1 => CodeSymbolKind.Namespace,   // H1 → document root
                    2 => CodeSymbolKind.Class,        // H2 → section
                    3 => CodeSymbolKind.Method,       // H3 → subsection
                    _ => CodeSymbolKind.Property      // H4-H6 → detail
                };

                nodes.Add(new OutlineNode(
                    title, kind, lineNum, lineNum,
                    Signature: new string('#', level) + " " + title,
                    Modifiers: $"h{level}"));
                continue;
            }

            // Setext heading (line followed by === or ---)
            if (i + 1 < lines.Length)
            {
                var nextLine = lines[i + 1].TrimEnd();
                if (nextLine.Length >= 3)
                {
                    if (nextLine.All(c => c == '='))
                    {
                        nodes.Add(new OutlineNode(
                            line.TrimEnd(), CodeSymbolKind.Namespace, lineNum, lineNum + 1,
                            Signature: line.TrimEnd(),
                            Modifiers: "h1-setext"));
                    }
                    else if (nextLine.All(c => c == '-'))
                    {
                        nodes.Add(new OutlineNode(
                            line.TrimEnd(), CodeSymbolKind.Class, lineNum, lineNum + 1,
                            Signature: line.TrimEnd(),
                            Modifiers: "h2-setext"));
                    }
                }
            }
        }

        // Sort by line number
        nodes.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));
        return nodes;
    }
}
