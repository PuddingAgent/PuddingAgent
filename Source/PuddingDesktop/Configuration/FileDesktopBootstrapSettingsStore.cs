using System.Text.Json;

namespace PuddingDesktop.Configuration;

/// <summary>
/// File-backed launcher settings stored outside DataRoot.
/// </summary>
public sealed class FileDesktopBootstrapSettingsStore : IDesktopBootstrapSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public async Task<DesktopBootstrapSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var path = DesktopBootstrapPathProvider.GetFilePath();
        if (!File.Exists(path))
            return new DesktopBootstrapSettings();

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<DesktopBootstrapSettings>(json, JsonOptions)
            ?? new DesktopBootstrapSettings();
    }

    public async Task SaveAsync(
        DesktopBootstrapSettings settings,
        CancellationToken cancellationToken)
    {
        var path = DesktopBootstrapPathProvider.GetFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tempPath = path + ".tmp";
        var backupPath = path + ".bak";
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
                File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
