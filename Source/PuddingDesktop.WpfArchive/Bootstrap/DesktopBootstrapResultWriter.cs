using System.Text.Json;

namespace PuddingDesktop.Bootstrap;

/// <summary>
/// Writes the bootstrap result file (&lt;SignalPath&gt;.result.json) with UTF-8 JSON.
/// </summary>
internal static class DesktopBootstrapResultWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task WriteAsync(
        string resultPath,
        DesktopBootstrapResult result,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultPath);
        ArgumentNullException.ThrowIfNull(result);

        var directory = Path.GetDirectoryName(resultPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using (var stream = new FileStream(
            resultPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            options: FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, result, WriteOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
    }
}
