using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Services;

/// <summary>
/// Resolves the effective per-library capacity budget from configuration files (ADR-089 §M2; user ruling
/// 2026-09-24: the budget is a configuration parameter, not a compiled-in value).
/// </summary>
/// <remarks>
/// <para>
/// Precedence, applied <b>field by field</b> so a project may override only the ceiling while the host-wide
/// file still supplies the soft threshold:
/// project file &gt; global file &gt; <see cref="CodeIndexLibraryBudgetDefaults"/>.
/// </para>
/// <para>
/// Both files use the same schema, and only the <c>library</c> section is read:
/// <code>
/// {
///   "library": {
///     "maxLibrarySize": "1GiB",   // human-readable; Si/IEC suffixes, see CodeIndexSizes
///     "maxLibraryBytes": 1073741824, // exact byte count; wins when both are present
///     "softRatio": 0.8
///   }
/// }
/// </code>
/// </para>
/// <para>
/// The resolver never throws and never fails open: a missing file simply means "this source declared nothing",
/// while a corrupt or invalid file falls back to the conservative default and reports a warning. Returning an
/// unlimited budget on a parse error would turn a typo into an unbounded index.
/// </para>
/// </remarks>
public sealed class CodeIndexLibraryBudgetResolver
{
    private readonly string? _projectConfigPath;
    private readonly string? _globalConfigPath;
    private readonly ILogger? _logger;

    /// <param name="projectConfigPath">
    /// Project-local configuration file (e.g. <c>&lt;projectRoot&gt;/.pudding/code-index.json</c>). Highest precedence.
    /// </param>
    /// <param name="globalConfigPath">
    /// Host-wide configuration file (e.g. <c>&lt;DataRoot&gt;/config/code-index.json</c>). Lower precedence.
    /// </param>
    /// <param name="logger">Optional logger; every fallback and every warning is reported.</param>
    public CodeIndexLibraryBudgetResolver(
        string? projectConfigPath,
        string? globalConfigPath,
        ILogger? logger = null)
    {
        _projectConfigPath = string.IsNullOrWhiteSpace(projectConfigPath) ? null : projectConfigPath;
        _globalConfigPath = string.IsNullOrWhiteSpace(globalConfigPath) ? null : globalConfigPath;
        _logger = logger;
    }

    /// <summary>Resolves the effective budget. Never throws.</summary>
    public CodeIndexLibraryBudget Resolve()
    {
        var warnings = new List<string>();

        var global = ReadSection(_globalConfigPath, CodeIndexLibraryBudgetSource.GlobalConfig, warnings);
        var project = ReadSection(_projectConfigPath, CodeIndexLibraryBudgetSource.ProjectConfig, warnings);

        var maxBytes = CodeIndexLibraryBudgetDefaults.MaxLibraryBytes;
        var softRatio = CodeIndexLibraryBudgetDefaults.SoftRatio;
        var source = CodeIndexLibraryBudgetSource.Default;
        string? configPath = null;

        // Lowest precedence first: each contributor overwrites only the fields it actually declared.
        foreach (var (contributorSource, path, section) in new[]
                 {
                     (CodeIndexLibraryBudgetSource.GlobalConfig, _globalConfigPath, global),
                     (CodeIndexLibraryBudgetSource.ProjectConfig, _projectConfigPath, project),
                 })
        {
            if (section is null)
                continue;

            var applied = false;

            if (section.MaxBytes is > 0)
            {
                maxBytes = section.MaxBytes.Value;
                applied = true;
                if (section.SizeUsedDecimalUnit)
                {
                    warnings.Add(
                        $"{path}: 'maxLibrarySize' = '{section.SizeText}' uses a decimal (10³) unit and resolved to " +
                        $"{maxBytes} bytes; 'GiB' would be binary (2³⁰). Make the intent explicit.");
                }
            }
            else if (section.MaxBytes is not null)
            {
                warnings.Add($"{path}: the declared budget must be positive ({section.MaxBytes} was ignored).");
            }

            if (section.SoftRatio is > 0d and <= 1d)
            {
                softRatio = section.SoftRatio.Value;
                applied = true;
            }
            else if (section.SoftRatio is not null)
            {
                warnings.Add(
                    $"{path}: 'softRatio' must be in (0, 1] ({section.SoftRatio} was ignored).");
            }

            if (applied)
            {
                source = contributorSource;
                configPath = path;
            }
        }

        foreach (var warning in warnings)
            _logger?.LogWarning("[CodeIndexLibraryBudget] {Warning}", warning);

        return new CodeIndexLibraryBudget(
            maxBytes,
            softRatio,
            source,
            configPath,
            warnings.Count == 0 ? null : warnings);
    }

    /// <summary>The <c>library</c> section of one configuration file, with <c>null</c> meaning "not declared".</summary>
    private sealed record LibrarySection(
        long? MaxBytes,
        double? SoftRatio,
        string? SizeText,
        bool SizeUsedDecimalUnit);

    private static LibrarySection? ReadSection(
        string? path,
        CodeIndexLibraryBudgetSource source,
        List<string> warnings)
    {
        if (path is null)
            return null;

        string text;
        try
        {
            // A missing file means "this source declared nothing" — it is not an error.
            if (!File.Exists(path))
                return null;

            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            warnings.Add($"{Describe(source)} '{path}' could not be read ({ex.Message}); ignoring this source.");
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(
                text,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("library", out var library)
                || library.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            long? maxBytes = null;
            if (library.TryGetProperty("maxLibraryBytes", out var bytesElement)
                && bytesElement.ValueKind == JsonValueKind.Number
                && bytesElement.TryGetInt64(out var declaredBytes))
            {
                maxBytes = declaredBytes;
            }

            string? sizeText = null;
            var sizeUsedDecimalUnit = false;
            if (library.TryGetProperty("maxLibrarySize", out var sizeElement)
                && sizeElement.ValueKind == JsonValueKind.String)
            {
                sizeText = sizeElement.GetString();
                var parsed = CodeIndexSizes.Parse(sizeText);
                if (parsed.Success)
                {
                    if (maxBytes is not null)
                    {
                        warnings.Add(
                            $"{path}: both 'maxLibraryBytes' and 'maxLibrarySize' are present; " +
                            $"'maxLibraryBytes' ({maxBytes} bytes) wins over '{sizeText}'.");
                    }
                    else
                    {
                        maxBytes = parsed.Bytes;
                        sizeUsedDecimalUnit = parsed.UsedDecimalUnit;
                    }
                }
                else
                {
                    warnings.Add(
                        $"{path}: 'maxLibrarySize' = '{sizeText}' is not a valid size ({parsed.Error}); " +
                        "falling back to the default budget.");
                }
            }

            double? softRatio = null;
            if (library.TryGetProperty("softRatio", out var ratioElement)
                && ratioElement.ValueKind == JsonValueKind.Number
                && ratioElement.TryGetDouble(out var declaredRatio))
            {
                softRatio = declaredRatio;
            }

            return new LibrarySection(maxBytes, softRatio, sizeText, sizeUsedDecimalUnit);
        }
        catch (JsonException ex)
        {
            warnings.Add(
                $"{Describe(source)} '{path}' is not valid JSON ({ex.Message}); " +
                "falling back to the default budget instead of treating it as unlimited.");
            return null;
        }
    }

    private static string Describe(CodeIndexLibraryBudgetSource source) => source switch
    {
        CodeIndexLibraryBudgetSource.ProjectConfig => "project configuration",
        CodeIndexLibraryBudgetSource.GlobalConfig => "global configuration",
        _ => "configuration",
    };
}
