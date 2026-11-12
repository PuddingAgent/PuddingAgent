using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PuddingDingTalk;

namespace PuddingAgent.Services;

internal static partial class DingTalkPublicMedia
{
    // The published MPCAL README displays this image. Bind the exact approved bytes.
    private const string LogoSha = "6c4bb5f647d04dad9701767706414b2128fb072f6bb7909c345f5fbd14cd5897";
    internal static string LogoId => "vision-" + LogoSha[..32];

    // Models also emit ordinary inline Markdown for attachments. Produce the same native image
    // directive used by Feishu. ProjectReply still authorizes every resulting artifact ID.
    internal static string NormalizeReply(string text) => InlineImage().Replace(text,
        match => "\n```image\n" + match.Groups[1].Value + "\n```\n").Trim();

    [GeneratedRegex(@"!\[[^\]\r\n]{0,120}\]\((?:artifact://)?(vision-[0-9a-f]{32})\)", RegexOptions.CultureInvariant)]
    private static partial Regex InlineImage();

    internal static async Task<GroupImage?> ReadLogoAsync(string? root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var path = Path.Combine(root, "images", "logo.png");
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        if (stream.Length != 2501) return null;
        var bytes = new byte[2501];
        await stream.ReadExactlyAsync(bytes, ct);
        return Convert.ToHexStringLower(SHA256.HashData(bytes)) == LogoSha ? new(bytes, "image/png") : null;
    }
}
