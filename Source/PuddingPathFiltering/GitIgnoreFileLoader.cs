using System.Text;

namespace PuddingPathFiltering;

/// <summary>
/// 叶子里<b>唯一</b>接触文件系统的类型：把一棵目录树下所有 <c>.gitignore</c> 读成 <see cref="IgnoreStack"/>。
/// 决策逻辑全部在纯类型中（<see cref="IgnoreFileParser"/> / <see cref="IgnoreStack"/> / <see cref="GitWildcard"/>），
/// 本类型不做任何判定 —— 这是 ADR-089 U4-4 D1「纯逻辑、无 I/O」的落地方式：
/// 判定链零 I/O，只留一个只读适配器；若把适配器放到某个消费者里，其它消费者必然复制一份（违反 D4 的单一真源）。
/// <para>
/// 载入顺序 = 广度优先（深度升序）⇒ 越深的 .gitignore 优先级越高，与 git 一致。
/// 噪声目录（<see cref="PathNoiseRules"/>）与 <c>.git</c> 不进入 —— 它们下面的规则不可能影响判定
/// （规则的基准目录限定了它只能命中自己子树内的路径，而该子树整体已被名字级规则排除）。
/// </para>
/// </summary>
public static class GitIgnoreFileLoader
{
    /// <summary>.gitignore 的文件名。</summary>
    public const string GitIgnoreFileName = ".gitignore";

    /// <summary>读取一棵目录树下的全部 <c>.gitignore</c>，构造忽略栈。</summary>
    /// <param name="rootDirectory">忽略语义的根（通常是 git 仓库根）。</param>
    /// <param name="ignoreCase">是否大小写折叠（Windows 上 git 的 <c>core.ignoreCase</c> 默认为 true）。</param>
    /// <param name="walkScopeDirectory">
    /// 只从该子目录（含）往下收集 .gitignore，但其基准目录仍相对 <paramref name="rootDirectory"/>。
    /// 语义：只让「落在被扫描树之内」的 .gitignore 生效 —— 于是把索引范围登记在一个被仓库
    /// .gitignore 忽略的目录（如 <c>temp/</c>）里时，<b>不会</b>因为祖先规则而被整体清空。
    /// 传 <c>null</c> 表示从 <paramref name="rootDirectory"/> 开始。
    /// </param>
    public static IgnoreStack Load(string rootDirectory, bool ignoreCase = false, string? walkScopeDirectory = null)
        => LoadWithSources(rootDirectory, ignoreCase, walkScopeDirectory).Stack;

    /// <summary>同 <see cref="Load"/>，并返回实际读到哪些文件（诊断 / 证据用）。</summary>
    public static (IgnoreStack Stack, IReadOnlyList<string> Sources) LoadWithSources(
        string rootDirectory,
        bool ignoreCase = false,
        string? walkScopeDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var rules = new List<IgnoreRule>();
        var sources = new List<string>();

        if (!Directory.Exists(root))
            return (new IgnoreStack(rules), sources);

        var start = string.IsNullOrWhiteSpace(walkScopeDirectory)
            ? root
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(walkScopeDirectory));

        if (!Directory.Exists(start))
            return (new IgnoreStack(rules), sources);

        var startRelative = start.Length > root.Length ? start[(root.Length + 1)..].Replace('\\', '/') : string.Empty;

        var pending = new Queue<(string Directory, string Relative)>();
        pending.Enqueue((start, startRelative));

        while (pending.TryDequeue(out var current))
        {
            var gitIgnorePath = Path.Combine(current.Directory, GitIgnoreFileName);
            if (File.Exists(gitIgnorePath))
            {
                rules.AddRange(IgnoreFileParser.Parse(
                    DisplayPath(current.Relative),
                    current.Relative,
                    ReadLines(gitIgnorePath),
                    ignoreCase));
                sources.Add(DisplayPath(current.Relative));
            }

            foreach (var child in EnumerateDirectories(current.Directory))
            {
                var name = Path.GetFileName(child);
                if (PathNoiseRules.DirectoryNames.Contains(name))
                    continue;

                var relative = current.Relative.Length == 0 ? name : $"{current.Relative}/{name}";
                pending.Enqueue((child, relative));
            }
        }

        return (new IgnoreStack(rules), sources);
    }

    private static string DisplayPath(string relativeDirectory)
        => relativeDirectory.Length == 0 ? GitIgnoreFileName : $"{relativeDirectory}/{GitIgnoreFileName}";

    private static IEnumerable<string> ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path, Encoding.UTF8);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory)
                .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
