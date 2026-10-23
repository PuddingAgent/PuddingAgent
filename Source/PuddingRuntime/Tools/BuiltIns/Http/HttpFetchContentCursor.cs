using System.Security.Cryptography;
using System.Text;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// Opaque continuation cursor for paged <c>http_fetch</c> content.
/// </summary>
/// <remarks>
/// The cursor binds three things: the representation that produced it, the character offset to
/// resume at, and a short fingerprint of the full content it belongs to. Reusing a cursor from a
/// different representation, or against content that has since changed, is rejected rather than
/// silently resuming mid-sentence at the wrong place.
/// </remarks>
/// <param name="Representation">Output representation that produced the cursor, e.g. <c>markdown</c>.</param>
/// <param name="Offset">Character offset into that representation to resume from.</param>
/// <param name="Fingerprint">Short fingerprint of the full content.</param>
public sealed record HttpFetchContentCursor(string Representation, int Offset, string Fingerprint)
{
    private const string Version = "v1";
    private const int FingerprintBytes = 6;

    /// <summary>Encodes the cursor into a URL-safe, padding-free opaque string.</summary>
    public string Encode()
    {
        var raw = $"{Version}|{Representation}|{Offset}|{Fingerprint}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>Decodes a cursor produced by <see cref="Encode"/>; malformed input yields <c>false</c>.</summary>
    public static bool TryDecode(string? wire, out HttpFetchContentCursor cursor)
    {
        cursor = null!;
        if (string.IsNullOrWhiteSpace(wire))
            return false;

        var normalized = wire.Trim().Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized,
        };

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = decoded.Split('|');
        if (parts.Length != 4 || parts[0] != Version)
            return false;

        if (!int.TryParse(parts[2], out var offset) || offset < 0)
            return false;

        cursor = new HttpFetchContentCursor(parts[1], offset, parts[3]);
        return true;
    }

    /// <summary>
    /// Short stable fingerprint of the whole representation, used to detect that the page changed
    /// between two reads.
    /// </summary>
    public static string FingerprintOf(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty));
        return Convert.ToHexString(hash, 0, FingerprintBytes).ToLowerInvariant();
    }
}
