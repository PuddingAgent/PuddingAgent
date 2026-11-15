using PuddingPathFiltering;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 噪声目录判定的单点实现（ADR-089 U4-4 D4 的延伸）。
/// <para>
/// 背景：<c>list_dir</c> 与 <c>search_grep</c> 默认排除 bin/obj/node_modules/.git 等目录，
/// 而 <c>file_search</c> 此前完全不排除 —— 同一目录不同工具口径不一，模型拿到的清单会混入
/// 构建产物副本（2026-11-15 实测：<c>Source</c> 下 198 个 .csproj 里有 2 个在 bin 下，
/// 一个模型自行标注剔除了，另一个直接混进交付清单）。
/// </para>
/// <para>
/// 语义要点：<b>只判「搜索根之下」的路径段</b>。搜索根本身就在 bin 里时不得把整棵树判成噪声，
/// 否则显式指定 directory=.../bin 会得到空结果 —— 那等于又造一个「拿不到全部信息」的洞。
/// 最后一段是文件名本身，不参与目录判定（<c>bin.csproj</c> 不是噪声目录）。
/// </para>
/// </summary>
internal static class FileNoisePathFilter
{
    /// <summary>
    /// 判断 <paramref name="path"/> 是否位于 <paramref name="searchRoot"/> 之下的噪声目录里。
    /// 无法归一化、或不在搜索根之下时一律返回 false（宁可不排除，也不要错杀）。
    /// </summary>
    public static bool IsUnderNoiseDirectory(string path, string searchRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(searchRoot))
            return false;

        string relative;
        try
        {
            relative = Path.GetRelativePath(Path.GetFullPath(searchRoot), Path.GetFullPath(path));
        }
        catch
        {
            return false;
        }

        if (relative.Length == 0 || relative == ".")
            return false;

        if (relative.StartsWith("..", StringComparison.Ordinal))
            return false;

        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        // 最后一段是文件名（或路径本身），不参与目录判定。
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (PathNoiseRules.DirectoryNames.Contains(segments[i]))
                return true;
        }

        return false;
    }
}
