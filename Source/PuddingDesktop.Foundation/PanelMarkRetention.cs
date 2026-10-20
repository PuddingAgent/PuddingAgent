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
///
/// <para>
/// 淘汰对象**只有从未被引用过的图**：每张图插入对话时会写一个 <c>.used</c> sidecar，
/// 带这个标记就不在候选池里（见 <see cref="MarkUsed"/>）。这比“按时间猜”可靠 ——
/// 一张上周插入的图，时间上很旧，但消息里还引用着它。
/// </para>
/// </summary>
public static class PanelMarkRetention
{
    /// <summary>
    /// 默认保留张数。
    ///
    /// <para>
    /// 定得很大是**故意的**：现在还没有引用关系落盘（哪张图进了消息、哪张进了草稿），
    /// 所以任何擦除都是盲擦。在这个前提下，宁可多留 —— 一个跑很久的助手也就几十 MB，
    /// 而误删一张被消息引用的图的代价是链接永久断掉。等引用表落地后再往下调。
    /// </para>
    /// </summary>
    public const int DefaultKeep = 500;

    /// <summary>本功能的文件名模式。前缀定死，避免误删同目录里别人的文件。</summary>
    private const string Pattern = "panel-marked-*.jpg";

    /// <summary>
    /// 「已引用」标记的后缀。
    ///
    /// <para>
    /// 一个空文件就够了 —— 它回答的只是「这张图有没有被插进过对话」。
    /// 用 sidecar 而不是集中索引：少一个文件损坏就少丢一整批的保护信息，
    /// 而且删除图片时不会把索引弄成半完成状态。
    /// </para>
    /// </summary>
    public const string UsedSuffix = ".used";

    /// <summary>
    /// 给一张烧录图打「已引用」标记：它被插进过对话（消息里存的就是这个路径）。
    ///
    /// <para>
    /// 失败静默：打标记是尽力而为的事，不该让插入动作报错。
    /// </para>
    /// </summary>
    public static void MarkUsed(string? markPath)
    {
        if (string.IsNullOrWhiteSpace(markPath))
        {
            return;
        }

        try
        {
            File.WriteAllBytes(markPath + UsedSuffix, Array.Empty<byte>());
        }
        catch (IOException)
        {
            // 目录只读、文件被占用：下次插入再说。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    /// <summary>
    /// 是不是已经被引用过。**引用过的图永远不删**：
    /// 它已经在消息历史里，删掉就是永久断链。
    /// </summary>
    private static bool IsUsed(string path) => File.Exists(path + UsedSuffix);

    /// <summary>按修改时间保留最新 <paramref name="keep"/> 张，删除其余的；返回删除数。</summary>
    /// <param name="minAge">
    /// 只删「比这个还旧」的文件；不传则不限年龄。
    /// 为什么要它：昨天刚插入的图很可能还在对话里被引用，删它就是断链。
    /// 数量与年龄**同时**满足才删。
    /// </param>
    /// <param name="protect">
    /// 受保护的文件（通常就是刚写入的那一张），永远不参与淘汰。
    /// 必要原因：排序依据是文件系统 mtime，而时钟回拨、或某些文件系统/拷贝操作
    /// 会保留旧 mtime ⇒ 新写的文件可能被排成“最旧”，被当成垃圾删掉。
    /// </param>
    public static int Prune(
        string? directory,
        int keep = DefaultKeep,
        TimeSpan? minAge = null,
        string? protect = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || keep < 0)
        {
            return 0;
        }

        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var cutoff = minAge is { } age && age > TimeSpan.Zero
            ? DateTime.UtcNow - age
            : (DateTime?)null;

        List<FileInfo> doomed;
        try
        {
            doomed = Directory
                .EnumerateFiles(directory, Pattern, SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(info => !IsSameFile(info.FullName, protect))
                // 引用过的图不进候选池：它在消息历史里，删掉就是永久断链。
                .Where(info => !IsUsed(info.FullName))
                // 用最后写入时间排序：文件名带时间戳，但用户可能拷来旧图，
                // 按文件系统时间更贴近"最近用过的那几张"。
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Skip(keep)
                // 先按数量选出候选，再剔除“还很新”的：两个条件同时满足才删。
                .Where(info => cutoff is null || info.LastWriteTimeUtc < cutoff)
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

        // 顺手收拾自己留下的垃圾：图被用户（或别的工具）手动删掉后，
        // 它的 .used 标记会一直留在目录里。只清“图已经不在了”的标记，
        // 不动任何匹配不上的文件。
        removed += PruneOrphanMarkers(directory);

        return removed;
    }

    /// <summary>清掉图已经不存在的 <c>.used</c> 标记；返回清掉的数量。</summary>
    private static int PruneOrphanMarkers(string directory)
    {
        var cleaned = 0;
        try
        {
            foreach (var marker in Directory.EnumerateFiles(
                directory, Pattern + UsedSuffix, SearchOption.TopDirectoryOnly))
            {
                var image = marker[..^UsedSuffix.Length];
                if (File.Exists(image))
                {
                    continue;
                }

                try
                {
                    File.Delete(marker);
                    cleaned++;
                }
                catch (IOException)
                {
                    // 占着就先留着，下次再说。
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return cleaned;
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
