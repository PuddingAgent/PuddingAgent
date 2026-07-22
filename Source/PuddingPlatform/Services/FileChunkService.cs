using System.Text;

namespace PuddingPlatform.Services;

/// <summary>
/// 大文件滑动窗口分块读取服务。
/// FileReadTool / FilePatchTool 在面对大文件（>2000 行 / >100KB）时，
/// 通过本服务以流式方式统计行数、按行窗口读取内容，避免 File.ReadAllText 全量加载
/// 以及 FindReplacementCandidates 的 O(n) 全量扫描和多次字符归一化造成的内存/CPU 浪费。
/// 本服务无状态，注册为 Singleton 安全。
/// </summary>
public sealed class FileChunkService
{
    /// <summary>大文件行数阈值。超过此值视为大文件。</summary>
    public const int LargeFileLineThreshold = 2000;

    /// <summary>大文件字节阈值。低于此值直接视为小文件（快速路径，无需统计行数）。</summary>
    public const int LargeFileByteThreshold = 100_000;

    /// <summary>
    /// 滑动窗口读取：返回从 <paramref name="offsetLines"/>（0-based）开始、
    /// 最多 <paramref name="limitLines"/> 行的内容。使用 StreamReader 逐行读取，
    /// 不将整个文件加载进内存。
    /// </summary>
    public async Task<string> ReadChunkAsync(
        string path, int offsetLines, int limitLines, CancellationToken ct = default)
    {
        if (offsetLines < 0) offsetLines = 0;
        if (limitLines <= 0) return string.Empty;

        var sb = new StringBuilder();
        using var reader = new StreamReader(path, Encoding.UTF8);
        var currentLine = 0;
        var emitted = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (currentLine >= offsetLines)
            {
                if (emitted > 0) sb.Append('\n');
                sb.Append(line);
                emitted++;
                if (emitted >= limitLines) break;
            }
            currentLine++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// 低开销统计文件总行数：流式扫描换行符，不把全部内容加载进内存。
    /// 约定：行数 = 换行符数量 + 1（与 FileReadTool 的既有 META 计算保持一致）。
    /// </summary>
    public async Task<int> CountLinesAsync(string path, CancellationToken ct = default)
    {
        var newlines = 0;
        using var reader = new StreamReader(path, Encoding.UTF8);
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadBlockAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '\n') newlines++;
            }
            ct.ThrowIfCancellationRequested();
        }

        return newlines + 1;
    }

    /// <summary>
    /// 返回第 <paramref name="oneBasedLine"/> 行（1-based）起始处的字符偏移量，
    /// 用于将“行范围”换算为 FindReplacementCandidates 所需的“字符范围”。
    /// 例如 GetLineStartOffsetAsync(path, 2001) 返回前 2000 行之后的字符偏移，
    /// 即“前 2000 行”窗口的排他上界。
    /// </summary>
    public async Task<int> GetLineStartOffsetAsync(string path, int oneBasedLine, CancellationToken ct = default)
    {
        if (oneBasedLine <= 1) return 0;
        var newlinesToSkip = oneBasedLine - 1;

        using var reader = new StreamReader(path, Encoding.UTF8);
        var buffer = new char[8192];
        var consumed = 0;
        var seen = 0;
        int read;
        while ((read = await reader.ReadBlockAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '\n')
                {
                    seen++;
                    if (seen == newlinesToSkip)
                        return consumed + i + 1;
                }
            }
            consumed += read;
            ct.ThrowIfCancellationRequested();
        }

        return consumed;
    }
}
