using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PuddingDesktop.Foundation;

/// <summary>
/// 烧录图（原图 + 笔迹）的保留策略。
///
/// <para>
/// 每次点「添加到对话」都会在 <c>{DataRoot}/panel-marks</c> 生成一张 JPEG。
/// 没有清理策略时这个目录会**只增不减** —— 用户在会话里改几十轮就是几十张图，
/// 而它们只是中间产物（真资产是制成品库）。
/// </para>
///
/// <para>
/// 只按数量保留最近 <paramref name="keep"/> 张，并且**只认本功能自己的文件名**
/// （<c>panel-marked-*.jpg</c>）：用户往这个目录里自己放的东西一律不动。
/// 清理失败（文件被占用等）时静默跳过 —— 保存动作不该因为收拾垃圾而失败。
/// </para>
/// </summary>
public static class PanelMarkRetention
{
    /// <summary>默认保留张数。够回看最近几轮，又不至于把目录撑大。</summary>
    public const int DefaultKeep = 50;

    /// <summary>本功能的文件名模式。前缀定死，避免误删同目录里别人的文件。</summary>
    private const string Pattern = "panel-marked-*.jpg";

    /// <summary>按修改时间保留最新 <paramref name="keep"/> 张，删除其余的；返回删除数。</summary>
    /// <param name="protect">
    /// 受保护的文件（通常就是刚写入的那一张），永远不参与淘汰。
    /// 必要原因：排序依据是文件系统 mtime，而时钟回拨、或某些文件系统/拷贝操作
    /// 会保留旧 mtime ⇒ 新写的文件可能被排成“最旧”，被当成垃圾删掉。
    /// </param>
    public static int Prune(string? directory, int keep = DefaultKeep, string? protect = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || keep < 0)
        {
            return 0;
        }

        if (!Directory.Exists(directory))
        {
            return 0;
        }

        List<FileInfo> doomed;
        try
        {
            doomed = Directory
                .EnumerateFiles(directory, Pattern, SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(info => !IsSameFile(info.FullName, protect))
                // 用最后写入时间排序：文件名带时间戳，但用户可能拷来旧图，
                // 按文件系统时间更贴近"最近用过的那几张"。
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Skip(keep)
                .ToList();
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }

        var removed = 0;
        foreach (var file in doomed)
        {
            try
            {
                file.Delete();
                removed++;
            }
            catch (IOException)
            {
                // 正被别的进程占用：下次再说。
            }
            catch (UnauthorizedAccessException)
            {
                // 没权限：如实跳过，不假装删掉了。
            }
        }

        return removed;
    }

    /// <summary>比较两个路径是否指向同一个文件（不追软链接，够用且不会因链环报错）。</summary>
    private static bool IsSameFile(string path, string? protect)
    {
        if (string.IsNullOrWhiteSpace(protect))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(path), Path.GetFullPath(protect), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // 非法路径（保留字符等）：当作不匹配，宁可少删也不误删。
            return false;
        }
    }
}
