using System.Text;

namespace Pudding.DesktopService;

/// <summary>
/// Desktop 侧文档预览的**路径解析与读取**：工作区相对路径 → 可读的绝对路径 → 文本。
///
/// <para>
/// 为什么 Core 已经校验过这里还要再校验一次：Desktop 不能假设上游一定对 ——
/// 另一条传输、旧版本 Core、被人为改过的请求都可能到这里。各守自己的边界，
/// 任一方失效都不穿透（与 <c>DocumentPathPolicy</c> 的并存不是复制，是纵深防御）。
/// </para>
/// <para>
/// 与 Core 策略的**分工**（为什么不共用一份白名单）：Core 决定"能访问哪些工作区文件"
/// （含 pdf/xlsx 等），Desktop 决定"自己能渲染哪些"（目前只有 Markdown 与纯文本）。
/// 把两份合并会让"新增一种 Core 能读的格式"被迫同时变成"Desktop 能渲染"。
/// </para>
/// </summary>
public sealed class DesktopDocumentWorkspace
{
    /// <summary>单次预览读取的字符上限；超出即截断并如实标注。</summary>
    public const int MaxPreviewCharacters = 200_000;

    private static readonly string[] AllowedExtensions = [".md", ".markdown", ".txt"];

    private readonly string _rootDirectory;

    /// <summary>
    /// <paramref name="rootDirectory"/> 必须是**已归一化的绝对目录**；
    /// 相对路径与不存在目录在构造期就失败（配错了根目录不该等到用户点预览才暴露）。
    /// </summary>
    public DesktopDocumentWorkspace(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("Workspace root must be an absolute path.", nameof(rootDirectory));
        }

        // 末尾补分隔符，使 StartsWith 判断不会把 C:\work 与 C:\work-other 混为一谈。
        var trimmed = Path.TrimEndingDirectorySeparator(rootDirectory);
        _rootDirectory = trimmed + Path.DirectorySeparatorChar;
    }

    public string RootDirectory => _rootDirectory;

    /// <summary>可渲染的扩展名（供错误提示与文档使用）。</summary>
    public static IReadOnlyList<string> SupportedExtensions => AllowedExtensions;

    /// <summary>
    /// 把工作区相对路径解析成绝对路径。**任何可疑形态都拒绝**，并给出可区分的原因，
    /// 让上层能如实说明"为什么不行"而不是一句含糊的"路径无效"。
    /// </summary>
    public bool TryResolve(
        string? relativePath,
        out string? fullPath,
        out string? errorCode,
        out string? errorMessage)
    {
        fullPath = null;
        errorCode = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            errorCode = "path_empty";
            errorMessage = "path 不能为空";
            return false;
        }

        var text = relativePath.Trim().Replace('\\', '/');

        if (text.Any(char.IsControl))
        {
            errorCode = "path_invalid";
            errorMessage = "path 不能包含控制字符";
            return false;
        }

        if (text.StartsWith('/'))
        {
            errorCode = "path_absolute";
            errorMessage = "只接受工作区相对路径，不允许以 / 开头";
            return false;
        }

        // 盘符（C:）与备用数据流（file.md::$DATA）都含冒号，一律拒绝。
        if (text.Contains(':'))
        {
            errorCode = "path_absolute";
            errorMessage = "只接受工作区相对路径，不允许盘符或数据流写法";
            return false;
        }

        // 逐段检查：.. 与空段（a//b）都拒绝；. 直接忽略。
        var segments = new List<string>();
        foreach (var segment in text.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                errorCode = "path_traversal";
                errorMessage = "不允许路径遍历（..）";
                return false;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            errorCode = "path_empty";
            errorMessage = "path 没有指向任何文件";
            return false;
        }

        var extension = Path.GetExtension(segments[^1]).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension, StringComparer.Ordinal))
        {
            errorCode = "path_unsupported_extension";
            errorMessage = $"Desktop 只能预览 {string.Join(" / ", AllowedExtensions)}";
            return false;
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(_rootDirectory, Path.Combine([.. segments])));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errorCode = "path_invalid";
            errorMessage = "path 无法解析成合法文件路径";
            return false;
        }

        // 最后一道闸：解析结果必须仍在根目录内（防符号链接/大小写/分隔符绕过）。
        if (!candidate.StartsWith(_rootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            errorCode = "path_outside_workspace";
            errorMessage = "解析后的路径不在工作区内";
            return false;
        }

        fullPath = candidate;
        return true;
    }

    /// <summary>
    /// 读取文本；超过预算即截断并标注。**不是错误**：预览本来就不承诺全文。
    /// </summary>
    public bool TryReadText(
        string fullPath,
        out string? text,
        out bool truncated,
        out string? errorCode,
        out string? errorMessage)
    {
        text = null;
        truncated = false;
        errorCode = null;
        errorMessage = null;

        if (!File.Exists(fullPath))
        {
            errorCode = "file_not_found";
            errorMessage = "文件不存在";
            return false;
        }

        try
        {
            using var stream = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var buffer = new char[MaxPreviewCharacters];
            var read = reader.Read(buffer, 0, buffer.Length);
            truncated = reader.Peek() >= 0;
            text = new string(buffer, 0, read);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 只带异常类型名：异常消息可能包含绝对路径（用户的目录结构）。
            errorCode = "file_unreadable";
            errorMessage = $"文件无法读取（{ex.GetType().Name}）";
            return false;
        }
    }
}
