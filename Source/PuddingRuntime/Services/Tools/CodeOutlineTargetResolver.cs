namespace PuddingRuntime.Services.Tools;

/// <summary>
/// <c>code_outline</c> 的输入解析结果（路径解析 + 存在性 + 扩展名）。
/// </summary>
/// <param name="FullPath">规范化后的绝对路径（读文件用这个）。</param>
/// <param name="DisplayPath">用户原样给出的路径（回显与错误信息用这个，不要换成绝对路径）。</param>
/// <param name="Extension">小写扩展名，含前导点；无扩展名时为空串。</param>
public sealed record CodeOutlineTarget(string FullPath, string DisplayPath, string Extension);

/// <summary>
/// 把 <c>code_outline</c> 的入参（路径 + 工作目录）解析成可用的目标。
/// <para>
/// <b>为什么单独抽出来</b>：这段逻辑原本躺在 <c>CodeOutlineTool.ExecuteCoreAsync</c> 方法体开头，
/// 而即将落地的 <c>workspace_open</c> 需要<b>一模一样</b>的路径语义。两份拷贝必然漂移，
/// 所以先把它变成一个可独立测试的小件。
/// </para>
/// <para>
/// <b>行为说明（与旧代码逐字一致）</b>：
/// 相对路径以 <paramref name="workingDirectory"/>（缺省为进程当前目录）为基准；
/// 绝对路径原样规范化。<b>不做工作区子树校验</b> —— 旧实现的 XML 注释曾声称
/// 「校验其在工作区范围内」，但方法体只做了 <c>GetFullPath</c> 组合，没有任何校验。
/// 这里如实描述实际行为，不把注释当成安全承诺；是否要加沙箱边界属于单独的决定。
/// </para>
/// </summary>
public static class CodeOutlineTargetResolver
{
    /// <summary>
    /// 解析目标。失败时返回 <c>null</c> 并通过 <paramref name="error"/> 给出<b>可直接返回给调用方</b>的错误文本。
    /// </summary>
    public static CodeOutlineTarget? Resolve(
        string? filePath,
        string? workingDirectory,
        out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            error = "file_path is required.";
            return null;
        }

        var displayPath = filePath.Trim();
        var cwd = Path.GetFullPath(workingDirectory ?? Directory.GetCurrentDirectory());

        var fullPath = Path.IsPathRooted(displayPath)
            ? Path.GetFullPath(displayPath)
            : Path.GetFullPath(Path.Combine(cwd, displayPath));

        if (!File.Exists(fullPath))
        {
            error = $"(文件不存在: {displayPath})";
            return null;
        }

        return new CodeOutlineTarget(fullPath, displayPath, Path.GetExtension(fullPath).ToLowerInvariant());
    }
}
