using System.Text;
using System.Text.Json;
using PuddingRetrievalEval.Contracts;

namespace PuddingRetrievalEval.Services;

/// <summary>Raised when an annotated query set is malformed. Loading is fail-closed: no silent defaults.</summary>
public sealed class EvalSetFormatException : Exception
{
    public EvalSetFormatException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Loads annotated query sets from version-controlled JSON data files
/// (<c>Source/PuddingRetrievalEval/eval/sets/*.json</c>). The annotation set is data, never a test
/// constant: a reviewer can diff it, and a regression run can compare two reports produced from it.
/// <para>
/// Fail-closed: unknown <c>kind</c>/<c>language</c> values, blank queries, empty or duplicated
/// <c>expectedHits</c> all throw <see cref="EvalSetFormatException"/> instead of degrading into a
/// silently weaker measurement.
/// </para>
/// </summary>
public static class EvalSetLoader
{
    /// <summary>Reads and validates a set file. <paramref name="path"/> is echoed into every error message.</summary>
    public static EvalSet LoadFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("set path is required", nameof(path));

        if (!File.Exists(path))
            throw new FileNotFoundException($"annotated query set not found: {path}", path);

        return Parse(File.ReadAllText(path, Encoding.UTF8), path);
    }

    /// <summary>Parses and validates a set document.</summary>
    public static EvalSet Parse(string json, string sourceName)
    {
        if (json is null)
            throw new ArgumentNullException(nameof(json));

        var origin = string.IsNullOrWhiteSpace(sourceName) ? "<memory>" : sourceName;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            throw new EvalSetFormatException($"{origin}: not valid JSON — {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new EvalSetFormatException($"{origin}: root must be a JSON object, found {root.ValueKind}");

            var name = RequireString(root, "name", origin);
            var version = RequireInt(root, "version", origin);
            if (version < 1)
                throw new EvalSetFormatException($"{origin}: version must be >= 1, found {version}");

            if (!TryGetProperty(root, "cases", out var casesElement) || casesElement.ValueKind != JsonValueKind.Array)
                throw new EvalSetFormatException($"{origin}: 'cases' must be a JSON array");

            var cases = new List<EvalCase>();
            var index = 0;
            foreach (var element in casesElement.EnumerateArray())
            {
                cases.Add(ParseCase(element, origin, index));
                index++;
            }

            if (cases.Count == 0)
                throw new EvalSetFormatException($"{origin}: 'cases' must not be empty");

            return new EvalSet(name, version, cases);
        }
    }

    /// <summary>Maps a <c>kind</c> literal to <see cref="EvalKind"/>; unknown literals are a format error.</summary>
    public static EvalKind ParseKind(string? raw, string origin)
        => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "symbol" => EvalKind.Symbol,
            "intent" => EvalKind.Intent,
            "crossref" => EvalKind.Crossref,
            _ => throw new EvalSetFormatException(
                $"{origin}: unknown kind '{raw}' — expected one of symbol|intent|crossref"),
        };

    /// <summary>Maps a <c>language</c> literal to <see cref="EvalLanguage"/>; unknown literals are a format error.</summary>
    public static EvalLanguage ParseLanguage(string? raw, string origin)
        => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "csharp" or "cs" => EvalLanguage.CSharp,
            "typescript" or "ts" or "tsx" => EvalLanguage.TypeScript,
            "markdown" or "md" => EvalLanguage.Markdown,
            _ => throw new EvalSetFormatException(
                $"{origin}: unknown language '{raw}' — expected one of csharp|typescript|markdown"),
        };

    private static EvalCase ParseCase(JsonElement element, string origin, int index)
    {
        var where = $"{origin}: cases[{index}]";

        if (element.ValueKind != JsonValueKind.Object)
            throw new EvalSetFormatException($"{where}: case must be a JSON object, found {element.ValueKind}");

        var query = RequireString(element, "query", where);
        var kind = ParseKind(RequireString(element, "kind", where), where);
        var language = ParseLanguage(RequireString(element, "language", where), where);

        if (!TryGetProperty(element, "expectedHits", out var expectedElement)
            || expectedElement.ValueKind != JsonValueKind.Array)
            throw new EvalSetFormatException($"{where}: 'expectedHits' must be a JSON array");

        var expected = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var hit in expectedElement.EnumerateArray())
        {
            if (hit.ValueKind != JsonValueKind.String)
                throw new EvalSetFormatException($"{where}: every expectedHits entry must be a string");

            var value = (hit.GetString() ?? string.Empty).Trim();
            if (value.Length == 0)
                throw new EvalSetFormatException($"{where}: expectedHits entries must not be blank");

            var (path, symbol) = PathIdentity.SplitExpected(value);
            var key = PathIdentity.Normalize(path) + "#" + (symbol ?? string.Empty).ToLowerInvariant();
            if (!seen.Add(key))
                throw new EvalSetFormatException($"{where}: duplicate expected hit '{value}'");

            expected.Add(value);
        }

        if (expected.Count == 0)
            throw new EvalSetFormatException($"{where}: 'expectedHits' must contain at least one entry");

        return new EvalCase(query, expected, kind, language);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string RequireString(JsonElement element, string name, string origin)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new EvalSetFormatException($"{origin}: '{name}' must be a JSON string");

        var text = (value.GetString() ?? string.Empty).Trim();
        if (text.Length == 0)
            throw new EvalSetFormatException($"{origin}: '{name}' must not be blank");

        return text;
    }

    private static int RequireInt(JsonElement element, string name, string origin)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number))
            throw new EvalSetFormatException($"{origin}: '{name}' must be a JSON integer");

        return number;
    }
}
