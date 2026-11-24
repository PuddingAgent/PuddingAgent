using System;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 「文件 / 目录不存在」的诊断语补强。
/// <para>
/// <b>起因（2026-11-25 子代理失败归因普查）：</b><c>file_read</c> 26 条、<c>list_dir</c> 6 条失败的主因
/// 是路径拼错——多一层或少一层根目录（例：把 <c>D:\CodeProject\PuddingCore\…</c> 当成
/// 真实存在的 <c>…\PuddingAgent\PuddingAgent\Source\PuddingCore\…</c>）。原始消息只回显**输入**
/// 的路径，模型无法从「File not found: Source/x.cs」看出它被解析到了哪个根下面，
/// 于是一轮之内只能靠反复试错。
/// </para>
/// <para>
/// <b>边界：</b>只补充**已经发生的事实**——解析出的绝对路径、最深的已存在祖先、缺失层数。
/// 不猜「你本来想写什么」、不做相似度联想、不建议改写：那会把一次确定的失败
/// 变成一条看似权威的误导。
/// </para>
/// <para>
/// <b>失败安全：</b>诊断构造本身不得把「找不到」升级成异常。路径含非法字符等情况一律
/// 原样返回原消息。
/// </para>
/// </summary>
internal static class MissingPathDiagnostics
{
    /// <summary>
    /// 在原消息后追加解析细节。输入为空、解析与输入完全相同、或构造过程抛异常时**原样返回**，
    /// 因此调用方可以无条件调用，既有前缀（<c>File not found: …</c>）保持不变。
    /// </summary>
    /// <param name="message">原始失败消息，必须以输入路径开头/包含，调用方负责前缀。</param>
    /// <param name="input">用户（模型）传入的原始路径，可能为相对路径。</param>
    /// <param name="resolvedFullPath">工具实际检查的绝对路径。</param>
    public static string Append(string message, string input, string resolvedFullPath)
    {
        if (string.IsNullOrWhiteSpace(resolvedFullPath))
            return message;

        try
        {
            var parts = new System.Collections.Generic.List<string>();

            // 相对输入被解析过 —— 这是最常被误解的一条：模型以为相对的是仓库根，实际是工作区根。
            if (!string.IsNullOrWhiteSpace(input) && !Path.IsPathRooted(input))
                parts.Add($"resolved to {resolvedFullPath}");

            // 最深的已存在祖先：把「哪一层开始错了」直接摆出来。
            var ancestor = FindDeepestExistingAncestor(resolvedFullPath);
            if (ancestor is not null && !PathsEqual(ancestor, resolvedFullPath))
            {
                var missing = CountMissingLevels(ancestor, resolvedFullPath);
                parts.Add(missing > 0
                    ? $"deepest existing ancestor: {ancestor} ({missing} missing level(s))"
                    : $"deepest existing ancestor: {ancestor}");
            }

            if (parts.Count == 0)
                return message;

            return $"{message} ({string.Join("; ", parts)})";
        }
        catch (Exception)
        {
            // 诊断不能改变失败的性质。
            return message;
        }
    }

    private static string? FindDeepestExistingAncestor(string fullPath)
    {
        var current = fullPath;
        for (var guard = 0; guard < 64; guard++)
        {
            if (Directory.Exists(current) || File.Exists(current))
                return current;

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || PathsEqual(parent, current))
                return null;

            current = parent;
        }

        return null;
    }

    /// <summary>从现存祖先到目标路径之间还差几段。</summary>
    private static int CountMissingLevels(string ancestor, string fullPath)
    {
        var rel = Path.GetRelativePath(ancestor, fullPath);
        if (string.IsNullOrEmpty(rel) || rel == ".")
            return 0;

        var separators = 0;
        foreach (var c in rel)
        {
            if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar)
                separators++;
        }

        return separators + 1;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
