namespace PuddingPlatformTests.Services;

/// <summary>ADR-077 存储合同测试用的最小合法图片字节（magic bytes/尺寸嗅探可通过）。</summary>
public static class VisionTestImages
{
    /// <summary>1×1 透明 PNG（67 字节，IHDR/IDAT/IEND 完整）。</summary>
    public static byte[] MinimalPng()
    {
        var base64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
        return Convert.FromBase64String(base64);
    }

    /// <summary>构造指定尺寸的 PNG IHDR 头部字节（无需完整图像，嗅探只读头部）。</summary>
    public static byte[] PngHeader(int width, int height)
    {
        var bytes = new byte[24];
        // PNG signature
        bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4E; bytes[3] = 0x47;
        bytes[4] = 0x0D; bytes[5] = 0x0A; bytes[6] = 0x1A; bytes[7] = 0x0A;
        // IHDR length = 13
        bytes[8] = 0; bytes[9] = 0; bytes[10] = 0; bytes[11] = 13;
        // "IHDR"
        bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';
        // width / height big-endian
        bytes[16] = (byte)(width >> 24); bytes[17] = (byte)(width >> 16);
        bytes[18] = (byte)(width >> 8); bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24); bytes[21] = (byte)(height >> 16);
        bytes[22] = (byte)(height >> 8); bytes[23] = (byte)height;
        return bytes;
    }
}
