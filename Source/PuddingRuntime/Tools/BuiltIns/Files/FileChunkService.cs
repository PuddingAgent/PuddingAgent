using System.Text;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Streams line-oriented windows from large files for Runtime file tools.
/// The service is stateless and safe to register as a singleton.
/// </summary>
public sealed class FileChunkService
{
    /// <summary>Files above this line count are considered large.</summary>
    public const int LargeFileLineThreshold = 2000;

    /// <summary>Files below this byte count can use the small-file fast path.</summary>
    public const int LargeFileByteThreshold = 100_000;

    /// <summary>Reads at most <paramref name="limitLines"/> lines from a zero-based line offset.</summary>
    public async Task<string> ReadChunkAsync(
        string path,
        int offsetLines,
        int limitLines,
        CancellationToken ct = default)
    {
        if (offsetLines < 0)
            offsetLines = 0;
        if (limitLines <= 0)
            return string.Empty;

        var result = new StringBuilder();
        using var reader = new StreamReader(path, Encoding.UTF8);
        var currentLine = 0;
        var emitted = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (currentLine >= offsetLines)
            {
                if (emitted > 0)
                    result.Append('\n');
                result.Append(line);
                emitted++;
                if (emitted >= limitLines)
                    break;
            }

            currentLine++;
        }

        return result.ToString();
    }

    /// <summary>Counts lines without loading the full file into memory.</summary>
    public async Task<int> CountLinesAsync(string path, CancellationToken ct = default)
    {
        var newlines = 0;
        var lastChar = '\0';
        using var reader = new StreamReader(path, Encoding.UTF8);
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadBlockAsync(buffer.AsMemory(0, buffer.Length), ct)
                   .ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '\n')
                    newlines++;
            }

            lastChar = buffer[read - 1];
            ct.ThrowIfCancellationRequested();
        }

        // A trailing newline terminates the last line rather than opening a new one:
        // "a\nb\nc\n" is 3 lines (newlines == 3), while "a\nb\nc" is 3 lines (newlines + 1).
        return lastChar == '\n' ? newlines : newlines + 1;
    }

    /// <summary>Returns the character offset at which a one-based line starts.</summary>
    public async Task<int> GetLineStartOffsetAsync(
        string path,
        int oneBasedLine,
        CancellationToken ct = default)
    {
        if (oneBasedLine <= 1)
            return 0;

        var newlinesToSkip = oneBasedLine - 1;
        using var reader = new StreamReader(path, Encoding.UTF8);
        var buffer = new char[8192];
        var consumed = 0;
        var seen = 0;
        int read;
        while ((read = await reader.ReadBlockAsync(buffer.AsMemory(0, buffer.Length), ct)
                   .ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != '\n')
                    continue;

                seen++;
                if (seen == newlinesToSkip)
                    return consumed + i + 1;
            }

            consumed += read;
            ct.ThrowIfCancellationRequested();
        }

        return consumed;
    }
}
