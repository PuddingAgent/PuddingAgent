namespace PuddingIndexChunking;

/// <summary>
/// Built-in C2 defaults: the per-language minimum token length and the per-language keyword stop-word
/// lists (ADR-089 C2).
/// <para>
/// These are <b>data</b>, not behaviour. The filter evaluates them, and each rule can be switched off
/// through <see cref="ChunkFilterOptions"/>, which is what lets the retrieval experiment report the
/// contribution of "drop short tokens" and "drop keywords" separately instead of lumping them into one
/// smaller index.
/// </para>
/// <para>
/// The lists are stored <b>lower-case</b> with an ordinal comparer; case-insensitivity is a property
/// of the filter (<see cref="ChunkFilterOptions.IgnoreCase"/>), not of the data, so that flipping the
/// option is observable.
/// </para>
/// </summary>
public static class ChunkFilterRules
{
    /// <summary>Minimum token length used for languages without a specific entry.</summary>
    public const int DefaultMinimumTokenLength = 2;

    private static readonly IReadOnlySet<string> NoStopWords =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Minimum number of characters a token must have to be worth indexing, per language.
    /// <para>
    /// Short identifiers dominate code punctuation-bound noise (<c>i</c>, <c>j</c>, <c>ok</c>) without
    /// carrying meaning, while prose (markdown) legitimately contains two-letter words — hence the
    /// language-dependent value the user asked for rather than one global constant.
    /// </para>
    /// </summary>
    public static int DefaultMinimumTokenLengthFor(SourceLanguage language) => language switch
    {
        SourceLanguage.CSharp => 3,
        SourceLanguage.TypeScript => 3,
        SourceLanguage.Python => 3,
        SourceLanguage.Markdown => 2,
        SourceLanguage.PlainText => 2,
        _ => DefaultMinimumTokenLength,
    };

    /// <summary>The programming-language keywords to keep out of the index for a language.</summary>
    public static IReadOnlySet<string> DefaultStopWords(SourceLanguage language) => language switch
    {
        SourceLanguage.CSharp => CSharpKeywords,
        SourceLanguage.TypeScript => TypeScriptKeywords,
        SourceLanguage.Python => PythonKeywords,
        _ => NoStopWords,
    };

    /// <summary>C# keywords: all reserved words plus the common contextual ones (lower-cased).</summary>
    public static IReadOnlySet<string> CSharpKeywords { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
        // contextual keywords that are ubiquitous in declarations
        "var", "async", "await", "yield", "record", "init", "get", "set", "when", "where",
        "partial", "nameof", "dynamic", "global",
    };

    /// <summary>TypeScript / JavaScript keywords.</summary>
    public static IReadOnlySet<string> TypeScriptKeywords { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "any", "as", "async", "await", "boolean", "break", "case", "catch", "class", "const",
        "constructor", "continue", "debugger", "declare", "default", "delete", "do", "else", "enum",
        "export", "extends", "false", "finally", "for", "from", "function", "get", "if",
        "implements", "import", "in", "instanceof", "interface", "is", "keyof", "let", "module",
        "namespace", "new", "null", "number", "object", "of", "private", "protected", "public",
        "readonly", "require", "return", "set", "static", "string", "super", "switch", "this",
        "throw", "true", "try", "type", "typeof", "undefined", "var", "void", "while", "with",
        "yield",
    };

    /// <summary>Python keywords.</summary>
    public static IReadOnlySet<string> PythonKeywords { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del",
        "elif", "else", "except", "false", "finally", "for", "from", "global", "if", "import",
        "in", "is", "lambda", "none", "nonlocal", "not", "or", "pass", "raise", "return", "self",
        "true", "try", "while", "with", "yield",
    };
}
