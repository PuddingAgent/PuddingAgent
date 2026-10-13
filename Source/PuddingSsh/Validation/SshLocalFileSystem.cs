namespace PuddingSsh.Validation;

/// <summary>
/// 本地文件系统探测（切片 B）：把 reparse point / 普通文件判定抽成接口，
/// 让路径策略可以**确定性单测**（真实文件系统路径另由集成测试与探针覆盖）。
/// </summary>
public interface ISshLocalFileSystem
{
    /// <summary>路径是否存在（文件或目录）。</summary>
    bool Exists(string path);

    /// <summary>是否为目录。</summary>
    bool IsDirectory(string path);

    /// <summary>是否为普通文件。</summary>
    bool IsRegularFile(string path);

    /// <summary>是否为重解析点（Windows 符号链接/junction、Unix 符号链接）。</summary>
    bool IsReparsePoint(string path);

    /// <summary>文件字节数（调用方已确认是普通文件）。</summary>
    long GetLength(string path);

    /// <summary>取绝对路径（不做存在性要求）。</summary>
    string GetFullPath(string path);
}

/// <summary>真实文件系统实现。</summary>
public sealed class SshLocalFileSystem : ISshLocalFileSystem
{
    public static SshLocalFileSystem Instance { get; } = new();

    public bool Exists(string path)
    {
        try
        {
            _ = System.IO.File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is System.IO.FileNotFoundException or System.IO.DirectoryNotFoundException)
        {
            return false;
        }
    }

    public bool IsDirectory(string path)
    {
        try
        {
            return System.IO.File.GetAttributes(path).HasFlag(System.IO.FileAttributes.Directory);
        }
        catch (Exception ex) when (ex is System.IO.FileNotFoundException or System.IO.DirectoryNotFoundException)
        {
            return false;
        }
    }

    public bool IsRegularFile(string path)
    {
        try
        {
            var attributes = System.IO.File.GetAttributes(path);
            return !attributes.HasFlag(System.IO.FileAttributes.Directory);
        }
        catch (Exception ex) when (ex is System.IO.FileNotFoundException or System.IO.DirectoryNotFoundException)
        {
            return false;
        }
    }

    public bool IsReparsePoint(string path)
    {
        try
        {
            return System.IO.File.GetAttributes(path).HasFlag(System.IO.FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is System.IO.FileNotFoundException or System.IO.DirectoryNotFoundException)
        {
            return false;
        }
    }

    public long GetLength(string path) => new System.IO.FileInfo(path).Length;

    public string GetFullPath(string path) => System.IO.Path.GetFullPath(path);
}
