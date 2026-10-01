namespace Pudding.Contracts.Desktop;

/// <summary>
/// 文件选择器请求。
///
/// <b>诚实语义</b>：返回的是**用户的选择**，不代表 Core 自动获得该路径的读取权——
/// 权限仍受进程身份与文件系统 ACL 约束。因此调用方拿到路径后仍须按普通文件访问处理失败。
/// </summary>
public sealed record DesktopFilePickerRequest
{
    public const int MaxTitleLength = 128;

    public const int MaxExtensions = 16;

    public DesktopFilePickerRequest(
        string title,
        bool allowMultiple = false,
        IReadOnlyList<string>? extensions = null)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > MaxTitleLength)
        {
            throw new ArgumentException($"Title must be 1..{MaxTitleLength} characters.", nameof(title));
        }

        if (extensions is { Count: > MaxExtensions })
        {
            throw new ArgumentException($"At most {MaxExtensions} extensions are supported.", nameof(extensions));
        }

        var normalized = new List<string>();
        foreach (var extension in extensions ?? [])
        {
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 16 || extension.Contains('*', StringComparison.Ordinal))
            {
                throw new ArgumentException("Extensions must be plain suffixes like 'pdf' without wildcards.", nameof(extensions));
            }

            normalized.Add(extension.TrimStart('.'));
        }

        Title = title;
        AllowMultiple = allowMultiple;
        Extensions = normalized;
    }

    public string Title { get; }

    public bool AllowMultiple { get; }

    /// <summary>可接受的扩展名（不含点号、不含通配符）；空表示不过滤。</summary>
    public IReadOnlyList<string> Extensions { get; }

    public override string ToString() =>
        $"file_picker(multi={AllowMultiple}, extensions={Extensions.Count}, titleChars={Title.Length})";
}

/// <summary>
/// 文件选择结果。**取消不是失败**（同对话框）；未选择任何文件时 <see cref="Paths"/> 为空。
/// </summary>
public sealed record DesktopFilePickerResult
{
    public DesktopFilePickerResult(bool canceled, IReadOnlyList<string>? paths = null)
    {
        var selected = new List<string>();
        foreach (var path in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Selected paths must be non-empty.", nameof(paths));
            }

            selected.Add(path);
        }

        // 取消与"选了文件"是互斥的：同时成立只会让上层无法判断，因此归一为取消 + 无选择。
        if (canceled && selected.Count > 0)
        {
            selected.Clear();
        }

        Canceled = canceled;
        Paths = selected;
    }

    public bool Canceled { get; }

    /// <summary>用户选中的绝对路径（可能为空）。**不代表 Core 一定可读**。</summary>
    public IReadOnlyList<string> Paths { get; }

    public bool HasSelection => Paths.Count > 0;

    /// <summary>只给形状：路径属于用户隐私，不进日志/审计。</summary>
    public override string ToString() =>
        $"file_picker(canceled={Canceled}, count={Paths.Count})";
}