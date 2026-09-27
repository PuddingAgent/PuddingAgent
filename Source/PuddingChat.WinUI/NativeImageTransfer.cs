using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace PuddingChat.WinUI;

/// <summary>Reads a user-initiated paste/drop. Core remains responsible for canonical image validation.</summary>
public static class NativeImageTransfer
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };
    public static bool ContainsImages(DataPackageView data) =>
        data.Contains(StandardDataFormats.StorageItems) || data.Contains(StandardDataFormats.Bitmap);

    public static async Task ReadAsync(DataPackageView data, Func<IReadOnlyList<string>, Task> import, CancellationToken ct)
    {
        if (data.Contains(StandardDataFormats.StorageItems))
        {
            var items = await data.GetStorageItemsAsync().AsTask(ct);
            if (items.Count == 0 || items.Any(i => i is not StorageFile || !Extensions.Contains(Path.GetExtension(i.Path))))
                throw new ArgumentException("请粘贴或拖入 PNG、JPEG、WebP、GIF 或 BMP 图片文件。");
            await import(items.Select(i => i.Path).ToArray());
            return;
        }
        if (!data.Contains(StandardDataFormats.Bitmap)) throw new ArgumentException("当前内容不包含图片。");
        var reference = await data.GetBitmapAsync().AsTask(ct);
        using var stream = await reference.OpenReadAsync().AsTask(ct);
        if (stream.Size is 0 or > 64UL * 1024 * 1024) throw new ArgumentException("图片为空或超过 64 MiB。");
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
        var extension = decoder.DecoderInformation.FileExtensions.FirstOrDefault(Extensions.Contains)
            ?? throw new ArgumentException("剪贴板图片格式暂不支持。");
        // Preserve original encoded bytes; no thumbnail or lossy re-encoding is submitted to Core.
        var path = Path.Combine(Path.GetTempPath(), $"pudding-paste-{Guid.NewGuid():N}{extension}");
        try
        {
            stream.Seek(0);
            using (var source = stream.AsStreamForRead())
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await source.CopyToAsync(target, ct);
            ct.ThrowIfCancellationRequested();
            await import([path]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
