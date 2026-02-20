namespace PuddingCode.Models;

/// <summary>
/// 当前打开的项目上下文。所有工具操作和 Agent 感知以此为根。
/// </summary>
public sealed class ProjectContext
{
    /// <summary>项目根目录的绝对路径。</summary>
    public string RootPath { get; }

    /// <summary>项目显示名称（目录名）。</summary>
    public string Name { get; }

    public ProjectContext(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
        Name = Path.GetFileName(RootPath) ?? RootPath;
    }

    /// <summary>将相对路径解析为项目内的绝对路径。</summary>
    public string Resolve(string relativePath) =>
        Path.GetFullPath(relativePath, RootPath);

    /// <summary>检查路径是否在项目根目录内。</summary>
    public bool Contains(string absolutePath) =>
        Path.GetFullPath(absolutePath).StartsWith(RootPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>获取相对于项目根的路径。</summary>
    public string GetRelativePath(string absolutePath) =>
        Path.GetRelativePath(RootPath, absolutePath);

    /// <summary>从当前工作目录创建 ProjectContext。</summary>
    public static ProjectContext FromCurrentDirectory() =>
        new(Environment.CurrentDirectory);
}
