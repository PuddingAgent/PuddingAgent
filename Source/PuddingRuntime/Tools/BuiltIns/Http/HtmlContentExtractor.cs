using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace PuddingRuntime.Services.Skills;

/// <summary>Extracts the high-value readable portion of an HTML document.</summary>
public static partial class HtmlContentExtractor
{
    private static readonly string[] s_noiseTags =
    [
        "script",
        "style",
        "nav",
        "footer",
        "header",
        "noscript",
        "svg",
        "canvas",
        "iframe",
        "form",
    ];

    public static string ExtractReadableHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        foreach (var tag in s_noiseTags)
        {
            var nodes = doc.DocumentNode.SelectNodes($"//{tag}");
            if (nodes is null)
                continue;

            foreach (var node in nodes.ToArray())
            {
                node.Remove();
            }
        }

        var content = doc.DocumentNode.SelectSingleNode("//main")
                      ?? doc.DocumentNode.SelectSingleNode("//article")
                      ?? doc.DocumentNode.SelectSingleNode("//body")
                      ?? doc.DocumentNode;

        return content.InnerHtml.Trim();
    }

    public static string ToPlainText(string html)
    {
        var readable = ExtractReadableHtml(html);
        if (string.IsNullOrWhiteSpace(readable))
            return string.Empty;

        var doc = new HtmlDocument();
        doc.LoadHtml(readable);
        var sb = new StringBuilder();
        AppendText(doc.DocumentNode, sb);
        return CollapseWhitespace(WebUtility.HtmlDecode(sb.ToString())).Trim();
    }

    public static bool IsHtml(string? contentType, string body)
    {
        if (!string.IsNullOrWhiteSpace(contentType)
            && contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            return true;

        var trimmed = body.TrimStart();
        return trimmed.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendText(HtmlNode node, StringBuilder sb)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            var text = ((HtmlTextNode)node).Text;
            if (!string.IsNullOrWhiteSpace(text))
                sb.Append(text).Append(' ');
            return;
        }

        if (node.Name is "br" or "p" or "div" or "section" or "article" or "li" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            sb.AppendLine();

        foreach (var child in node.ChildNodes)
        {
            AppendText(child, sb);
        }

        if (node.Name is "p" or "div" or "section" or "article" or "li" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            sb.AppendLine();
    }

    private static string CollapseWhitespace(string text) =>
        WhitespaceRegex().Replace(text, " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
