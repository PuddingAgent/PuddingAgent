using System.Text;

namespace PuddingChat;

/// <summary>Immutable user-selected context. The source path is descriptive, never re-opened on send.</summary>
public sealed record TextFileContext(string Id, string Name, string SourcePath, string Text, int ByteCount);

/// <summary>Bounded, lossless local text import. Binary documents need a separate extraction capability.</summary>
public static class TextFileContexts
{
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxTotalBytes = 512 * 1024;
    public const int MaxFiles = 8;
    // Mirrors the existing Core text-part contract; the Composition integration test verifies this ceiling.
    public const int MaxMessageCharacters = 100_000;

    public static async Task<TextFileContext> ReadAsync(string path, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择文件的完整路径。", nameof(path));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadAsync(path, stream, ct);
    }

    // Stream overload keeps decoding and limits independently testable, including files growing during reads.
    public static async Task<TextFileContext> ReadAsync(string path, Stream source, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择文件的完整路径。", nameof(path));
        var bytes = new byte[MaxFileBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await source.ReadAsync(bytes.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
        }
        ct.ThrowIfCancellationRequested();
        if (length > MaxFileBytes) throw new ArgumentException("文本文件超过 256 KiB，请选择较小文件或相关片段。");
        var offset = 0;
        Encoding encoding = new UTF8Encoding(false, true);
        if (length >= 4 && ((bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
            || (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)))
            throw new ArgumentException("暂不支持 UTF-32，请将文件转换为 UTF-8。");
        if (length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) offset = 3;
        else if (length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { encoding = new UnicodeEncoding(false, true, true); offset = 2; }
        else if (length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { encoding = new UnicodeEncoding(true, true, true); offset = 2; }
        string text;
        try { text = encoding.GetString(bytes, offset, length - offset); }
        catch (DecoderFallbackException e) { throw new ArgumentException("文件不是有效的 UTF-8 或带 BOM 的 UTF-16 文本，未添加。", e); }
        if (text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw new ArgumentException("文件包含二进制控制字符，无法作为文本上下文添加。");
        return new TextFileContext(Guid.NewGuid().ToString("N"), Path.GetFileName(path), path, text, length);
    }

    public static void Validate(IReadOnlyList<TextFileContext> files)
    {
        if (files.Count > MaxFiles) throw new ArgumentException("每条消息最多添加 8 个文本文件。");
        if (files.Any(f => f.ByteCount < 0 || f.ByteCount > MaxFileBytes || f.Text.Length > MaxFileBytes)
            || files.Sum(f => (long)f.ByteCount) > MaxTotalBytes)
            throw new ArgumentException("文本附件总大小不能超过 512 KiB，单文件不能超过 256 KiB。");
        if (files.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != files.Count)
            throw new ArgumentException("同一附件不能重复提交。");
    }

    /// <summary>Build once when preparing a pending send; retry uses this exact snapshot.</summary>
    public static string Compose(string draft, IReadOnlyList<TextFileContext> files)
    {
        Validate(files);
        if (files.Count == 0) return draft;
        var result = new StringBuilder(draft);
        foreach (var file in files)
        {
            // A longer fence prevents file contents containing Markdown fences from closing the context block.
            var longest = 0; var current = 0;
            foreach (var c in file.Text) { current = c == '`' ? current + 1 : 0; longest = Math.Max(longest, current); }
            var fence = new string('`', Math.Max(3, longest + 1));
            result.Append("\n\n附件（导入时的文本快照）：")
                .Append(System.Text.Json.JsonSerializer.Serialize(file.SourcePath))
                .Append('\n').Append(fence).Append("text\n").Append(file.Text)
                .Append('\n').Append(fence);
            if (result.Length > MaxMessageCharacters)
                throw new ArgumentException("消息与文本附件合计超过 100,000 字符，请缩小文件或减少附件。");
        }
        return result.ToString();
    }
}
