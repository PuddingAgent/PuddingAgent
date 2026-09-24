using System.Globalization;

namespace PuddingCodeIndex.Contracts;

/// <summary>Result of parsing a human-written size literal such as <c>"1GiB"</c>, <c>"4GB"</c> or <c>"1073741824"</c>.</summary>
/// <param name="Success">Whether a positive byte count was produced.</param>
/// <param name="Bytes">Parsed byte count (0 when <paramref name="Success"/> is false).</param>
/// <param name="Unit">Normalised (upper-case) unit suffix; empty for a bare byte count.</param>
/// <param name="UsedDecimalUnit">True when a decimal (10³) suffix was used — see <see cref="CodeIndexSizes"/>.</param>
/// <param name="Error">Human-readable reason when parsing failed; otherwise null.</param>
public readonly record struct CodeIndexSizeParseResult(
    bool Success,
    long Bytes,
    string Unit,
    bool UsedDecimalUnit,
    string? Error);

/// <summary>
/// Parses the size literals accepted by the index configuration files.
/// </summary>
/// <remarks>
/// Unit semantics are deliberately explicit, because "1GB" is ambiguous by nature and a silent
/// 7.4% deviation on a capacity guardrail is exactly the kind of error that must not be assumed away:
/// <list type="bullet">
/// <item><c>B</c> / no suffix — bytes.</item>
/// <item><c>KiB</c> / <c>MiB</c> / <c>GiB</c> / <c>TiB</c> — binary (2¹⁰, 2²⁰, 2³⁰, 2⁴⁰), IEC 80000-13.</item>
/// <item><c>KB</c> / <c>MB</c> / <c>GB</c> / <c>TB</c> — decimal (10³, 10⁶, 10⁹, 10¹²), SI.</item>
/// </list>
/// A decimal unit still parses, but is flagged through <see cref="CodeIndexSizeParseResult.UsedDecimalUnit"/>
/// so callers can surface it instead of silently reinterpreting the author's intent.
/// </remarks>
public static class CodeIndexSizes
{
    private static readonly string[] DecimalUnits = ["KB", "MB", "GB", "TB"];

    /// <summary>Parses a size literal; never throws.</summary>
    public static CodeIndexSizeParseResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new CodeIndexSizeParseResult(false, 0, string.Empty, false, "size text is empty");

        var trimmed = text.Trim();

        var digitEnd = trimmed.Length;
        while (digitEnd > 0 && char.IsLetter(trimmed[digitEnd - 1]))
            digitEnd--;

        var numberPart = trimmed[..digitEnd].Trim();
        var unit = trimmed[digitEnd..].Trim().ToUpperInvariant();

        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value)
            || double.IsInfinity(value)
            || value <= 0d)
        {
            return new CodeIndexSizeParseResult(
                false, 0, unit, false, $"'{text}' does not start with a positive number");
        }

        var factor = unit switch
        {
            "" or "B" => 1d,
            "KIB" => 1024d,
            "MIB" => 1024d * 1024d,
            "GIB" => 1024d * 1024d * 1024d,
            "TIB" => 1024d * 1024d * 1024d * 1024d,
            "KB" => 1e3,
            "MB" => 1e6,
            "GB" => 1e9,
            "TB" => 1e12,
            _ => 0d,
        };

        if (factor == 0d)
        {
            return new CodeIndexSizeParseResult(
                false, 0, unit, false,
                $"unknown size unit '{unit}' (use B, KiB, MiB, GiB, TiB; or KB, MB, GB, TB for decimal)");
        }

        var total = value * factor;
        if (total < 1d || total > long.MaxValue)
            return new CodeIndexSizeParseResult(false, 0, unit, false, $"'{text}' is out of range for a byte count");

        return new CodeIndexSizeParseResult(true, (long)total, unit, DecimalUnits.Contains(unit), null);
    }
}
