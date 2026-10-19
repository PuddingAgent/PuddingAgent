using System.Buffers.Binary;

namespace PuddingBrowser.Automation;

/// <summary>PNG/JPEG capture header facts. Bounded byte parsing only; never trusts declared MIME or planned size.</summary>
public sealed record ScreenshotImageHeader(string MimeType, int Width, int Height)
{
    public static ScreenshotImageHeader? Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 33 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) == 13 && bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return Create("image/png", BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]),
                BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]));
        }
        if (bytes.Length < 4 || bytes[0] != 255 || bytes[1] != 216) return null;
        for (var offset = 2; offset < bytes.Length;)
        {
            if (bytes[offset++] != 255) return null;
            while (offset < bytes.Length && bytes[offset] == 255) offset++;
            if (offset >= bytes.Length) return null;
            var marker = bytes[offset++];
            if (marker is 0xD9 or 0xDA) return null;
            if (marker is 0x01 or >= 0xD0 and <= 0xD8) continue;
            if (offset + 2 > bytes.Length) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (length < 2 || offset + length > bytes.Length) return null;
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (length < 8) return null;
                return Create("image/jpeg", BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]));
            }
            offset += length;
        }
        return null;
    }

    private static ScreenshotImageHeader? Create(string mime, uint width, uint height) =>
        width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue
            ? new(mime, (int)width, (int)height) : null;
}
